using System.Text.Json;
using System.Text.Json.Serialization;

namespace RustText.Demo;

internal sealed record WorkspaceEntry(string Id, string Name, long LastUsedMs = 0)
{
    public string? Theme { get; init; }
}
internal sealed record DeletedWorkspaceEntry(WorkspaceEntry Workspace, long DeletedAtMs, string DirectoryName);
public enum WorkspaceStartupMode { LastUsed, Specific, Empty }

internal sealed record WorkspaceCatalog(
    List<WorkspaceEntry> Workspaces,
    Dictionary<string, string> WindowAssignments,
    List<DeletedWorkspaceEntry>? DeletedWorkspaces = null)
{
    public static WorkspaceCatalog CreateDefault() => new(
        [new WorkspaceEntry(WorkspaceCatalogStore.DefaultWorkspaceId, "По умолчанию")],
        new Dictionary<string, string>(StringComparer.Ordinal) { [WorkspaceCatalogStore.PrimaryWindowId] = WorkspaceCatalogStore.DefaultWorkspaceId });
}

internal sealed class WorkspaceCatalogStore
{
    internal const string DefaultWorkspaceId = "default";
    internal const string PrimaryWindowId = "primary";
    private const int MaximumWorkspaceCount = 200;
    private const int MaximumNameLength = 80;
    private readonly string _directory;
    private readonly string _indexPath;
    private readonly string _moveMarker;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceCatalogStore(string profileBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBaseDirectory);
        var profile = Path.GetFullPath(profileBaseDirectory);
        _directory = Path.Combine(profile, "workspaces");
        _indexPath = Path.Combine(_directory, "index.json");
        _moveMarker = Path.Combine(_directory, "pending-directory-move");
    }

    public string DirectoryPath => _directory;

    public async Task<WorkspaceCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceEntry> CreateAsync(string name, string? theme = null, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (catalog.Workspaces.Count >= MaximumWorkspaceCount)
                throw new InvalidOperationException("Достигнут предел в 200 рабочих пространств.");
            EnsureNameAvailable(catalog, name);
            var entry = new WorkspaceEntry(Guid.NewGuid().ToString("N"), name) { Theme = theme };
            var updated = catalog with { Workspaces = [.. catalog.Workspaces, entry] };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceCatalog> SetThemeAsync(string id, string theme, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var selected = catalog.Workspaces.FirstOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException("Рабочее пространство не найдено.");
            if (selected.Theme == theme) return catalog;
            var updated = catalog with
            {
                Workspaces = catalog.Workspaces.Select(item => item.Id == id ? item with { Theme = theme } : item).ToList()
            };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (id == DefaultWorkspaceId) throw new InvalidOperationException("Рабочее пространство «По умолчанию» нельзя переименовать.");
            var selected = catalog.Workspaces.FirstOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException("Рабочее пространство не найдено.");
            EnsureNameAvailable(catalog, name, id);
            var updated = catalog with
            {
                Workspaces = catalog.Workspaces.Select(item => item.Id == id ? item with { Name = name } : item).ToList()
            };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetWindowWorkspaceAsync(string windowId, string workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!catalog.Workspaces.Any(item => item.Id == workspaceId))
                throw new KeyNotFoundException("Рабочее пространство не найдено.");
            var assignments = new Dictionary<string, string>(catalog.WindowAssignments, StringComparer.Ordinal)
            {
                [windowId] = workspaceId
            };
            var updated = catalog with
            {
                WindowAssignments = assignments,
                Workspaces = catalog.Workspaces.Select(item => item.Id == workspaceId
                    ? item with { LastUsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                    : item).ToList()
            };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceCatalog> RemoveWindowAsync(string windowId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!catalog.WindowAssignments.ContainsKey(windowId)) return catalog;
            var assignments = new Dictionary<string, string>(catalog.WindowAssignments, StringComparer.Ordinal);
            assignments.Remove(windowId);
            var updated = catalog with { WindowAssignments = assignments };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<WorkspaceCatalog> DeleteAsync(string id, int retentionLimit = 10, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(id, retentionLimit, cancellationToken);

    public Task<WorkspaceCatalog> DiscardIncompleteCopyAsync(string id) =>
        DeleteCoreAsync(id, null, CancellationToken.None);

    private async Task<WorkspaceCatalog> DeleteCoreAsync(string id, int? retentionLimit, CancellationToken cancellationToken)
    {
        if (id == DefaultWorkspaceId) throw new InvalidOperationException("Пространство «По умолчанию» удалить нельзя.");
        if (retentionLimit is < -1 or > MaximumWorkspaceCount) throw new ArgumentOutOfRangeException(nameof(retentionLimit));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!catalog.Workspaces.Any(item => item.Id == id)) throw new KeyNotFoundException("Рабочее пространство не найдено.");
            var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var archiveRoot = Path.Combine(_directory, "deleted");
            var directoryName = id + "-" + deletedAt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N");
            var tombstone = Path.Combine(archiveRoot, directoryName);
            var workspacePath = GetWorkspaceDirectory(id);
            var moved = false;
            var committed = false;
            try
            {
                if (System.IO.Directory.Exists(workspacePath))
                {
                    EnsureNoReparsePoints(workspacePath);
                    EnsureNoReparsePoints(tombstone);
                    System.IO.Directory.CreateDirectory(archiveRoot);
                    await WriteMoveMarkerAsync(directoryName, cancellationToken).ConfigureAwait(false);
                    System.IO.Directory.Move(workspacePath, tombstone);
                    moved = true;
                }

                var deleted = (catalog.DeletedWorkspaces ?? [])
                    .Append(new DeletedWorkspaceEntry(catalog.Workspaces.Single(item => item.Id == id), deletedAt, moved ? directoryName : ""))
                    .OrderByDescending(item => item.DeletedAtMs)
                    .ToList();
                var retained = retentionLimit switch
                {
                    null => deleted.Where(item => item.Workspace.Id != id).ToList(),
                    -1 => deleted,
                    int limit => deleted.Take(limit).ToList()
                };
                var updated = catalog with
                {
                    Workspaces = catalog.Workspaces.Where(item => item.Id != id).ToList(),
                    WindowAssignments = catalog.WindowAssignments
                        .Where(pair => pair.Value != id)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    DeletedWorkspaces = retained
                };
                await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
                committed = true;
                if (moved) File.Delete(_moveMarker);
                foreach (var expired in deleted.Except(retained))
                {
                    if (string.IsNullOrEmpty(expired.DirectoryName)) continue;
                    var expiredPath = Path.Combine(archiveRoot, expired.DirectoryName);
                    try
                    {
                        EnsureNoReparsePoints(expiredPath);
                        System.IO.Directory.Delete(expiredPath, recursive: true);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        // A failed cleanup leaves the expired data isolated in the archive directory.
                    }
                }
                return updated;
            }
            catch when (!committed)
            {
                if (moved && System.IO.Directory.Exists(tombstone) && !System.IO.Directory.Exists(workspacePath))
                    System.IO.Directory.Move(tombstone, workspacePath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceCatalog> RestoreDeletedAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var deleted = (catalog.DeletedWorkspaces ?? []).FirstOrDefault(item => item.Workspace.Id == id)
                ?? throw new KeyNotFoundException("Удалённое рабочее пространство не найдено.");
            if (catalog.Workspaces.Any(item => item.Id == id)) throw new InvalidOperationException("Рабочее пространство уже восстановлено.");
            var restoredWorkspace = deleted.Workspace;
            if (catalog.Workspaces.Any(item => string.Equals(item.Name, restoredWorkspace.Name, StringComparison.OrdinalIgnoreCase)))
                restoredWorkspace = restoredWorkspace with { Name = CreateUniqueRestoredName(catalog, restoredWorkspace.Name) };

            var archiveRoot = Path.Combine(_directory, "deleted");
            var archivedPath = string.IsNullOrEmpty(deleted.DirectoryName)
                ? null : Path.Combine(archiveRoot, deleted.DirectoryName);
            var workspacePath = GetWorkspaceDirectory(id);
            var moved = archivedPath is not null;
            if (moved && !Directory.Exists(archivedPath))
                throw new InvalidDataException("Файлы удалённого пространства отсутствуют. Запись оставлена в истории.");
            if (moved)
            {
                EnsureNoReparsePoints(archivedPath!);
                if (Directory.Exists(workspacePath)) throw new IOException("Целевая папка пространства уже существует.");
                System.IO.Directory.CreateDirectory(_directory);
                await WriteMoveMarkerAsync(deleted.DirectoryName, cancellationToken).ConfigureAwait(false);
                System.IO.Directory.Move(archivedPath!, workspacePath);
            }

            var committed = false;
            try
            {
                var updated = catalog with
                {
                    Workspaces = [.. catalog.Workspaces, restoredWorkspace],
                    DeletedWorkspaces = catalog.DeletedWorkspaces!.Where(item => item.Workspace.Id != id).ToList()
                };
                await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
                committed = true;
                if (moved) File.Delete(_moveMarker);
                return updated;
            }
            catch when (!committed)
            {
                if (moved && System.IO.Directory.Exists(workspacePath) && !System.IO.Directory.Exists(archivedPath!))
                    System.IO.Directory.Move(workspacePath, archivedPath!);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public string GetWorkspaceDirectory(string id)
    {
        if (id == DefaultWorkspaceId) return Path.Combine(_directory, DefaultWorkspaceId);
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Идентификатор рабочего пространства некорректен.");
        return Path.Combine(_directory, id);
    }

    private async Task<WorkspaceCatalog> LoadCoreAsync(CancellationToken token)
    {
        if (!File.Exists(_indexPath))
        {
            if (File.Exists(_moveMarker)) throw new InvalidDataException("Отсутствует каталог для незавершённого перемещения пространства.");
            var initial = WorkspaceCatalog.CreateDefault();
            await SaveCoreAsync(initial, token).ConfigureAwait(false);
            return initial;
        }

        try
        {
            await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var catalog = Validate(await JsonSerializer.DeserializeAsync(stream, WorkspaceCatalogJsonContext.Default.WorkspaceCatalog, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Каталог рабочих пространств пуст."));
            await RecoverDirectoryMoveAsync(catalog, token).ConfigureAwait(false);
            return catalog;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Каталог рабочих пространств повреждён. Другие сохранённые данные не изменены.", error);
        }
    }

    private async Task<FileStream> LockCatalogAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "index.lock");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }

    private Task WriteMoveMarkerAsync(string directoryName, CancellationToken token) =>
        StorageService.AtomicWriteAsync(_moveMarker, System.Text.Encoding.UTF8.GetBytes(directoryName), token);

    private async Task RecoverDirectoryMoveAsync(WorkspaceCatalog catalog, CancellationToken token)
    {
        if (!File.Exists(_moveMarker)) return;
        EnsureNoReparsePoints(_moveMarker);
        if (new FileInfo(_moveMarker).Length > 128) throw new InvalidDataException("Журнал перемещения пространства повреждён.");
        var name = await File.ReadAllTextAsync(_moveMarker, token).ConfigureAwait(false);
        var parts = name.Split('-');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out _) || !long.TryParse(parts[1], out _)
            || !Guid.TryParseExact(parts[2], "N", out _))
            throw new InvalidDataException("Журнал перемещения пространства повреждён.");

        var live = GetWorkspaceDirectory(parts[0]);
        var archived = Path.Combine(_directory, "deleted", name);
        var active = catalog.Workspaces.Any(entry => entry.Id == parts[0]);
        var retained = catalog.DeletedWorkspaces?.Any(entry => entry.DirectoryName == name) == true;
        if (active || retained)
        {
            var target = active ? live : archived;
            var source = active ? archived : live;
            if (Directory.Exists(target) == Directory.Exists(source))
                throw new InvalidDataException("Не удалось однозначно восстановить папку рабочего пространства.");
            if (!Directory.Exists(target))
            {
                EnsureNoReparsePoints(source);
                EnsureNoReparsePoints(target);
                Directory.Move(source, target);
            }
        }
        File.Delete(_moveMarker);
    }

    private Task SaveCoreAsync(WorkspaceCatalog catalog, CancellationToken token) =>
        StorageService.WriteJsonAsync(_indexPath, Validate(catalog), WorkspaceCatalogJsonContext.Default.WorkspaceCatalog, token);

    private WorkspaceCatalog Validate(WorkspaceCatalog catalog)
    {
        if (catalog.Workspaces is null || catalog.WindowAssignments is null || catalog.Workspaces.Count is < 1 or > MaximumWorkspaceCount
            || catalog.Workspaces.Any(item => item is null))
            throw new InvalidDataException("Каталог рабочих пространств содержит недопустимое число записей.");
        if (!catalog.Workspaces.Any(item => item.Id == DefaultWorkspaceId && item.Name == "По умолчанию"))
            throw new InvalidDataException("В каталоге отсутствует пространство «По умолчанию».");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in catalog.Workspaces)
        {
            if (item is null || (item.Id != DefaultWorkspaceId && !Guid.TryParseExact(item.Id, "N", out _))
                || !ids.Add(item.Id) || !names.Add(ValidateStoredName(item.Name)))
                throw new InvalidDataException("Каталог рабочих пространств содержит повторяющиеся или некорректные записи.");
        }

        foreach (var assignment in catalog.WindowAssignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Key) || !ids.Contains(assignment.Value))
                throw new InvalidDataException("Назначение рабочего пространства окну некорректно.");
        }

        foreach (var deleted in catalog.DeletedWorkspaces ?? [])
        {
            if (deleted is null || deleted.Workspace is null || deleted.DirectoryName is null || deleted.Workspace.Id == DefaultWorkspaceId
                || !Guid.TryParseExact(deleted.Workspace.Id, "N", out _) || ids.Contains(deleted.Workspace.Id)
                || (deleted.DirectoryName.Length != 0 && (deleted.DirectoryName.Any(character => !Uri.IsHexDigit(character) && character != '-')
                    || !deleted.DirectoryName.StartsWith(deleted.Workspace.Id + "-", StringComparison.Ordinal))))
                throw new InvalidDataException("История удалённых пространств содержит некорректную запись.");
            _ = ValidateStoredName(deleted.Workspace.Name);
        }

        return catalog;
    }

    private static string ValidateName(string name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is < 1 or > MaximumNameLength || value.Any(char.IsControl))
            throw new ArgumentException("Имя рабочего пространства должно содержать от 1 до 80 печатных символов.", nameof(name));
        return value;
    }

    private static string ValidateStoredName(string name)
    {
        try { return ValidateName(name); }
        catch (ArgumentException error) { throw new InvalidDataException("Имя рабочего пространства в каталоге некорректно.", error); }
    }

    private static void EnsureNameAvailable(WorkspaceCatalog catalog, string name, string? exceptId = null)
    {
        if (catalog.Workspaces.Any(item => item.Id != exceptId && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Рабочее пространство с таким именем уже существует.");
    }

    private static string CreateUniqueRestoredName(WorkspaceCatalog catalog, string originalName)
    {
        for (var suffixIndex = 1; ; suffixIndex++)
        {
            var suffix = suffixIndex == 1 ? " (восстановлено)" : $" (восстановлено {suffixIndex})";
            var prefixLength = Math.Min(originalName.Length, MaximumNameLength - suffix.Length);
            if (prefixLength > 0 && char.IsHighSurrogate(originalName[prefixLength - 1])) prefixLength--;
            var candidate = originalName[..prefixLength].TrimEnd() + suffix;
            if (!catalog.Workspaces.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private void EnsureNoReparsePoints(string path)
    {
        var root = Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь рабочего пространства выходит за пределы каталога.");
        for (var current = target; current.StartsWith(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            if (File.Exists(current) || System.IO.Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Рабочее пространство содержит ссылку на другой каталог.");
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, _directory, StringComparison.OrdinalIgnoreCase)) break;
        }

        if (System.IO.Directory.Exists(target))
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(target);
            while (pendingDirectories.TryPop(out var currentDirectory))
            {
                foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(currentDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Рабочее пространство содержит ссылку на другой каталог.");
                    if ((attributes & FileAttributes.Directory) != 0) pendingDirectories.Push(child);
                }
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WorkspaceCatalog))]
internal partial class WorkspaceCatalogJsonContext : JsonSerializerContext { }
