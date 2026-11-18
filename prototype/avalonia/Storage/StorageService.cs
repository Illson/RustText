using System.Security.Cryptography;
using System.Text.Json;
using RustText.Demo.Logs;

namespace RustText.Demo;

internal sealed class StorageService
{
    private const int MaxRecentFiles = 100;
    private const int MaxClosedTabs = 100;
    private readonly string _root;
    private readonly string _checkpoints;
    private readonly string _drafts;
    private readonly string _versions;
    private readonly bool _hasProfileOverride;

    private StorageService(string root)
    {
        _root = root;
        _hasProfileOverride = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RUSTTEXT_PROFILE_DIR"));
        _checkpoints = Path.Combine(root, "session");
        _drafts = Path.Combine(root, "drafts");
        _versions = Path.Combine(root, "versions");
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
            return new StorageService(Path.Combine(Path.GetFullPath(overridePath), "dotnet-demo"));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("Папка LOCALAPPDATA недоступна.");
        return new StorageService(Path.Combine(local, "RustText", "dotnet-demo"));
    }

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

    public async Task<SessionState?> LoadSessionAsync(CancellationToken cancellationToken = default)
    {
        var current = Path.Combine(_checkpoints, "latest.json");
        var backup = Path.Combine(_checkpoints, "previous.json");
        foreach (var path in new[] { current, backup })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var checkpointName = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
                if (Path.GetFileName(checkpointName) != checkpointName || !checkpointName.StartsWith("checkpoint-", StringComparison.Ordinal)) continue;
                var manifest = Path.Combine(_checkpoints, checkpointName, "manifest.json");
                await using var stream = File.OpenRead(manifest);
                return await JsonSerializer.DeserializeAsync(stream, StorageJsonContext.Default.SessionState, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return await LoadLegacySessionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(SessionState state, IReadOnlyDictionary<long, string>? drafts = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var writerLock = AcquireSessionLock();
        var checkpoint = Path.Combine(_checkpoints, $"checkpoint-{DateTime.UtcNow.Ticks:x16}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(checkpoint);
        try
        {
            var draftRefs = new Dictionary<long, (string Reference, string Hash)>();
            if (drafts is not null)
            {
                foreach (var (tabId, content) in drafts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = $"draft-{tabId}.txt";
                    await AtomicWriteAsync(Path.Combine(checkpoint, name), System.Text.Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);
                    draftRefs[tabId] = (Path.GetFileName(checkpoint) + "/" + name, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
                }
            }
            var tabs = new List<SessionTab>(state.Tabs.Count);
            foreach (var tab in state.Tabs)
                tabs.Add(draftRefs.TryGetValue(tab.Id, out var draft) ? tab with { DraftRef = draft.Reference, DraftSha256 = draft.Hash } : await PreserveDraftReferenceAsync(checkpoint, tab, cancellationToken).ConfigureAwait(false));
            var closed = new List<SessionTab>();
            foreach (var tab in (state.ClosedTabs ?? []).Take(MaxClosedTabs))
                closed.Add(draftRefs.TryGetValue(tab.Id, out var draft) ? tab with { DraftRef = draft.Reference, DraftSha256 = draft.Hash } : await PreserveDraftReferenceAsync(checkpoint, tab, cancellationToken).ConfigureAwait(false));
            var snapshot = state with { Tabs = tabs, ClosedTabs = closed };
            await using (var stream = new FileStream(Path.Combine(checkpoint, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, StorageJsonContext.Default.SessionState, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false); stream.Flush(true);
            }
            var latest = Path.Combine(_checkpoints, "latest.json");
            if (File.Exists(latest)) File.Copy(latest, Path.Combine(_checkpoints, "previous.json"), overwrite: true);
            await AtomicWriteAsync(latest, System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(checkpoint)), cancellationToken).ConfigureAwait(false);
            var dirs = new DirectoryInfo(_checkpoints).GetDirectories("checkpoint-*").OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(2);
            foreach (var old in dirs) old.Delete(recursive: true);
        }
        catch { try { Directory.Delete(checkpoint, recursive: true); } catch { } throw; }
    }

    public async Task<string?> LoadDraftAsync(SessionTab tab, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tab.DraftRef)) return null;
        var path = ResolveAnyDraftReference(tab.DraftRef);
        if (new FileInfo(path).Length > 8L * 1024 * 1024) throw new IOException("Черновик больше 8 МиБ. Используйте GetDraftPath для потокового чтения.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (tab.DraftSha256 is not null && !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), tab.DraftSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Черновик повреждён.");
        return new System.Text.UTF8Encoding(false, true).GetString(bytes);
    }

    public string? GetDraftPath(SessionTab tab) => string.IsNullOrWhiteSpace(tab.DraftRef) ? null : ResolveAnyDraftReference(tab.DraftRef);
    public long GetDraftLength(SessionTab tab) => GetDraftPath(tab) is { } path ? new FileInfo(path).Length : 0;

    public async Task<SessionTab> SaveClosedDraftAsync(SessionTab tab, string text, CancellationToken token = default)
    {
        var file = Path.Combine(_drafts, $"closed-{tab.Id}.txt");
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await AtomicWriteAsync(file, bytes, token).ConfigureAwait(false);
        return tab with { DraftRef = "standalone/closed-" + tab.Id + ".txt", DraftSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
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
            var legacyPath = Path.Combine(GetProfileBaseDirectory(), "settings.json");
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
            var legacyPath = Path.Combine(GetProfileBaseDirectory(), "recent-files.json");
            if (!File.Exists(legacyPath)) return [];
            return await ReadLegacyRecentAsync(legacyPath, cancellationToken).ConfigureAwait(false);
        }
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, StorageJsonContext.Default.ListRecentFileEntry, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task AddRecentAsync(RecentFileEntry entry, CancellationToken cancellationToken = default)
    {
        var entries = (await GetRecentFilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
        entries.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, entry with { LastWorkedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        entries = entries.Take(MaxRecentFiles).ToList();
        await WriteJsonAsync(Path.Combine(_root, "recent-files.json"), entries, StorageJsonContext.Default.ListRecentFileEntry, cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonDocument?> ReadLegacyProfileFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("Ожидалось имя файла профиля.", nameof(fileName));
        if (_hasProfileOverride) return Task.FromResult<JsonDocument?>(null);
        var path = Path.Combine(GetProfileBaseDirectory(), fileName);
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
            var legacyRoot = Path.GetFullPath(Path.Combine(GetProfileBaseDirectory(), "session"));
            var legacyPath = Path.GetFullPath(Path.Combine(legacyRoot, pieces[0], pieces[1]));
            EnsureNoReparsePoints(legacyPath, legacyRoot);
            return legacyPath;
        }
        return ResolveCheckpointReference(reference);
    }

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

    private async Task<SessionTab> PreserveDraftReferenceAsync(string checkpoint, SessionTab tab, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(tab.DraftRef)) return tab;
        var source = ResolveAnyDraftReference(tab.DraftRef);
        if (!File.Exists(source)) return tab with { DraftRef = null, DraftSha256 = null };
        var name = $"draft-{tab.Id}.txt";
        var destination = Path.Combine(checkpoint, name);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        input.Position = 0;
        await input.CopyToAsync(output, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
        var sha = Convert.ToHexString(hash);
        if (tab.DraftSha256 is not null && !string.Equals(sha, tab.DraftSha256, StringComparison.Ordinal)) throw new InvalidDataException("Черновик повреждён.");
        return tab with { DraftRef = Path.GetFileName(checkpoint) + "/" + name, DraftSha256 = sha };
    }

    private async Task<SessionState?> LoadLegacySessionAsync(CancellationToken token)
    {
        if (_hasProfileOverride) return null;
        var legacySession = Path.Combine(GetProfileBaseDirectory(), "session");
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
        var tabs = ParseTabs(state, "tabs", checkpoint, drafts, "draft-");
        var closed = ParseTabs(state, "closed", checkpoint, closedDrafts, "closed-draft-");
        var active = TryInt(state, "active", 0);
        var window = state.TryGetProperty("window", out var w) ? w : default;
        var layout = state.TryGetProperty("layout", out var layoutValue) && layoutValue.ValueKind == JsonValueKind.Object ? ParseLegacyLayout(layoutValue) : null;
        var activeGroup = state.TryGetProperty("layout", out var legacyLayout) && legacyLayout.ValueKind == JsonValueKind.Object ? TryLong(legacyLayout, "focused", 0) : (long?)null;
        return new(tabs, closed, Math.Clamp(active, 0, Math.Max(0, tabs.Count - 1)), TryDouble(window, "width", 1100), TryDouble(window, "height", 760), TryBool(window, "maximized"), TryBool(state, "wrap"), TryDouble(state, "font_size", 14), layout, activeGroup);
    }

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
                logState = new LogTabState(Offset: TryLong(log, "offset", 0), HiddenOffset: TryLong(log, "hidden", 0), FollowEnd: TryBool(log, "follow_end"), AutoRefresh: !log.TryGetProperty("auto_refresh", out var autoRefresh) || autoRefresh.ValueKind == JsonValueKind.True);
            result.Add(new SessionTab(TryLong(item, "id", index + 1), file, encoding, start, Math.Max(0, end - start), TryBool(item, "reversed"), TryArrayDouble(item, "scroll", 0), TryArrayDouble(item, "scroll", 1), draft.Ref is null ? null : $"legacy/{checkpoint}/{draftPrefix}{index}.txt", draft.Hash, TryLongNullable(item, "log", "offset"), TryLongDirectNullable(item, "line_count"), TryLongDirectNullable(item, "last_worked_ms"), ending, Log: logState)
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
            var enc = TryBool(entry, "windows_1251") ? TextEncodingKind.Windows1251 : TextEncodingKind.Utf8;
            var position = entry.TryGetProperty("position", out var p) ? p : default;
            var range = position.ValueKind == JsonValueKind.Object && position.TryGetProperty("selection", out var sel) ? sel : default;
            results.Add(new(filePath, enc, TryBool(entry, "view_as_log"), TryInt(range, "start", 0), Math.Max(0, TryInt(range, "end", 0) - TryInt(range, "start", 0)), TryLongNullable(position, "log", "offset"), TryLongDirectNullable(entry, "line_count"), TryLongDirectNullable(entry, "last_worked_ms")));
        }
        return results;
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
