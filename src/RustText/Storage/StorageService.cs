using System.Security.Cryptography;
using System.Text.Json;
using RustText.Demo.Logs;

namespace RustText.Demo;

internal sealed class StorageService
{
    private const int MaxRecentFiles = 10_000;
    private const int MaxClosedTabs = 100;
    private const int MaximumCheckpointCount = 2;
    private readonly string _root;
    private readonly string _profileBaseDirectory;
    private readonly string _checkpoints;
    private readonly string _drafts;
    private readonly string _versions;
    private readonly bool _hasProfileOverride;
    private readonly SemaphoreSlim _recentGate = new(1, 1);
    private SessionState? _cachedSession;
    private bool _hasCachedSession;
    private bool _cachedSessionWasLatest;
    private string? _cachedLatestPointer;
    private string? _cachedPreviousPointer;
    private Dictionary<string, (long Length, long LastWriteTicks)> _cachedFiles = new(StringComparer.OrdinalIgnoreCase);

    private StorageService(string profileBaseDirectory, bool hasProfileOverride)
    {
        _profileBaseDirectory = Path.GetFullPath(profileBaseDirectory);
        _root = Path.Combine(_profileBaseDirectory, "dotnet-demo");
        _hasProfileOverride = hasProfileOverride;
        _checkpoints = Path.Combine(_root, "session");
        _drafts = Path.Combine(_root, "drafts");
        _versions = Path.Combine(_root, "versions");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_checkpoints);
        Directory.CreateDirectory(_drafts);
        Directory.CreateDirectory(_versions);
    }

    public static StorageService OpenDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("RUSTTEXT_PROFILE_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!Path.IsPathFullyQualified(overridePath)) throw new IOException("RUSTTEXT_PROFILE_DIR должен содержать абсолютный путь.");
            return new StorageService(Path.GetFullPath(overridePath), hasProfileOverride: true);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("Папка LOCALAPPDATA недоступна.");
        return new StorageService(Path.Combine(local, "RustText"), hasProfileOverride: false);
    }

    internal static StorageService OpenForTesting(string profileBaseDirectory) =>
        new(profileBaseDirectory, hasProfileOverride: false);

    public string RootDirectory => _root;
    public static string GetProfileBaseDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("RUSTTEXT_PROFILE_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!Path.IsPathFullyQualified(overridePath)) throw new IOException("RUSTTEXT_PROFILE_DIR должен содержать абсолютный путь.");
            return Path.GetFullPath(overridePath);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("Папка LOCALAPPDATA недоступна.");
        return Path.Combine(local, "RustText");
    }

    private void CacheSession(SessionState? state, bool isLatest)
    {
        _cachedSession = state;
        _hasCachedSession = true;
        _cachedSessionWasLatest = isLatest;
        var latest = Path.Combine(_checkpoints, "latest.json");
        var previous = Path.Combine(_checkpoints, "previous.json");
        _cachedLatestPointer = File.Exists(latest) ? File.ReadAllText(latest).Trim() : null;
        _cachedPreviousPointer = File.Exists(previous) ? File.ReadAllText(previous).Trim() : null;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pointer in new[] { _cachedLatestPointer, _cachedPreviousPointer })
        {
            if (pointer is null || Path.GetFileName(pointer) != pointer || !pointer.StartsWith("checkpoint-", StringComparison.Ordinal)) continue;
            files.Add(Path.Combine(_checkpoints, pointer, "manifest.json"));
        }
        if (state is not null)
        {
            foreach (var tab in (state.Tabs ?? []).Concat(state.ClosedTabs ?? []))
            {
                if (tab.DraftRef is null) continue;
                try { files.Add(ResolveAnyDraftReference(tab.DraftRef)); }
                catch (Exception error) when (IsInvalidCheckpointException(error)) { }
            }
        }
        _cachedFiles = files.ToDictionary(path => path, GetFileSignature, StringComparer.OrdinalIgnoreCase);
    }

    private bool CachedFilesAreUnchanged()
    {
        var latestPath = Path.Combine(_checkpoints, "latest.json");
        var previousPath = Path.Combine(_checkpoints, "previous.json");
        var latest = File.Exists(latestPath) ? File.ReadAllText(latestPath).Trim() : null;
        var previous = File.Exists(previousPath) ? File.ReadAllText(previousPath).Trim() : null;
        return string.Equals(latest, _cachedLatestPointer, StringComparison.Ordinal)
            && string.Equals(previous, _cachedPreviousPointer, StringComparison.Ordinal)
            && _cachedFiles.All(entry => GetFileSignature(entry.Key) == entry.Value);
    }

    private static (long Length, long LastWriteTicks) GetFileSignature(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, -1);
    }

    public async Task<SessionState?> LoadSessionAsync(CancellationToken cancellationToken = default,
        Func<SessionState, Task>? showPendingTabs = null)
    {
        var current = Path.Combine(_checkpoints, "latest.json");
        var backup = Path.Combine(_checkpoints, "previous.json");
        foreach (var (path, isLatest) in new[] { (current, true), (backup, false) })
        {
            if (await ReadCheckpointAsync(path, cancellationToken).ConfigureAwait(false) is { } state)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (showPendingTabs is not null) await showPendingTabs(state).ConfigureAwait(false);
                if (!await ValidateSessionDraftsAsync(state, cancellationToken).ConfigureAwait(false)) continue;
                CacheSession(state, isLatest);
                return state;
            }
        }
        var legacy = await LoadLegacySessionAsync(cancellationToken).ConfigureAwait(false);
        CacheSession(legacy, isLatest: false);
        return legacy;
    }

    public async Task<SessionState> SaveSessionAsync(SessionState state, IReadOnlyDictionary<long, string>? drafts = null, CancellationToken cancellationToken = default)
    {
        var readers = drafts?.ToDictionary(pair => pair.Key, pair => (Func<TextReader>)(() => new StringReader(pair.Value)));
        return await SaveSessionWithDraftReadersAsync(state, readers, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionState> SaveSessionWithDraftReadersAsync(
        SessionState state,
        IReadOnlyDictionary<long, Func<TextReader>>? drafts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var writerLock = AcquireSessionLock();
        // Startup and explicit loads always validate every referenced draft. Within this process,
        // successful checkpoints are immutable and already validated, so a benign save can reuse
        // the validated manifest without reading every large draft again.
        var fallbackState = _hasCachedSession && CachedFilesAreUnchanged()
            ? _cachedSession
            : await LoadSessionAsync(cancellationToken).ConfigureAwait(false);
        var fallbackDrafts = (fallbackState?.Tabs ?? [])
            .Concat(fallbackState?.ClosedTabs ?? [])
            .Where(tab => tab.DraftRef is not null)
            .GroupBy(tab => tab.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var checkpoint = Path.Combine(_checkpoints, $"checkpoint-{DateTime.UtcNow.Ticks:x16}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(checkpoint);
        var committed = false;
        try
        {
            var draftRefs = new Dictionary<long, (string Reference, string Hash)>();
            if (drafts is not null)
            {
                foreach (var (tabId, openReader) in drafts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (temporary, hash, _) = await WriteDraftStreamAsync(checkpoint, tabId, openReader, cancellationToken).ConfigureAwait(false);
                    var previousDraft = fallbackDrafts.GetValueOrDefault(tabId);
                    if (previousDraft is { DraftRef: not null, DraftSha256: not null }
                        && hash.Equals(previousDraft.DraftSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        if (previousDraft.DraftRef.StartsWith($"standalone/session-{tabId}-", StringComparison.Ordinal))
                        {
                            File.Delete(temporary);
                            draftRefs[tabId] = (previousDraft.DraftRef, previousDraft.DraftSha256);
                        }
                        else
                        {
                            draftRefs[tabId] = await MoveToStableDraftAsync(temporary, tabId, hash, cancellationToken).ConfigureAwait(false);
                        }
                        continue;
                    }

                    draftRefs[tabId] = await MoveToStableDraftAsync(temporary, tabId, hash, cancellationToken).ConfigureAwait(false);
                }
            }
            var tabs = new List<SessionTab>(state.Tabs.Count);
            foreach (var tab in state.Tabs)
                tabs.Add(draftRefs.TryGetValue(tab.Id, out var draft) ? tab with { DraftRef = draft.Reference, DraftSha256 = draft.Hash }
                    : await PreserveDraftReferenceAsync(checkpoint, tab, fallbackDrafts.GetValueOrDefault(tab.Id), cancellationToken).ConfigureAwait(false));
            var closed = new List<SessionTab>();
            foreach (var tab in (state.ClosedTabs ?? []).Take(MaxClosedTabs))
                closed.Add(draftRefs.TryGetValue(tab.Id, out var draft) ? tab with { DraftRef = draft.Reference, DraftSha256 = draft.Hash }
                    : await PreserveDraftReferenceAsync(checkpoint, tab, fallbackDrafts.GetValueOrDefault(tab.Id), cancellationToken).ConfigureAwait(false));
            var snapshot = state with { Tabs = tabs, ClosedTabs = closed };
            await using (var stream = new FileStream(Path.Combine(checkpoint, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, StorageJsonContext.Default.SessionState, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false); stream.Flush(true);
            }
            var latest = Path.Combine(_checkpoints, "latest.json");
            var previous = Path.Combine(_checkpoints, "previous.json");
            if (_cachedSessionWasLatest && File.Exists(latest))
            {
                var currentPointer = await File.ReadAllTextAsync(latest, cancellationToken).ConfigureAwait(false);
                await AtomicWriteAsync(previous, System.Text.Encoding.UTF8.GetBytes(currentPointer.Trim()), cancellationToken).ConfigureAwait(false);
            }
            await AtomicWriteAsync(latest, System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(checkpoint)), cancellationToken).ConfigureAwait(false);
            committed = true;
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pointer in new[] { latest, previous })
            {
                if (!File.Exists(pointer)) continue;
                var name = (await File.ReadAllTextAsync(pointer, cancellationToken).ConfigureAwait(false)).Trim();
                if (Path.GetFileName(name) == name && name.StartsWith("checkpoint-", StringComparison.Ordinal)) retained.Add(name);
            }
            foreach (var old in new DirectoryInfo(_checkpoints).GetDirectories("checkpoint-*").OrderByDescending(d => d.Name, StringComparer.Ordinal))
            {
                if (retained.Count >= MaximumCheckpointCount && !retained.Contains(old.Name))
                {
                    try { old.Delete(recursive: true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                retained.Add(old.Name);
            }
            await PruneUnreferencedDraftsAsync(latest, previous, cancellationToken).ConfigureAwait(false);
            CacheSession(snapshot, isLatest: true);
            return snapshot;
        }
        catch
        {
            if (!committed) try { Directory.Delete(checkpoint, recursive: true); } catch { }
            throw;
        }
    }

    public async Task<SessionTab> ResolveCurrentDraftAsync(SessionTab tab, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tab.DraftRef)) return tab;
        try
        {
            var path = ResolveAnyDraftReference(tab.DraftRef);
            if (File.Exists(path))
            {
                if (tab.DraftSha256 is null) return tab;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (hash.Equals(tab.DraftSha256, StringComparison.OrdinalIgnoreCase)) return tab;
            }
        }
        catch (Exception error) when (IsInvalidCheckpointException(error)) { }

        var latest = await LoadSessionAsync(cancellationToken).ConfigureAwait(false);
        var candidate = (latest?.Tabs ?? []).Concat(latest?.ClosedTabs ?? [])
            .FirstOrDefault(value => value.Id == tab.Id && value.DraftSha256 is not null
                && string.Equals(value.DraftSha256, tab.DraftSha256, StringComparison.OrdinalIgnoreCase));
        return candidate ?? throw new IOException("Черновик больше не доступен в сохранённых контрольных точках.");
    }

    public async Task<string?> LoadDraftAsync(SessionTab tab, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tab.DraftRef)) return null;
        var path = ResolveAnyDraftReference(tab.DraftRef);
        if (new FileInfo(path).Length > 8L * 1024 * 1024) throw new IOException("Черновик больше 8 МиБ. Используйте GetDraftPath для потокового чтения.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (tab.DraftSha256 is not null && !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), tab.DraftSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Черновик повреждён.");
        return new System.Text.UTF8Encoding(false, true).GetString(bytes);
    }

    public string? GetDraftPath(SessionTab tab) => string.IsNullOrWhiteSpace(tab.DraftRef) ? null : ResolveAnyDraftReference(tab.DraftRef);
    public long GetDraftLength(SessionTab tab) => GetDraftPath(tab) is { } path ? new FileInfo(path).Length : 0;

    public async Task<SessionTab> SaveClosedDraftAsync(SessionTab tab, string text, CancellationToken token = default)
    {
        return await SaveClosedDraftAsync(tab, () => new StringReader(text), token).ConfigureAwait(false);
    }

    public async Task<SessionTab> SaveClosedDraftAsync(SessionTab tab, Func<TextReader> openReader, CancellationToken token = default)
    {
        var (temporary, hash, lineCount) = await WriteDraftStreamAsync(_drafts, tab.Id, openReader, token).ConfigureAwait(false);
        var name = $"closed-{tab.Id}-{hash}.txt";
        var file = Path.Combine(_drafts, name);
        try
        {
            File.Move(temporary, file, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
        return tab with { DraftRef = "standalone/" + name, DraftSha256 = hash, LineCount = lineCount };
    }

    public SessionTab AdoptEditedDraft(SessionTab tab, string tempPath, string hash)
    {
        var name = $"closed-{tab.Id}-{hash}.txt";
        File.Move(tempPath, Path.Combine(_drafts, name), overwrite: true);
        return tab with { DraftRef = "standalone/" + name, DraftSha256 = hash };
    }

    public async Task SaveDraftAsync(long tabId, string content, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_drafts, $"draft-{tabId}.txt");
        await AtomicWriteAsync(path, System.Text.Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> LoadDraftAsync(long tabId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_drafts, $"draft-{tabId}.txt");
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new System.Text.UTF8Encoding(false, true).GetString(bytes);
    }

    public async Task<UserSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, "settings.json");
        if (!File.Exists(path))
        {
            if (_hasProfileOverride) return new();
            var legacyPath = Path.Combine(_profileBaseDirectory, "settings.json");
            if (!File.Exists(legacyPath)) return new();
            await using var legacy = File.OpenRead(legacyPath);
            try { return (await JsonSerializer.DeserializeAsync(legacy, StorageJsonContext.Default.UserSettings, cancellationToken).ConfigureAwait(false) ?? new()).Normalize(); }
            catch (JsonException ex) { throw new InvalidDataException("Файл настроек RustText повреждён.", ex); }
        }
        await using var stream = File.OpenRead(path);
        try { return (await JsonSerializer.DeserializeAsync(stream, StorageJsonContext.Default.UserSettings, cancellationToken).ConfigureAwait(false) ?? new()).Normalize(); }
        catch (JsonException ex) { throw new InvalidDataException("Файл настроек повреждён.", ex); }
    }

    public Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default) =>
        WriteJsonAsync(Path.Combine(_root, "settings.json"), settings.Normalize(), StorageJsonContext.Default.UserSettings, cancellationToken);

    public async Task<IReadOnlyList<RecentFileEntry>> GetRecentFilesAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, "recent-files.json");
        if (!File.Exists(path))
        {
            if (_hasProfileOverride) return [];
            var legacyPath = Path.Combine(_profileBaseDirectory, "recent-files.json");
            if (!File.Exists(legacyPath)) return [];
            return await ReadLegacyRecentAsync(legacyPath, cancellationToken).ConfigureAwait(false);
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync(stream, StorageJsonContext.Default.ListRecentFileEntry, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task AddRecentAsync(RecentFileEntry entry, CancellationToken cancellationToken = default)
    {
        _ = await UpdateRecentAsync(entry.Path, entry, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RecentFileEntry>> RemoveRecentAsync(string path, CancellationToken token = default) =>
        UpdateRecentAsync(path, null, token);

    private async Task<IReadOnlyList<RecentFileEntry>> UpdateRecentAsync(string path, RecentFileEntry? replacement, CancellationToken token)
    {
        await _recentGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var entries = (await GetRecentFilesAsync(token).ConfigureAwait(false)).ToList();
            entries.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
            if (replacement is not null)
            {
                entries.Insert(0, replacement with { LastWorkedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                if (entries.Count > MaxRecentFiles) entries.RemoveRange(MaxRecentFiles, entries.Count - MaxRecentFiles);
            }
            await WriteJsonAsync(Path.Combine(_root, "recent-files.json"), entries, StorageJsonContext.Default.ListRecentFileEntry, token).ConfigureAwait(false);
            return entries;
        }
        finally { _recentGate.Release(); }
    }

    public Task<JsonDocument?> ReadLegacyProfileFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("Ожидалось имя файла профиля.", nameof(fileName));
        if (_hasProfileOverride) return Task.FromResult<JsonDocument?>(null);
        var path = Path.Combine(_profileBaseDirectory, fileName);
        return ReadJsonDocumentAsync(path, cancellationToken);
    }

    private static async Task<JsonDocument?> ReadJsonDocumentAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    private string ResolveCheckpointReference(string reference)
    {
        var full = Path.GetFullPath(Path.Combine(_checkpoints, reference.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(_checkpoints) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Некорректная ссылка на черновик.");
        EnsureNoReparsePoints(full, Path.GetFullPath(_checkpoints));
        return full;
    }

    private string ResolveAnyDraftReference(string reference)
    {
        if (reference.StartsWith("standalone/", StringComparison.Ordinal))
        {
            var name = reference["standalone/".Length..];
            if (Path.GetFileName(name) != name) throw new InvalidDataException("Некорректная ссылка на черновик.");
            var path = Path.Combine(_drafts, name);
            EnsureNoReparsePoints(path, Path.GetFullPath(_drafts));
            return path;
        }
        if (reference.StartsWith("legacy/", StringComparison.Ordinal))
        {
            if (_hasProfileOverride) throw new InvalidDataException("Legacy profile disabled by RUSTTEXT_PROFILE_DIR.");
            var pieces = reference["legacy/".Length..].Split('/');
            if (pieces.Length != 2 || !pieces[0].StartsWith("checkpoint-", StringComparison.Ordinal)
                || !(pieces[1].StartsWith("draft-", StringComparison.Ordinal) || pieces[1].StartsWith("closed-draft-", StringComparison.Ordinal))
                || Path.GetFileName(pieces[1]) != pieces[1]) throw new InvalidDataException("Некорректная legacy-ссылка на черновик.");
            var legacyRoot = Path.GetFullPath(Path.Combine(_profileBaseDirectory, "session"));
            var legacyPath = Path.GetFullPath(Path.Combine(legacyRoot, pieces[0], pieces[1]));
            EnsureNoReparsePoints(legacyPath, legacyRoot);
            return legacyPath;
        }
        return ResolveCheckpointReference(reference);
    }

    private async Task<bool> ValidateSessionDraftsAsync(SessionState state, CancellationToken token)
    {
        foreach (var tab in (state.Tabs ?? []).Concat(state.ClosedTabs ?? []))
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(tab.DraftRef))
            {
                continue;
            }

            try
            {
                var path = ResolveAnyDraftReference(tab.DraftRef);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if (tab.DraftSha256 is null)
                {
                    continue;
                }

                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (!actual.Equals(tab.DraftSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (Exception error) when (IsInvalidCheckpointException(error))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<SessionState?> ReadCheckpointAsync(string pointerPath, CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await FindCheckpointManifestAsync(pointerPath, cancellationToken).ConfigureAwait(false);
            return manifest is null ? null : await ReadSessionManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsInvalidCheckpointException(error))
        {
            return null;
        }
    }

    private async Task<string?> FindCheckpointManifestAsync(string pointerPath, CancellationToken token)
    {
        if (!File.Exists(pointerPath)) return null;
        var name = (await File.ReadAllTextAsync(pointerPath, token).ConfigureAwait(false)).Trim();
        return Path.GetFileName(name) == name && name.StartsWith("checkpoint-", StringComparison.Ordinal)
            ? Path.Combine(_checkpoints, name, "manifest.json") : null;
    }

    private static async Task<SessionState?> ReadSessionManifestAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var state = await JsonSerializer.DeserializeAsync(stream, StorageJsonContext.Default.SessionState, token).ConfigureAwait(false);
        return state?.Tabs is not null ? state : null;
    }

    public async Task RemoveHistoryFromPreviousCheckpointAsync(long tabId, bool removeBackupDraft, CancellationToken token = default)
    {
        using var writerLock = AcquireSessionLock();
        var previous = Path.Combine(_checkpoints, "previous.json");
        var manifest = await FindCheckpointManifestAsync(previous, token).ConfigureAwait(false);
        if (!File.Exists(manifest)) return;
        EnsureNoReparsePoints(manifest, Path.GetFullPath(_checkpoints));
        if (await ReadSessionManifestAsync(manifest, token).ConfigureAwait(false) is not { } backup) return;

        var closed = backup.ClosedTabs?.Where(tab => tab.Id != tabId).ToList();
        var tabs = removeBackupDraft ? backup.Tabs.Where(tab => tab.Id != tabId).ToList() : backup.Tabs;
        if (tabs.Count == backup.Tabs.Count && closed?.Count == backup.ClosedTabs?.Count) return;

        var active = backup.Active - backup.Tabs.Take(backup.Active).Count(tab => removeBackupDraft && tab.Id == tabId);
        backup = backup with { Tabs = tabs, ClosedTabs = closed, Active = Math.Clamp(active, 0, Math.Max(0, tabs.Count - 1)) };
        await WriteJsonAsync(manifest, backup, StorageJsonContext.Default.SessionState, token).ConfigureAwait(false);
        await PruneUnreferencedDraftsAsync(Path.Combine(_checkpoints, "latest.json"), previous, token).ConfigureAwait(false);
        if (_cachedSessionWasLatest) CacheSession(_cachedSession, isLatest: true);
    }

    private static bool IsInvalidCheckpointException(Exception error) =>
        error is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or ArgumentException
            or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;

    private static void EnsureNoReparsePoints(string path, string root)
    {
        var current = path;
        while (current.Length >= root.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Ссылка на черновик проходит через символьную ссылку.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return;
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }
        throw new InvalidDataException("Ссылка на черновик вышла за пределы хранилища.");
    }

    private async Task<SessionTab> PreserveDraftReferenceAsync(
        string checkpoint,
        SessionTab tab,
        SessionTab? fallback,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(tab.DraftRef))
        {
            return tab;
        }

        if (fallback is { DraftRef: not null, DraftSha256: not null }
            && string.Equals(tab.DraftRef, fallback.DraftRef, StringComparison.Ordinal)
            && string.Equals(tab.DraftSha256, fallback.DraftSha256, StringComparison.OrdinalIgnoreCase)
            && (tab.DraftRef.StartsWith("standalone/", StringComparison.Ordinal)
                || tab.DraftRef.StartsWith("legacy/", StringComparison.Ordinal)))
        {
            return tab;
        }

        if (await TryPreserveDraftAsync(checkpoint, tab, token).ConfigureAwait(false) is { } preserved)
        {
            return preserved;
        }

        if (fallback is not null && await TryPreserveDraftAsync(checkpoint, fallback, token).ConfigureAwait(false) is { } recovered)
        {
            return tab with { DraftRef = recovered.DraftRef, DraftSha256 = recovered.DraftSha256 };
        }

        return tab with { DraftRef = null, DraftSha256 = null };
    }

    private static async Task<(string TemporaryPath, string Hash, long LineCount)> WriteDraftStreamAsync(
        string checkpoint,
        long tabId,
        Func<TextReader> openReader,
        CancellationToken token)
    {
        var temporary = Path.Combine(checkpoint, $"draft-{tabId}-{Guid.NewGuid():N}.tmp");
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var encoder = encoding.GetEncoder();
        var chars = new char[32 * 1024];
        var bytes = new byte[encoding.GetMaxByteCount(chars.Length)];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long lineBreakCount = 0;
        var hasCharacters = false;
        var lastCharacter = '\0';
        var previousCarriageReturn = false;
        try
        {
            using var reader = openReader();
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            while (true)
            {
                var read = await reader.ReadAsync(chars.AsMemory(), token).ConfigureAwait(false);
                var flush = read == 0;
                for (var index = 0; index < read; index++)
                {
                    hasCharacters = true;
                    lastCharacter = chars[index];
                    if (chars[index] == '\r')
                    {
                        lineBreakCount++;
                        previousCarriageReturn = true;
                    }
                    else if (chars[index] == '\n')
                    {
                        if (!previousCarriageReturn) lineBreakCount++;
                        previousCarriageReturn = false;
                    }
                    else
                    {
                        previousCarriageReturn = false;
                    }
                }
                encoder.Convert(chars, 0, read, bytes, 0, bytes.Length, flush,
                    out _, out var bytesUsed, out var completed);
                if (bytesUsed > 0)
                {
                    hash.AppendData(bytes, 0, bytesUsed);
                    await output.WriteAsync(bytes.AsMemory(0, bytesUsed), token).ConfigureAwait(false);
                }
                if (flush && completed) break;
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            var lineCount = lineBreakCount + (hasCharacters && lastCharacter is not ('\r' or '\n') ? 1 : 0);
            return (temporary, Convert.ToHexString(hash.GetHashAndReset()), lineCount);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    private Task<(string Reference, string Hash)> MoveToStableDraftAsync(string temporary, long tabId, string hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = $"session-{tabId}-{hash}.txt";
        File.Move(temporary, Path.Combine(_drafts, name), overwrite: true);
        return Task.FromResult(("standalone/" + name, hash));
    }

    private async Task PruneUnreferencedDraftsAsync(string latest, string previous, CancellationToken token)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pointer in new[] { latest, previous })
        {
            if (!File.Exists(pointer)) continue;
            var checkpointName = (await File.ReadAllTextAsync(pointer, token).ConfigureAwait(false)).Trim();
            if (Path.GetFileName(checkpointName) != checkpointName || !checkpointName.StartsWith("checkpoint-", StringComparison.Ordinal)) continue;
            var manifest = Path.Combine(_checkpoints, checkpointName, "manifest.json");
            if (!File.Exists(manifest)) continue;
            await using var stream = File.OpenRead(manifest);
            var state = await JsonSerializer.DeserializeAsync(stream, StorageJsonContext.Default.SessionState, token).ConfigureAwait(false);
            if (state is null) continue;
            foreach (var tab in (state.Tabs ?? []).Concat(state.ClosedTabs ?? []))
            {
                if (tab.DraftRef is not { } reference) continue;
                try { referenced.Add(Path.GetFullPath(ResolveAnyDraftReference(reference))); }
                catch (Exception error) when (IsInvalidCheckpointException(error)) { }
            }
        }

        var root = Path.GetFullPath(_drafts) + Path.DirectorySeparatorChar;
        foreach (var file in new DirectoryInfo(_drafts).EnumerateFiles())
        {
            token.ThrowIfCancellationRequested();
            if (!file.Name.StartsWith("session-", StringComparison.Ordinal)
                && !file.Name.StartsWith("closed-", StringComparison.Ordinal)) continue;
            var fullPath = Path.GetFullPath(file.FullName);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || referenced.Contains(fullPath)) continue;
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<SessionTab?> TryPreserveDraftAsync(string checkpoint, SessionTab tab, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(tab.DraftRef)) return null;
        string source;
        try
        {
            source = ResolveAnyDraftReference(tab.DraftRef);
            var attributes = File.GetAttributes(source);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or ArgumentException)
        {
            return null;
        }

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        var sha = Convert.ToHexString(hash);
        if (tab.DraftSha256 is not null && !sha.Equals(tab.DraftSha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = $"session-{tab.Id}-{sha}.txt";
        var destination = Path.Combine(_drafts, name);
        var saved = tab with { DraftRef = "standalone/" + name, DraftSha256 = sha };
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return saved;

        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        input.Position = 0;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
        return saved;
    }

    private async Task<SessionState?> LoadLegacySessionAsync(CancellationToken token)
    {
        if (_hasProfileOverride) return null;
        var legacySession = Path.Combine(_profileBaseDirectory, "session");
        if (!Directory.Exists(legacySession)) return null;
        foreach (var directory in new DirectoryInfo(legacySession).GetDirectories("checkpoint-*", SearchOption.TopDirectoryOnly).OrderByDescending(d => d.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var path = Path.Combine(directory.FullName, "manifest.json");
            try
            {
                await using var stream = File.OpenRead(path);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                var root = document.RootElement;
                if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1 || !root.TryGetProperty("state", out var rawState)) continue;
                if (!await VerifyLegacyDraftsAsync(directory.FullName, root, "drafts", "draft-", token).ConfigureAwait(false)
                    || !await VerifyLegacyDraftsAsync(directory.FullName, root, "closed_drafts", "closed-draft-", token).ConfigureAwait(false)) continue;
                var openDrafts = GetDraftReferences(root, "drafts", "draft-");
                var closedDrafts = GetDraftReferences(root, "closed_drafts", "closed-draft-");
                var state = ParseLegacyState(rawState, directory.Name, openDrafts, closedDrafts);
                return state;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or ArgumentException) { }
        }
        return null;
    }

    private static async Task<bool> VerifyLegacyDraftsAsync(string directory, JsonElement manifest, string property, string prefix, CancellationToken token)
    {
        if (!manifest.TryGetProperty(property, out var infos) || infos.ValueKind != JsonValueKind.Array) return true;
        var index = 0;
        foreach (var info in infos.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (info.ValueKind == JsonValueKind.Null) { index++; continue; }
            if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("bytes", out var lengthValue) || !lengthValue.TryGetInt64(out var expectedLength)) return false;
            var path = Path.Combine(directory, prefix + index + ".txt");
            if (!File.Exists(path) || new FileInfo(path).Length != expectedLength) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            uint crc = 0xFFFFFFFF;
            long readTotal = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                sha.AppendData(buffer, 0, read);
                readTotal += read;
                for (var j = 0; j < read; j++)
                {
                    crc ^= buffer[j];
                    for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
                }
            }
            if (readTotal != expectedLength) return false;
            var actualSha = Convert.ToHexString(sha.GetHashAndReset());
            if (info.TryGetProperty("sha256", out var shaValue) && shaValue.ValueKind == JsonValueKind.String)
            {
                if (!string.Equals(actualSha, shaValue.GetString(), StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (info.TryGetProperty("checksum", out var checksum) && checksum.TryGetUInt32(out var expectedCrc) && ~crc != expectedCrc) return false;
            index++;
        }
        return true;
    }

    private static Dictionary<int, (string Ref, string? Hash)> GetDraftReferences(JsonElement manifest, string property, string prefix)
    {
        var result = new Dictionary<int, (string, string?)>();
        if (!manifest.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) return result;
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var hash = item.TryGetProperty("sha256", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null;
                result[index] = (prefix + index + ".txt", hash);
            }
            index++;
        }
        return result;
    }

    private static SessionState ParseLegacyState(JsonElement state, string checkpoint, Dictionary<int, (string Ref, string? Hash)> drafts, Dictionary<int, (string Ref, string? Hash)> closedDrafts)
    {
        var wrap = TryBool(state, "wrap");
        var tabs = ApplyLegacyLogDefaults(ParseTabs(state, "tabs", checkpoint, drafts, "draft-"), wrap);
        var closed = ApplyLegacyLogDefaults(ParseTabs(state, "closed", checkpoint, closedDrafts, "closed-draft-"), wrap);
        var active = TryInt(state, "active", 0);
        var window = state.TryGetProperty("window", out var w) ? w : default;
        var layout = state.TryGetProperty("layout", out var layoutValue) && layoutValue.ValueKind == JsonValueKind.Object ? ParseLegacyLayout(layoutValue) : null;
        var activeGroup = state.TryGetProperty("layout", out var legacyLayout) && legacyLayout.ValueKind == JsonValueKind.Object ? TryLong(legacyLayout, "focused", 0) : (long?)null;
        return new(tabs, closed, Math.Clamp(active, 0, Math.Max(0, tabs.Count - 1)), TryDouble(window, "width", 1100), TryDouble(window, "height", 760), TryBool(window, "maximized"), wrap, TryDouble(state, "font_size", 14), layout, activeGroup);
    }

    private static List<SessionTab> ApplyLegacyLogDefaults(List<SessionTab> tabs, bool wrap) =>
        tabs.Select(tab => tab.Log is { } log ? tab with { Log = log with { Encoding = tab.Encoding, WordWrap = wrap } } : tab).ToList();

    private static List<SessionTab> ParseTabs(JsonElement state, string property, string checkpoint, Dictionary<int, (string Ref, string? Hash)> drafts, string draftPrefix)
    {
        var result = new List<SessionTab>();
        if (!state.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) return result;
        foreach (var (item, index) in items.EnumerateArray().Select((value, index) => (value, index)))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var format = item.TryGetProperty("format", out var f) ? f : default;
            var encodingText = TryString(format, "encoding", "Utf8");
            var encoding = encodingText switch { "Utf8Bom" => TextEncodingKind.Utf8Bom, "Utf16Le" => TextEncodingKind.Utf16LittleEndian, "Utf16Be" => TextEncodingKind.Utf16BigEndian, "Windows1251" => TextEncodingKind.Windows1251, _ => TextEncodingKind.Utf8 };
            var endingText = TryString(format, "endings", "Lf");
            var ending = endingText switch { "CrLf" => LineEndingKind.CrLf, "Cr" => LineEndingKind.Cr, "Mixed" => LineEndingKind.Mixed, _ => LineEndingKind.Lf };
            string? file = null;
            if (item.TryGetProperty("file", out var fileValue) && fileValue.ValueKind == JsonValueKind.Object && fileValue.TryGetProperty("path", out var fp)) file = fp.GetString();
            var range = item.TryGetProperty("selection", out var selection) ? selection : default;
            var start = TryInt(range, "start", 0); var end = TryInt(range, "end", start);
            var draft = drafts.TryGetValue(index, out var d) ? d : default;
            LogTabState? logState = null;
            if (item.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    logState = JsonSerializer.Deserialize(log, StorageJsonContext.Default.LogTabState);
                }
                catch (JsonException)
                {
                }

                logState ??= new LogTabState(
                    Offset: TryLong(log, "offset", 0),
                    HiddenOffset: TryLong(log, "hidden", 0),
                    FollowEnd: TryBool(log, "follow_end"),
                    AutoRefresh: !log.TryGetProperty("auto_refresh", out var autoRefresh) || autoRefresh.ValueKind == JsonValueKind.True);
                logState = logState with { Encoding = encoding, HiddenOffset = TryLong(log, "hidden", logState.HiddenOffset) };
            }
            result.Add(new SessionTab(TryLong(item, "id", index + 1), file, encoding, start, Math.Max(0, end - start), TryBool(item, "reversed"), ReadLegacyScroll(item, 0), ReadLegacyScroll(item, 1), draft.Ref is null ? null : $"legacy/{checkpoint}/{draftPrefix}{index}.txt", draft.Hash, TryLongNullable(item, "log", "offset"), TryLongDirectNullable(item, "line_count"), TryLongDirectNullable(item, "last_worked_ms"), ending, Log: logState)
            {
                JsonValidationDisabled = TryBool(item, "json_validation_disabled"),
                LegacyStamp = ReadLegacyStamp(fileValue),
                SelectionUsesUtf8 = true
            });
        }
        return result;
    }

    private static LegacyFileStamp? ReadLegacyStamp(JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Object || TryLongDirectNullable(version, "bytes") is not { } length
            || ReadLegacyTime(version, "modified") is not { } modified)
        {
            return null;
        }

        return new LegacyFileStamp(length, modified, ReadLegacyTime(version, "created"));
    }

    private static DateTime? ReadLegacyTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object
            || TryLongDirectNullable(value, "secs_since_epoch") is not { } seconds)
        {
            return null;
        }

        var nanos = TryLong(value, "nanos_since_epoch", 0);
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddTicks(nanos / 100L);
    }

    private static WorkspaceLayout? ParseLegacyLayout(JsonElement value)
    {
        if (!value.TryGetProperty("root", out var root)) return null;
        var focused = TryLong(value, "focused", 1);
        return ConvertLayoutNode(root, focused);
    }

    private static WorkspaceLayout? ConvertLayoutNode(JsonElement node, long focused)
    {
        var id = TryLong(node, "id", focused);
        if (node.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.Object)
        {
            var tabs = group.TryGetProperty("documents", out var docs) && docs.ValueKind == JsonValueKind.Array ? docs.EnumerateArray().Select(e => e.GetInt64()).ToArray() : [];
            long? active = group.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : null;
            return new(id, tabs, active);
        }
        if (node.TryGetProperty("split", out var split) && split.ValueKind == JsonValueKind.Object)
        {
            var axis = TryString(split, "axis", "Horizontal");
            var ratio = TryDouble(split, "ratio", 0.5);
            var first = split.TryGetProperty("first", out var left) ? ConvertLayoutNode(left, focused) : null;
            var second = split.TryGetProperty("second", out var right) ? ConvertLayoutNode(right, focused) : null;
            return new(id, Split: axis, Ratio: ratio, First: first, Second: second);
        }
        return new(id);
    }

    private async Task<IReadOnlyList<RecentFileEntry>> ReadLegacyRecentAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
        var results = new List<RecentFileEntry>();
        foreach (var entry in json.RootElement.EnumerateArray().Take(MaxRecentFiles))
        {
            var filePath = TryString(entry, "path", ""); if (filePath.Length == 0) continue;
            TextEncodingKind? enc = TryBool(entry, "windows_1251") ? TextEncodingKind.Windows1251 : null;
            var position = entry.TryGetProperty("position", out var p) ? p : default;
            var range = position.ValueKind == JsonValueKind.Object && position.TryGetProperty("selection", out var sel) ? sel : default;
            var start = LegacyByteIndex(range, "start", 0);
            var end = LegacyByteIndex(range, "end", start);
            var length = Math.Max(0, end - start);
            var log = ReadLegacyLogBookmark(position);
            if (log is not null)
            {
                log = log with { Encoding = enc };
            }

            results.Add(new RecentFileEntry(
                filePath,
                enc,
                TryBool(entry, "view_as_log"),
                start,
                length,
                log?.Offset ?? TryLongNullable(position, "log", "offset"),
                TryLongDirectNullable(entry, "line_count"),
                TryLongDirectNullable(entry, "last_worked_ms"),
                TryBool(position, "reversed"),
                ReadLegacyScroll(position, 0),
                ReadLegacyScroll(position, 1),
                TryBool(position, "json_validation_disabled"),
                log,
                SelectionUsesUtf8: true));
        }
        return results;
    }

    private static LogTabState? ReadLegacyLogBookmark(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Object || !position.TryGetProperty("log", out var log)
            || log.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize(log, StorageJsonContext.Default.LogTabState);
            return state is null ? null : state with { HiddenOffset = TryLong(log, "hidden", state.HiddenOffset) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int LegacyByteIndex(JsonElement range, string name, int fallback)
    {
        var start = Math.Clamp(TryLong(range, name, fallback), 0, int.MaxValue);
        return (int)start;
    }

    private static double ReadLegacyScroll(JsonElement item, int index)
    {
        var offset = TryArrayDouble(item, "scroll", index);
        return double.IsFinite(offset) ? Math.Max(0, -offset) : 0;
    }

    private static string TryString(JsonElement element, string name, string fallback) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static bool TryBool(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static int TryInt(JsonElement element, string name, int fallback) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;
    private static long TryLong(JsonElement element, string name, long fallback) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : fallback;
    private static double TryDouble(JsonElement element, string name, double fallback) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : fallback;
    private static double TryArrayDouble(JsonElement element, string name, int index) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > index ? value[index].GetDouble() : 0;
    private static long? TryLongDirectNullable(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;
    private static long? TryLongNullable(JsonElement element, string parent, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out var value) && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var number) && number.TryGetInt64(out var parsed) ? parsed : null;

    private FileStream AcquireSessionLock()
    {
        var stream = new FileStream(Path.Combine(_checkpoints, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try { stream.Lock(0, 1); return stream; }
        catch { stream.Dispose(); throw; }
    }

    internal static async Task WriteJsonAsync<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        await AtomicWriteAsync(path, bytes, token).ConfigureAwait(false);
    }

    internal static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("Некорректный путь.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.pending");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (FileNotFoundException) { } }
    }
}

internal static class SettingsExtensions
{
    internal static UserSettings Normalize(this UserSettings settings) => settings with
    {
        UntitledTabLimit = Math.Clamp(settings.UntitledTabLimit, 1, 200),
        RecentFileLimit = Math.Clamp(settings.RecentFileLimit, 1, 100),
        VersionFileLimitBytes = Math.Clamp(settings.VersionFileLimitBytes, 0, 50 * 1024 * 1024),
        VersionTotalLimitBytes = Math.Clamp(settings.VersionTotalLimitBytes, 0, 1024L * 1024 * 1024)
    };
}
