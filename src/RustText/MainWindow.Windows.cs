using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Document;

namespace RustText.Demo;

public partial class MainWindow
{
    private readonly WorkspaceSession _workspace;
    private readonly bool _restoreWorkspace;
    private bool _isDetachedWindow;
    private bool _workspaceSwitchPending;
    private int _closedTabRestoreOperations;

    private sealed class WorkspaceSession
    {
        public StorageService Storage { get; } = StorageService.OpenDefault();
        public WorkspaceCatalogStore CatalogStore { get; } = new(StorageService.GetProfileBaseDirectory());
        public WorkspaceCatalog? Catalog;
        public SemaphoreSlim SaveGate { get; } = new(1, 1);
        public SemaphoreSlim WorkspaceSwitchGate { get; } = new(1, 1);
        public SemaphoreSlim CloseGate { get; } = new(1, 1);
        public SemaphoreSlim SettingsGate { get; } = new(1, 1);
        public UserSettings Settings { get; set; } = new();
        public bool SettingsReady { get; set; }
        public List<MainWindow> Windows { get; } = [];
        public Dictionary<string, List<SessionTab>> ClosedTabs { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string WorkspaceId, string WindowId), StorageService> SessionStores { get; } = [];
        public HashSet<(string WorkspaceId, string WindowId)> SharedSessionAliases { get; } = [];
        public Dictionary<StorageService, DetachedDraftRecovery> DetachedDrafts { get; } = [];
        public HashSet<long> ClosedTabOperations { get; } = [];
        public Dictionary<string, Dictionary<long, SavedLiveDraft>> LiveDrafts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int?> LastCheckpoints { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UnavailableSessions { get; } = new(StringComparer.Ordinal);
        public long NextId;
        public TabDrag? Drag;

        public StorageService GetStorage(string workspaceId, string windowId, bool shareDefaultSession = false)
        {
            if (workspaceId == WorkspaceCatalogStore.DefaultWorkspaceId
                && (windowId == WorkspaceCatalogStore.PrimaryWindowId || shareDefaultSession))
                return Storage;
            var key = (workspaceId, windowId);
            if (SessionStores.TryGetValue(key, out var storage)) return storage;
            storage = StorageService.OpenWorkspace(Storage.ProfileBaseDirectory, workspaceId, windowId);
            SessionStores.Add(key, storage);
            return storage;
        }

        public void ShareSession(string workspaceId, string windowId, StorageService storage)
        {
            var key = (workspaceId, windowId);
            SessionStores[key] = storage;
            SharedSessionAliases.Add(key);
        }

        public void ReleaseSharedSession(string workspaceId, string windowId, StorageService storage)
        {
            var key = (workspaceId, windowId);
            if (SessionStores.TryGetValue(key, out var existing) && ReferenceEquals(existing, storage))
            {
                SessionStores.Remove(key);
                SharedSessionAliases.Remove(key);
            }
        }

        public List<SessionTab> GetClosedTabs(StorageService storage)
        {
            var key = StorageCacheKey(storage);
            if (!ClosedTabs.TryGetValue(key, out var tabs)) ClosedTabs.Add(key, tabs = []);
            return tabs;
        }

        public Dictionary<long, SavedLiveDraft> GetLiveDrafts(string workspaceId)
        {
            if (!LiveDrafts.TryGetValue(workspaceId, out var drafts)) LiveDrafts.Add(workspaceId, drafts = []);
            return drafts;
        }

        public int? GetLastCheckpoint(string workspaceId) =>
            LastCheckpoints.GetValueOrDefault(workspaceId);

        public void SetLastCheckpoint(string workspaceId, int? fingerprint) =>
            LastCheckpoints[workspaceId] = fingerprint;
    }

    private sealed record WorkspaceViewState(Dictionary<long, WorkTab> Tabs, Group Root, Group Active, bool Home,
        double Width, double Height, bool Maximized, bool WordWrap, double FontSize, StorageService Storage, List<SessionTab> ClosedTabs);

    private sealed record DetachedDraftRecovery(StorageService PrimaryStorage)
    {
        public Dictionary<long, long?> DestinationIds { get; } = [];
    }

    private WorkspaceViewState CaptureWorkspaceView() => new(_tabs, _root, _active, _home,
        Width, Height, WindowState == WindowState.Maximized, _wordWrap, _fontSize, SessionStorage, _closedTabs);

    private StorageService SessionStorage => _startEmpty
        ? _workspace.GetStorage(WorkspaceCatalogStore.DefaultWorkspaceId, EmptyStartupWindowId)
        : _workspace.GetStorage(_workspaceId, _windowId, _shareDefaultSession);
    private static string StorageCacheKey(StorageService storage) => Path.GetFullPath(storage.RootDirectory)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    private string WorkspaceCacheKey => StorageCacheKey(SessionStorage);

    private IEnumerable<WorkTab> LoadedWorkspaceTabs() =>
        _tabs.Values.Concat(_workspaceViews.Values.SelectMany(view => view.Tabs.Values)).Distinct();

    private sealed record SavedLiveDraft(WeakReference<TextDocument> Document, long Revision, SessionTab Saved);

    internal MainWindow? FindOtherWindow() =>
        _workspace.Windows.LastOrDefault(window => !ReferenceEquals(window, this) && !window._closed && window.IsVisible);

    private MainWindow? FindOtherSessionWindow() =>
        _workspace.Windows.LastOrDefault(window => !ReferenceEquals(window, this) && !window._closed
            && window.IsVisible && SharesSessionWith(window));

    private bool SharesSessionWith(MainWindow window) =>
        ReferenceEquals(SessionStorage, window.SessionStorage)
        || string.Equals(SessionStorage.RootDirectory, window.SessionStorage.RootDirectory, StringComparison.OrdinalIgnoreCase);

    private bool HasRestorableWindowAssignment() =>
        _workspace.Catalog?.WindowAssignments.GetValueOrDefault(_windowId) is { } assignedWorkspace
        && !(_shareDefaultSession && assignedWorkspace == WorkspaceCatalogStore.DefaultWorkspaceId)
        && !_workspace.SharedSessionAliases.Contains((assignedWorkspace, _windowId));

    private HashSet<StorageService> RestorableAssignedStores(MainWindow[] windows)
    {
        var assignedWindows = windows.Where(window => window.HasRestorableWindowAssignment())
            .Select(window => window._windowId).ToHashSet(StringComparer.Ordinal);
        return _workspace.SessionStores
            .Where(pair => assignedWindows.Contains(pair.Key.WindowId) && !_workspace.SharedSessionAliases.Contains(pair.Key))
            .Select(pair => pair.Value).ToHashSet();
    }

    private void RestoreDetachedDraftTracking(StorageService storage, string workspaceId, SessionState state)
    {
        if (state.DetachedDrafts is not { Count: > 0 } drafts) return;
        var primary = _workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId);
        if (ReferenceEquals(storage, primary)) return;
        foreach (var (id, destinationId) in drafts)
        {
            RecordDetachedDraft(id, (storage, primary), destinationId);
            _workspace.NextId = Math.Max(_workspace.NextId, Math.Max(id, destinationId ?? 0));
        }
    }

    private async Task RestoreAssignedWorkspaceWindowsAsync()
    {
        if (_windowId != WorkspaceCatalogStore.PrimaryWindowId) return;
        _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        foreach (var assignment in _workspace.Catalog.WindowAssignments.Where(item => item.Key != WorkspaceCatalogStore.PrimaryWindowId).ToArray())
        {
            if (_workspace.Windows.Any(window => !window._closed && window._windowId == assignment.Key)) continue;
            var window = new MainWindow([], _workspace, assignment.Value, assignment.Key)
            {
                _recent = _recent
            };
            App.TrackWindow(window);
            window.Show();
        }
    }

    private IEnumerable<WorkTab> TabsInVisualOrder()
    {
        var byItem = _tabs.Values.ToDictionary(tab => tab.Item);
        return Groups().SelectMany(group => group.Tabs.Items.OfType<TabItem>()).Select(item => byItem[item]);
    }

    private void OpenTabWindow(WorkTab tab, PixelPoint? dropPoint = null)
    {
        if (!CanDragTab(tab)) return;
        if (_startEmpty && !CanTransferStoredDraft(tab, sharesStorage: false)) return;

        var workspaceId = _workspaceId;
        var windowId = "window:" + Guid.NewGuid().ToString("N");
        var sourceStorage = SessionStorage;
        var primaryStorage = _workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId);
        var independent = !_startEmpty && !ReferenceEquals(sourceStorage, primaryStorage);
        var sharedStorage = independent || workspaceId != WorkspaceCatalogStore.DefaultWorkspaceId ? sourceStorage : null;
        if (sharedStorage is not null)
            _workspace.ShareSession(workspaceId, windowId, sharedStorage);
        var window = new MainWindow([], _workspace, workspaceId, windowId,
            shareDefaultSession: sharedStorage is null, restoreWorkspace: false)
        {
            _isDetachedWindow = true,
            _fontSize = _fontSize,
            _wordWrap = _wordWrap,
            _recent = _recent
        };
        if (sharedStorage is not null)
            window.Closed += (_, _) => _workspace.ReleaseSharedSession(workspaceId, windowId, sharedStorage);
        if (dropPoint is { } point)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var x = point.X - 65;
            var y = point.Y - 18;
            if (Screens.ScreenFromPoint(point) is { } screen)
            {
                var work = screen.WorkingArea;
                window.Width = Math.Min(window.Width, Math.Max(window.MinWidth, work.Width / screen.Scaling));
                window.Height = Math.Min(window.Height, Math.Max(window.MinHeight, work.Height / screen.Scaling));
                var width = (int)Math.Ceiling(window.Width * screen.Scaling);
                var height = (int)Math.Ceiling(window.Height * screen.Scaling);
                x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - width));
                y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - height));
            }
            window.Position = new PixelPoint(x, y);
        }

        App.TrackWindow(window);
        window.Show();
        try
        {
            TransferTab(tab, window, window._active, null);
        }
        catch
        {
            window.Close();
            throw;
        }

        window.Activate();
    }

    private void TransferTab(WorkTab tab, MainWindow destination, Group group, WorkTab? before)
    {
        if (tab.Closing || tab.Saving || tab.Log?.HasPendingEdit == true || !_tabs.ContainsKey(tab.Id) || ReferenceEquals(tab, before)) return;
        if (ReferenceEquals(this, destination))
        {
            MoveTab(tab, group, before);
            return;
        }
        if (!CanTransferStoredDraft(tab, SharesSessionWith(destination))) return;

        var detachedRecovery = destination.PrepareDetachedDraftRecovery();
        var saved = CaptureTab(tab);
        var copy = destination.AddTab(tab.Id, group, select: false);
        try
        {
            destination.SetDeferred(copy, saved);
            if (tab.Editor is { } editor)
            {
                destination.AttachEditor(copy, editor.Document);
                copy.Deferred = null;
                copy.Dirty = tab.Dirty;
                copy.RestoredDirty = tab.RestoredDirty;
                copy.Revision = tab.Revision;
                copy.DiskStatus = tab.DiskStatus;
                copy.LastWorked = tab.LastWorked;
                RestoreEditorView(copy, saved);
                destination.UpdateTab(copy);
            }
            destination.MoveTab(copy, group, before);
        }
        catch
        {
            destination.RemoveTab(copy);
            throw;
        }

        RemoveTab(tab);
        if (detachedRecovery is { } recoveryTarget)
            destination.RecordDetachedDraft(tab.Id, recoveryTarget);
        if (_tabs.Count == 0 && !_closing && destination.IsVisible)
        {
            Close();
        }
    }

    private bool CanTransferStoredDraft(WorkTab tab, bool sharesStorage)
    {
        if (sharesStorage || tab.Editor is not null || (tab.Recovery ?? tab.Deferred)?.DraftRef is null)
            return true;

        SetStatus(tab.Recovery is null
            ? "Сначала откройте сохранённый черновик, чтобы перенести его в другой сеанс."
            : "Для переноса постраничного черновика в другой сеанс используйте «Сохранить копию…».");
        return false;
    }

    private (StorageService Source, StorageService Primary)? PrepareDetachedDraftRecovery()
    {
        if (!_isDetachedWindow || _startEmpty) return null;

        var source = SessionStorage;
        var primary = _workspace.GetStorage(_workspaceId, WorkspaceCatalogStore.PrimaryWindowId);
        if (string.Equals(StorageCacheKey(source), StorageCacheKey(primary), StringComparison.OrdinalIgnoreCase))
            return null;
        if (_workspace.DetachedDrafts.TryGetValue(source, out var existing)
            && !string.Equals(StorageCacheKey(existing.PrimaryStorage), StorageCacheKey(primary), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Окно черновика уже связано с другим рабочим пространством.");
        return (source, primary);
    }

    private void RecordDetachedDraft(long tabId, (StorageService Source, StorageService Primary) target, long? destinationId = null)
    {
        if (!_workspace.DetachedDrafts.TryGetValue(target.Source, out var recovery))
            _workspace.DetachedDrafts.Add(target.Source, recovery = new DetachedDraftRecovery(target.Primary));
        recovery.DestinationIds.TryAdd(tabId, destinationId);
    }

    private void TrackDetachedDraft(WorkTab tab)
    {
        if (PrepareDetachedDraftRecovery() is { } target)
            RecordDetachedDraft(tab.Id, target);
    }

}
