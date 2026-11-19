using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RustText.Demo.Editor;
using RustText.Demo.Logs;

namespace RustText.Demo;

public partial class MainWindow
{
    private const string EmptyStartupWindowId = "window:fffffffffffffffffffffffffffffff0";
    private readonly StorageService _storage;
    private List<SessionTab> _closedTabs = [];
    private IReadOnlyList<RecentFileEntry> _recent = [];
    private MenuItem? _recentMenu;
    private MenuItem? _closedFilesMenu;
    private MenuItem? _closedDraftsMenu;
    private Action? _refreshHome;
    private DateTimeOffset _lastHomeRefresh;
    private SessionState? _pendingSession;
    private bool _validatingStartupSession;
    private bool _mountingSessionTabs;
    private bool _emptyStartupPreserveAllHistory;

    private async Task RestoreSessionAsync()
    {
        _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        _validatingStartupSession = true;
        try
        {
            if (!_workspace.SettingsReady)
            {
                try
                {
                    _workspace.Settings = await _storage.LoadSettingsAsync(_lifetime.Token);
                }
                finally
                {
                    _workspace.SettingsReady = true;
                    ApplyWorkspaceTheme();
                }
            }
            _recent = await _storage.GetRecentFilesAsync(_lifetime.Token);
        }
        catch (Exception error) when (IsUserError(error))
        {
            SetStatus(error.Message);
        }

        if (_windowId == WorkspaceCatalogStore.PrimaryWindowId
            && Settings.WorkspaceStartupMode == WorkspaceStartupMode.Specific
            && Settings.StartupWorkspaceId is { } preferred
            && _workspace.Catalog.Workspaces.Any(item => item.Id == preferred))
        {
            _workspaceId = preferred;
        }
        else if (_workspace.Catalog.WindowAssignments.TryGetValue(_windowId, out var assignedWorkspace))
        {
            _workspaceId = assignedWorkspace;
        }
        _startEmpty = _windowId == WorkspaceCatalogStore.PrimaryWindowId
            && Settings.WorkspaceStartupMode == WorkspaceStartupMode.Empty;
        if (_startEmpty)
        {
            _workspaceId = WorkspaceCatalogStore.DefaultWorkspaceId;
        }
        _closedTabs = _workspace.GetClosedTabs(SessionStorage);

        var restore = _startEmpty
            ? SessionStorage.LoadSessionAsync(_lifetime.Token)
            : SessionStorage.LoadSessionAsync(_lifetime.Token, ShowPendingSessionAsync);

        SessionState? state;
        try { state = await restore; }
        catch (Exception error) when (_startEmpty && IsUserError(error))
        {
            state = null;
            _workspace.UnavailableSessions.Add(WorkspaceCacheKey);
            _workspaceLoadWarning = $"Черновики пустого окна не удалось прочитать. {error.Message}";
            SetStatus(_workspaceLoadWarning);
        }
        finally { _validatingStartupSession = false; }
        if (_closed) return;

        var restoredState = state ?? _pendingSession;
        if (Settings.WordWrap is null || Settings.FontSize is null)
        {
            await SaveWorkspaceSettingsAsync(settings => settings with
            {
                WordWrap = settings.WordWrap ?? restoredState?.Wrap ?? true,
                FontSize = settings.FontSize ?? restoredState?.FontSize ?? FocusTheme.EditorFontSize
            });
        }
        _wordWrap = Settings.WordWrap ?? true;
        _fontSize = Settings.FontSize ?? FocusTheme.EditorFontSize;
        ApplyTextAppearance();

        if (_startEmpty)
        {
            if (state is null && SessionStorage.HasSessionCheckpoint)
            {
                _workspace.UnavailableSessions.Add(WorkspaceCacheKey);
                _workspaceLoadWarning ??= "Черновики пустого окна не удалось прочитать. Их файлы оставлены без изменений.";
                SetStatus(_workspaceLoadWarning);
            }
            else if (state is not null)
            {
                _workspaceLoadWarning = null;
                AddEmptyStartupHistory(state);
            }
            else _workspaceLoadWarning = null;
            RefreshHistoryMenus();
            return;
        }

        if (state is null && _pendingSession is null)
        {
            if (SessionStorage.HasSessionCheckpoint)
            {
                _workspace.UnavailableSessions.Add(WorkspaceCacheKey);
                _workspaceLoadWarning = "Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.";
                SetStatus(_workspaceLoadWarning);
            }
            RefreshHistoryMenus();
            return;
        }

        if (state is not null) _closedTabs.AddRange((state.ClosedTabs ?? []).Take(100));
        if (state is null || !ReferenceEquals(state, _pendingSession))
        {
            MountSessionTabs(state ?? new SessionState([]), pending: false);
        }
        else
        {
            foreach (var tab in _tabs.Values.Where(tab => tab.CheckingSession))
            {
                tab.CheckingSession = false;
                tab.Item.Content = DeferredContent(tab);
            }
        }
        _pendingSession = null;

        if (SelectedIn(_active) is { } selected)
            await GuardAsync(() => ActivateDeferredAsync(selected));

        RefreshHistoryMenus();
    }

    private void AddEmptyStartupHistory(SessionState state)
    {
        _emptyStartupPreserveAllHistory |= state.Tabs.Count > 0 || (state.ClosedTabs?.Count ?? 0) > 100;
        _workspace.NextId = Math.Max(_workspace.NextId,
            state.Tabs.Concat(state.ClosedTabs ?? []).Select(tab => tab.Id).DefaultIfEmpty().Max());
        var existingIds = _closedTabs.Select(tab => tab.Id).ToHashSet();
        var reopenedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var tab in state.Tabs)
        {
            if (existingIds.Add(tab.Id))
                _closedTabs.Insert(0, tab with { ClosedAtMs = tab.ClosedAtMs ?? reopenedAt });
        }
        foreach (var tab in state.ClosedTabs ?? [])
        {
            if (existingIds.Add(tab.Id)) _closedTabs.Add(tab);
        }
        if (!_emptyStartupPreserveAllHistory && _closedTabs.Count > 100) _closedTabs.RemoveRange(100, _closedTabs.Count - 100);
    }

    private bool PreserveEmptyHistory(StorageService storage) =>
        _emptyStartupPreserveAllHistory && _workspace.SessionStores.TryGetValue(
            (WorkspaceCatalogStore.DefaultWorkspaceId, EmptyStartupWindowId), out var scratch) && ReferenceEquals(scratch, storage);

    private async Task ShowPendingSessionAsync(SessionState state)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_closed || _pendingSession is not null) return;
            _pendingSession = state;
            MountSessionTabs(state, pending: true);
        });
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private void MountSessionTabs(SessionState state, bool pending)
    {
        _mountingSessionTabs = true;
        try
        {
            foreach (var tab in _tabs.Values.Where(tab => tab.CheckingSession).ToArray()) RemoveTab(tab);
            var liveTabs = TabsInVisualOrder().ToArray();
            foreach (var tab in liveTabs) tab.Group.Tabs.Items.Remove(tab.Item);

            Width = Math.Clamp(state.WindowWidth, MinWidth, 4000);
            Height = Math.Clamp(state.WindowHeight, MinHeight, 2400);
            WindowState = state.Maximized ? WindowState.Maximized : WindowState.Normal;

            _wordWrap = Settings.WordWrap ?? state.Wrap;
            _fontSize = Settings.FontSize ?? Math.Clamp(state.FontSize, 8, 48);
            _workspace.NextId = Math.Max(_workspace.NextId, state.Tabs.Concat(state.ClosedTabs ?? []).Select(tab => tab.Id).DefaultIfEmpty().Max());
            _root = state.Layout is { } layout ? RestoreLayout(layout) : MakeGroup();
            var groups = Groups().ToArray();
            _active = groups.FirstOrDefault(group => group.Id == state.ActiveGroupId) ?? groups[0];
            foreach (var saved in state.Tabs)
            {
                if (_tabs.ContainsKey(saved.Id)) continue;
                var group = FindSavedGroup(state.Layout, saved.Id);
                var tab = AddTab(saved.Id, groups.FirstOrDefault(current => current.Id == group) ?? _active, false);
                tab.CheckingSession = pending;
                SetDeferred(tab, saved);
            }

            foreach (var group in groups)
            {
                if (FindSavedSelection(state.Layout, group.Id) is { } id && _tabs.TryGetValue(id, out var tab))
                    group.Tabs.SelectedItem = tab.Item;
            }

            if (state.ActiveGroupId is null && state.Active >= 0 && state.Active < state.Tabs.Count
                && _tabs.TryGetValue(state.Tabs[state.Active].Id, out var active))
            {
                _active = active.Group;
                _active.Tabs.SelectedItem = active.Item;
            }

            foreach (var tab in liveTabs)
            {
                tab.Group = _active;
                _active.Tabs.Items.Add(tab.Item);
            }
            if (liveTabs.LastOrDefault() is { } live) _active.Tabs.SelectedItem = live.Item;
            ShowGroups();
        }
        finally { _mountingSessionTabs = false; }
    }

    private Group RestoreLayout(WorkspaceLayout state)
    {
        _workspace.NextId = Math.Max(_workspace.NextId, state.GroupId);
        if (state.First is not { } first || state.Second is not { } second)
        {
            return MakeGroup(state.GroupId);
        }

        return new Group(state.GroupId)
        {
            Axis = state.Split == "Vertical" ? Orientation.Vertical : Orientation.Horizontal,
            Ratio = Math.Clamp(state.Ratio, .1, .9),
            First = RestoreLayout(first),
            Second = RestoreLayout(second)
        };
    }

    private static long? FindSavedGroup(WorkspaceLayout? state, long tabId)
    {
        if (state is null)
        {
            return null;
        }

        return state.Tabs?.Contains(tabId) == true ? state.GroupId
            : FindSavedGroup(state.First, tabId) ?? FindSavedGroup(state.Second, tabId);
    }

    private static long? FindSavedSelection(WorkspaceLayout? state, long groupId)
    {
        if (state is null)
        {
            return null;
        }

        return state.GroupId == groupId ? state.ActiveTab
            : FindSavedSelection(state.First, groupId) ?? FindSavedSelection(state.Second, groupId);
    }

    private WorkspaceLayout CaptureLayout(Group group, Dictionary<long, WorkTab>? tabs = null)
    {
        tabs ??= _tabs;
        return group.Axis is { } axis
            ? new WorkspaceLayout(group.Id, Split: axis.ToString(), Ratio: group.Ratio,
                First: CaptureLayout(group.First!, tabs), Second: CaptureLayout(group.Second!, tabs))
            : new WorkspaceLayout(group.Id, group.Tabs.Items.OfType<TabItem>()
                .Select(item => tabs.Values.First(tab => ReferenceEquals(tab.Item, item)).Id).ToArray(),
                group.Tabs.SelectedItem is TabItem selected
                    ? tabs.Values.FirstOrDefault(tab => ReferenceEquals(tab.Item, selected))?.Id : null);
    }

    private SessionTab CaptureTab(WorkTab tab)
    {
        if (tab.Deferred is { } pending)
        {
            return pending;
        }

        if (tab.Recovery is { } recovered)
        {
            return tab.Log is { } preview
                ? recovered with
                {
                    DraftOffset = preview.CurrentOffset ?? recovered.DraftOffset,
                    LineCount = preview.KnownLineCount
                }
                : recovered;
        }

        var editor = tab.Editor;
        var log = tab.Log?.CaptureState() ?? (tab.PendingFileOpenMode is FileOpenMode.PagedEditor or FileOpenMode.Log
            ? new LogTabState(tab.Encoding, WordWrap: _wordWrap, AutoRefresh: tab.AutoRefresh,
                EditPages: tab.PendingFileOpenMode == FileOpenMode.PagedEditor) : null);
        var caret = editor?.CaretOffset ?? 0;
        var selection = editor is null ? default : MultiCaretEditing.Primary(editor.TextArea);
        var selectionStart = selection.Start;
        var selectionLength = selection.Length;
        var lineEndings = tab.OriginalEndings.IsDefaultOrEmpty ? null : tab.OriginalEndings.Select(value => (byte)value).ToArray();
        return new SessionTab(tab.Id, tab.Path, tab.Encoding, selectionStart, selectionLength,
            caret < selectionStart + selectionLength, editor?.HorizontalOffset ?? 0, editor?.VerticalOffset ?? 0,
            LogOffset: log?.Offset, LineCount: editor?.Document.LineCount,
            LastWorkedMs: tab.LastWorked.ToUnixTimeMilliseconds(), LineEnding: tab.Ending,
            LineEndingMap: lineEndings, Stamp: tab.Stamp, Log: log)
        {
            JsonValidationDisabled = tab.JsonDisabled,
            AutoRefresh = tab.AutoRefresh,
            CustomTitle = tab.CustomTitle,
            UntitledName = tab.Path is null ? UntitledName(tab) : null
        };
    }

    private Task SaveSessionAsync() => SaveWorkspaceSessionAsync(excludeWindow: null);

    private async Task PreserveDetachedDraftsOnCloseAsync()
    {
        var trackedViews = _workspaceViews.Values.Prepend(CaptureWorkspaceView())
            .Where(view => _workspace.DetachedDrafts.ContainsKey(view.Storage))
            .GroupBy(view => view.Storage).ToArray();
        if (trackedViews.Length == 0) return;

        var token = _lifetime.Token;
        await _workspace.SaveGate.WaitAsync(token);
        try
        {
            foreach (var views in trackedViews)
            {
                var source = views.Key;
                var recovery = _workspace.DetachedDrafts[source];
                var otherViews = SessionViews(source, this).Any();
                var closingIds = views.SelectMany(view => view.Tabs.Keys).ToHashSet();
                var pending = recovery.DestinationIds.Keys.Where(id => !otherViews || closingIds.Contains(id)).ToHashSet();
                if (pending.Count == 0) continue;

                var saved = await source.LoadSessionAsync(token);
                if (saved is null && source.HasSessionCheckpoint)
                    throw new InvalidDataException("Не удалось прочитать сохранённый черновик вынесенной вкладки.");
                var drafts = (saved?.Tabs ?? []).Concat(saved?.ClosedTabs ?? [])
                    .Where(tab => pending.Contains(tab.Id) && tab.DraftRef is not null)
                    .DistinctBy(tab => tab.Id).ToArray();
                var savedIds = drafts.Select(tab => tab.Id).ToHashSet();
                if (views.SelectMany(view => view.Tabs.Values).Any(tab => pending.Contains(tab.Id)
                    && (tab.Dirty || tab.Recovery is not null || tab.Deferred?.DraftRef is not null || tab.Path is null && tab.Editor is not null)
                    && !savedIds.Contains(tab.Id)))
                    throw new IOException("Черновик вынесенной вкладки ещё не сохранён. Повторите закрытие окна.");
                if (drafts.Length > 0) await MergeDetachedDraftsAsync(source, recovery, drafts, token);

                foreach (var draft in drafts) recovery.DestinationIds.Remove(draft.Id);
                if (!otherViews || recovery.DestinationIds.Count == 0) _workspace.DetachedDrafts.Remove(source);
            }
        }
        finally { _workspace.SaveGate.Release(); }
    }

    private async Task MergeDetachedDraftsAsync(StorageService source, DetachedDraftRecovery recovery,
        SessionTab[] drafts, CancellationToken token)
    {
        var primary = recovery.PrimaryStorage;
        var checkpoint = await primary.LoadSessionAsync(token);
        if (checkpoint is null && primary.HasSessionCheckpoint)
            throw new InvalidDataException("Не удалось прочитать основной сеанс рабочего пространства для восстановления черновика.");
        checkpoint ??= new SessionState([]);

        var liveViews = SessionViews(primary, null).ToList();
        var liveHistory = liveViews.Count > 0 ? _workspace.GetClosedTabs(primary) : null;
        var existing = liveHistory ?? checkpoint.ClosedTabs ?? [];
        _workspace.NextId = Math.Max(_workspace.NextId, checkpoint.Tabs.Concat(checkpoint.ClosedTabs ?? []).Concat(existing)
            .Select(tab => tab.Id).DefaultIfEmpty().Max());

        var readers = new Dictionary<long, Func<TextReader>>();
        var recovered = new List<SessionTab>();
        var closedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var draft in drafts)
        {
            var id = recovery.DestinationIds[draft.Id] ?? NextId();
            recovery.DestinationIds[draft.Id] = id;
            var path = source.GetDraftPath(draft)!;
            readers.Add(id, () => new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false));
            recovered.Add(draft with { Id = id, DraftRef = null, DraftSha256 = null, ClosedAtMs = closedAt });
        }

        if (liveHistory is not null)
        {
            // Include live tabs and their drafts so checkpoint pruning keeps any newly opened recovery files.
            var committed = (await SaveSessionViewsAsync(primary, liveViews, forget: null, force: true, recovered, readers))!;
            liveHistory.RemoveAll(tab => readers.ContainsKey(tab.Id));
            liveHistory.InsertRange(0, committed.ClosedTabs!.Where(tab => readers.ContainsKey(tab.Id)));
            if (!liveViews[0].Owner.PreserveEmptyHistory(primary) && liveHistory.Count > 100)
                liveHistory.RemoveRange(100, liveHistory.Count - 100);
            RefreshHistoryMenus();
        }
        else
        {
            // Only the closed history changes when the primary session is on disk.
            var updated = checkpoint with { ClosedTabs = [.. recovered, .. existing.Where(tab => !readers.ContainsKey(tab.Id))] };
            await primary.SaveSessionWithDraftReadersAsync(updated, readers, token);
        }
        _workspace.SetLastCheckpoint(StorageCacheKey(primary), null);
    }

    private async Task SaveWorkspaceSessionAsync(MainWindow? excludeWindow, SessionTab? forgetClosedTab = null)
    {
        var activeStorage = SessionStorage;
        var activeTabs = _tabs;
        if (forgetClosedTab is null && !_restoreFinished && !_closing)
            return;

        await _workspace.SaveGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(activeStorage, SessionStorage) || !ReferenceEquals(activeTabs, _tabs)) return;
            var stores = _workspaceViews.Values.Select(view => view.Storage).Prepend(activeStorage).Distinct().ToArray();
            foreach (var storage in stores)
            {
                if (_workspace.UnavailableSessions.Contains(StorageCacheKey(storage))) continue;
                var isActive = ReferenceEquals(storage, activeStorage);
                if (isActive && _startEmpty && _tabs.Count == 0 && forgetClosedTab is null) continue;
                var views = SessionViews(storage, isActive ? excludeWindow : null).ToList();
                if (views.Count == 0) continue;
                await SaveSessionViewsAsync(storage, views, isActive ? forgetClosedTab : null, isActive && _closing);
            }
        }
        finally { _workspace.SaveGate.Release(); }
    }

    private IEnumerable<(MainWindow Owner, WorkspaceViewState View)> SessionViews(StorageService storage, MainWindow? excludeWindow)
    {
        var windows = _workspace.Windows.Where(window => !window._closed && !ReferenceEquals(window, excludeWindow)).ToArray();
        // A shared default session keeps the visible primary layout first, followed by detached and hidden views.
        foreach (var window in windows.OrderByDescending(window => window._windowId == WorkspaceCatalogStore.PrimaryWindowId))
        {
            if (ReferenceEquals(window.SessionStorage, storage))
                yield return (window, window.CaptureWorkspaceView());
        }
        foreach (var window in windows)
        {
            foreach (var view in window._workspaceViews.Values.Where(view => ReferenceEquals(view.Storage, storage)))
                yield return (window, view);
        }
    }

    private async Task<SessionState?> SaveSessionViewsAsync(StorageService storage,
        List<(MainWindow Owner, WorkspaceViewState View)> views, SessionTab? forget, bool force,
        IReadOnlyList<SessionTab>? recovered = null, IReadOnlyDictionary<long, Func<TextReader>>? draftReaders = null)
    {
        var (owner, home) = views[0];
        var entries = views.SelectMany(pair =>
        {
            var byItem = pair.View.Tabs.Values.ToDictionary(tab => tab.Item);
            return pair.Owner.Groups(pair.View.Root).SelectMany(group => group.Tabs.Items.OfType<TabItem>())
                .Select(item => (pair.Owner, Tab: byItem[item]));
        }).ToArray();
        var tabs = entries.Select(entry => entry.Tab).ToArray();
        var recoveries = tabs.Select(tab => tab.Recovery).ToArray();
        var state = entries.Select(entry => entry.Owner.CaptureTab(entry.Tab)).ToList();
        var selected = home.Tabs.Values.FirstOrDefault(tab => ReferenceEquals(tab.Item, home.Active.Tabs.SelectedItem));
        var history = _workspace.GetClosedTabs(storage);
        var preserveHistory = owner.PreserveEmptyHistory(storage);
        var historyToSave = recovered is null ? history : recovered.Concat(history).DistinctBy(tab => tab.Id);
        var capturedHistory = historyToSave.Where(tab => tab.Id != forget?.Id).Take(preserveHistory ? int.MaxValue : 100).ToList();
        var session = new SessionState(state, capturedHistory, Math.Max(0, Array.IndexOf(tabs, selected)),
            double.IsFinite(home.Width) ? home.Width : 1080, double.IsFinite(home.Height) ? home.Height : 700,
            home.Maximized, home.WordWrap, home.FontSize, owner.CaptureLayout(home.Root, home.Tabs), home.Active.Id);
        var cacheKey = StorageCacheKey(storage);
        var fingerprint = CheckpointFingerprint(session, tabs);
        if (!force && forget is null && _workspace.GetLastCheckpoint(cacheKey) == fingerprint) return null;

        var existing = _workspace.GetLiveDrafts(cacheKey);
        Dictionary<long, Func<TextReader>> readers = draftReaders is null ? [] : new(draftReaders);
        var live = new Dictionary<long, (AvaloniaEdit.Document.TextDocument Document, long Revision)>();
        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (tab.Editor is not { } editor || !(tab.Dirty || tab.Path is null)) continue;
            var document = editor.Document;
            live.Add(tab.Id, (document, tab.Revision));
            if (existing.TryGetValue(tab.Id, out var prior) && prior.Revision == tab.Revision
                && prior.Document.TryGetTarget(out var previousDocument) && ReferenceEquals(previousDocument, document))
                state[i] = state[i] with { DraftRef = prior.Saved.DraftRef, DraftSha256 = prior.Saved.DraftSha256 };
            else
                readers.Add(tab.Id, document.CreateSnapshot().CreateReader);
        }

        var committed = await storage.SaveSessionWithDraftReadersAsync(session, readers, _lifetime.Token, preserveAllClosedTabs: preserveHistory);
        existing.Clear();
        for (var i = 0; i < tabs.Length; i++)
        {
            var saved = committed.Tabs[i];
            if (ReferenceEquals(tabs[i].Deferred, state[i])) tabs[i].Deferred = saved;
            if (recoveries[i] is { } recovery && ReferenceEquals(tabs[i].Recovery, recovery)) tabs[i].Recovery = saved;
            if (saved.DraftRef is not null && live.TryGetValue(saved.Id, out var document))
                existing.Add(saved.Id, new(new(document.Document), document.Revision, saved));
        }
        for (var i = 0; i < Math.Min(capturedHistory.Count, committed.ClosedTabs?.Count ?? 0); i++)
        {
            var index = history.FindIndex(tab => ReferenceEquals(tab, capturedHistory[i]));
            if (index >= 0) history[index] = committed.ClosedTabs![i];
        }
        if (forget is not null)
        {
            history.RemoveAll(tab => tab.Id == forget.Id);
            _workspace.SetLastCheckpoint(cacheKey, null);
            await storage.RemoveHistoryFromPreviousCheckpointAsync(forget.Id,
                forget.DraftRef is not null && committed.Tabs.All(tab => tab.Id != forget.Id), _lifetime.Token);
        }
        _workspace.SetLastCheckpoint(cacheKey, fingerprint);
        return committed;
    }

    private SessionTab? FindSavedLiveDraft(WorkTab tab) =>
        _workspace.GetLiveDrafts(WorkspaceCacheKey).TryGetValue(tab.Id, out var saved)
        && saved.Revision == tab.Revision
        && saved.Document.TryGetTarget(out var document)
        && ReferenceEquals(document, tab.Editor?.Document) ? saved.Saved : null;

    private static int CheckpointFingerprint(SessionState state, WorkTab[] tabs)
    {
        var hash = new HashCode();
        hash.Add(state.Active);
        hash.Add(state.ActiveGroupId);
        hash.Add(state.WindowWidth);
        hash.Add(state.WindowHeight);
        hash.Add(state.Maximized);
        hash.Add(state.Wrap);
        hash.Add(state.FontSize);
        AddLayout(state.Layout);
        for (var i = 0; i < tabs.Length; i++)
        {
            hash.Add(state.Tabs[i] with { LineEndingMap = null });
            hash.Add(tabs[i].Revision);
            hash.Add(tabs[i].Dirty);
        }

        foreach (var tab in state.ClosedTabs ?? [])
        {
            hash.Add(tab with { LineEndingMap = null });
        }

        return hash.ToHashCode();

        void AddLayout(WorkspaceLayout? node)
        {
            if (node is null)
            {
                return;
            }

            hash.Add(node.GroupId);
            hash.Add(node.ActiveTab);
            hash.Add(node.Split);
            hash.Add(node.Ratio);
            foreach (var id in node.Tabs ?? [])
            {
                hash.Add(id);
            }

            AddLayout(node.First);
            AddLayout(node.Second);
        }
    }

    private void SetDeferred(WorkTab tab, SessionTab saved)
    {
        tab.CustomTitle = saved.CustomTitle;
        tab.Path = saved.Path;
        tab.Encoding = saved.Encoding;
        tab.Ending = saved.LineEnding;
        tab.Stamp = saved.Stamp;
        tab.OriginalEndings = saved.LineEndingMap is { } markers
            ? markers.Select(value => (LineEndingKind)value).ToImmutableArray() : default;
        tab.JsonDisabled = saved.JsonValidationDisabled;
        tab.AutoRefresh = saved.AutoRefresh ?? true;
        tab.LastWorked = saved.LastWorkedMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.Now;
        tab.Deferred = saved;
        tab.Item.Content = DeferredContent(tab);
        UpdateTab(tab);
    }

    private Control DeferredContent(WorkTab tab) => tab.CheckingSession
        ? new TextBlock { Text = "Проверка сохранённой вкладки…", Margin = new Thickness(18) }
        : FocusButton(Button("Загрузить вкладку", () => Run(() => ActivateDeferredAsync(tab))), primary: true);

    private Task ActivateDeferredAsync(WorkTab tab) => ActivateDeferredCoreAsync(tab, allowDuringWorkspaceSwitch: false);

    private Task ActivateDeferredDuringWorkspaceSwitchAsync(WorkTab tab) =>
        ActivateDeferredCoreAsync(tab, allowDuringWorkspaceSwitch: true);

    private async Task ActivateDeferredCoreAsync(WorkTab tab, bool allowDuringWorkspaceSwitch)
    {
        if ((_workspaceSwitchPending && !allowDuringWorkspaceSwitch)
            || tab.Closing || tab.Deferred is not { } saved || tab.Loading || tab.CheckingSession || _closed)
        {
            return;
        }

        var storage = SessionStorage;
        var workspaceId = _workspaceId;
        var tabs = _tabs;
        bool IsCurrentTab() => _workspaceId == workspaceId && ReferenceEquals(_tabs, tabs)
            && tabs.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, tab);

        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        tab.Loading = true;
        tab.LoadCancellation = source;
        tab.Item.Content = LoadingSurface(source, "Восстановление вкладки…");
        try
        {
            var token = source.Token;
            var previousDeferred = saved;
            saved = await storage.ResolveCurrentDraftAsync(saved, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentTab()) return;
            if (ReferenceEquals(tab.Deferred, previousDeferred))
            {
                tab.Deferred = saved;
            }
            if ((saved.Log is not null || saved.LogOffset is not null) && saved.Path is { } logPath)
            {
                RestoreSavedLog(tab, logPath, saved);
            }
            else if (saved.DraftRef is not null)
            {
                var draftPath = storage.GetDraftPath(saved);
                if (draftPath is not null && new FileInfo(draftPath).Length > LineGeneratorThreshold)
                {
                    AttachPagedDraftEditor(tab, saved, draftPath);
                }
                else
                {
                    var draft = await storage.LoadDraftAsync(saved, token) ?? "";
                    if (!IsCurrentTab()) return;
                    if (draftPath is not null && IsOversizedEditorText(draft))
                    {
                        AttachPagedDraftEditor(tab, saved, draftPath);
                    }
                    else
                    {
                        if (!TryShareFileEditor(tab, saved.Encoding, draft))
                        {
                            AttachEditor(tab, draft);
                            tab.Dirty = draft.Length != 0 || tab.Path is not null;
                            tab.RestoredDirty = tab.Dirty;
                        }
                        if (tab.Stamp is null && tab.Path is { } original && File.Exists(original))
                        {
                            try
                            {
                                if (saved.LegacyStamp is { } legacy)
                                {
                                    var stamp = await DocumentFiles.GetStampAsync(original, token);
                                    if (!IsCurrentTab()) return;
                                    if (stamp.Length == legacy.Length && stamp.LastWriteUtc == legacy.LastWriteUtc
                                        && (legacy.CreatedUtc is null || File.GetCreationTimeUtc(original) == legacy.CreatedUtc))
                                    {
                                        tab.Stamp = stamp;
                                    }
                                }
                            }
                            catch (IOException)
                            {
                            }
                            catch (UnauthorizedAccessException)
                            {
                            }

                            tab.DiskStatus = tab.Stamp is null
                                ? "Проверьте исходник и сохраните черновик через «Сохранить как»." : null;
                        }
                    }
                }
            }
            else if (saved.Path is { } path)
            {
                if (!TryShareFileEditor(tab, saved.Encoding))
                {
                    if (new FileInfo(path).Length > DocumentFiles.MaximumDocumentBytes)
                        AttachLog(tab, path, saved.Encoding,
                            new LogTabState(saved.Encoding, WordWrap: _wordWrap, AutoRefresh: tab.AutoRefresh, EditPages: true));
                    else
                    {
                        var loaded = await DocumentFiles.ReadAsync(path, saved.Encoding, token);
                        if (!IsCurrentTab()) return;
                        AttachLoadedEditor(tab, loaded);
                    }
                }
            }
            else
            {
                AttachEditor(tab, "");
            }

            RestoreEditorView(tab, saved);

            tab.Deferred = null;
            UpdateTab(tab);
        }
        catch (Exception error)
        {
            if (IsCurrentTab())
            {
                var retry = FocusButton(Button("Повторить загрузку", () => Run(() => ActivateDeferredAsync(tab))), primary: true);
                if (saved.Path is { } missing && error is FileNotFoundException or DirectoryNotFoundException)
                {
                    tab.DiskStatus = $"Файл не найден. {missing}";
                    tab.Item.Content = DialogPanel(new TextBlock { Text = tab.DiskStatus, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, retry);
                }
                else tab.Item.Content = retry;
            }
            throw;
        }
        finally
        {
            tab.Loading = false;
            tab.LoadCancellation = null;
        }
    }

    private void RestoreSavedLog(WorkTab tab, string path, SessionTab saved)
    {
        var state = saved.Log ?? new LogTabState(saved.Encoding, saved.LogOffset ?? 0, WordWrap: _wordWrap);
        AttachLog(tab, path, state.Encoding, state);
    }

    private static void RestoreEditorView(WorkTab tab, SessionTab saved)
    {
        if (tab.Editor is not { } editor)
        {
            return;
        }

        var size = editor.Document.TextLength;
        var start = Math.Clamp(saved.SelectionStart, 0, size);
        var length = Math.Clamp(saved.SelectionLength, 0, size - start);
        if (saved.SelectionUsesUtf8)
        {
            var text = editor.Text;
            start = Utf8IndexToUtf16(text, saved.SelectionStart);
            var end = Utf8IndexToUtf16(text, (long)saved.SelectionStart + saved.SelectionLength);
            length = Math.Max(0, end - start);
        }

        editor.Select(start, length);
        editor.CaretOffset = saved.Reversed ? start : start + length;
        Dispatcher.UIThread.Post(() =>
        {
            if (editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            {
                scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(saved.ScrollX, saved.ScrollY));
            }
        }, DispatcherPriority.Loaded);
    }

    private const long LineGeneratorThreshold = 8L * 1024 * 1024;

    internal static int Utf8IndexToUtf16(string text, long byteOffset)
    {
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (byteOffset < rune.Utf8SequenceLength)
            {
                break;
            }

            byteOffset -= rune.Utf8SequenceLength;
            index += rune.Utf16SequenceLength;
        }

        return index;
    }

    private void AttachPagedDraftEditor(WorkTab tab, SessionTab saved, string draftPath, int? pageCaret = null,
        bool focus = false, long? byteCaret = null)
    {
        var name = saved.Path is { } path ? Path.GetFileName(path) : saved.UntitledName;
        var state = new LogTabState(TextEncodingKind.Utf8, saved.DraftOffset ?? 0, WordWrap: _wordWrap);
        var lineCount = saved.DraftOffset.HasValue ? saved.LineCount : null;
        AttachLog(tab, draftPath, TextEncodingKind.Utf8, state, name ?? "Черновик", lineCount, initialDraftCaretBytes: byteCaret);
        tab.Recovery = saved;
        UpdateTab(tab);
        var editor = tab.Log!;
        if (pageCaret is not null || focus) RestorePagedEditorCaret(tab, editor, pageCaret, focus);
        editor.CommitDraftPage = async (temporary, hash, offset, caret, focusEditor, token) =>
        {
            await _workspace.SaveGate.WaitAsync(token);
            try
            {
                var current = tab.Recovery ?? throw new IOException("Черновик недоступен.");
                var edited = SessionStorage.AdoptEditedDraft(current, temporary, hash) with { DraftOffset = offset, LineCount = null };
                AttachPagedDraftEditor(tab, edited, SessionStorage.GetDraftPath(edited)!, caret, focusEditor);
            }
            finally { _workspace.SaveGate.Release(); }
            await GuardAsync(async () =>
            {
                await SaveSessionAsync();
                if (ReferenceEquals(tab, ActiveTab)) SetStatus("Страница черновика сохранена.");
            });
        };
    }

    private void RestorePagedEditorCaret(WorkTab tab, LogView editor, int? pageCaret, bool focus) => Run(async () =>
    {
        await editor.InitialPageLoad;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(tab.Log, editor)) return;
            if (pageCaret is { } caret) editor.RestorePageCaret(caret, focus: false);
            if (focus && ReferenceEquals(tab, ActiveTab)) editor.FocusPageEditor();
        }, DispatcherPriority.Loaded);
    });

    private async Task SaveCurrentDraftCopyAsync(WorkTab tab)
    {
        if (tab.Log?.HasPendingEdit == true) throw new IOException("Сохраните страницу перед созданием копии.");
        if (tab.Recovery is not { } recovery) return;
        var current = await SessionStorage.ResolveCurrentDraftAsync(recovery, _lifetime.Token);
        if (ReferenceEquals(tab.Recovery, recovery)) tab.Recovery = current;
        if (SessionStorage.GetDraftPath(current) is { } path) await SaveDraftCopyAsync(path);
    }

    private async Task RememberClosedAsync(
        WorkTab tab,
        bool preserveUnsavedText = false,
        bool deferHistoryLimit = false)
    {
        await _workspace.SaveGate.WaitAsync(_lifetime.Token);
        try
        {
            var saved = CaptureTab(tab) with { ClosedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            if (tab.Editor is { } editor && (tab.Path is null || (preserveUnsavedText && tab.Dirty)))
            {
                if (FindSavedLiveDraft(tab) is { } existingDraft)
                {
                    saved = saved with { DraftRef = existingDraft.DraftRef, DraftSha256 = existingDraft.DraftSha256 };
                }
                else
                {
                    var snapshot = editor.Document.CreateSnapshot();
                    saved = await SessionStorage.SaveClosedDraftAsync(saved, snapshot.CreateReader, _lifetime.Token);
                }
            }

            _closedTabs.RemoveAll(item => item.Id == saved.Id);
            _closedTabs.Insert(0, saved);
            if (!deferHistoryLimit && !(_startEmpty && _emptyStartupPreserveAllHistory) && _closedTabs.Count > 100) _closedTabs.RemoveAt(_closedTabs.Count - 1);
        }
        finally { _workspace.SaveGate.Release(); }

        RefreshHistoryMenus();
    }

    private Task RestoreLastClosedAsync() => _closedTabs.Count > 0 ? RestoreClosedAsync(_closedTabs[0]) : Task.CompletedTask;

    private Task RestoreClosedAsync(SessionTab saved) => RestoreClosedCoreAsync(saved, allowDuringWorkspaceSwitch: false);

    private Task RestoreClosedWhileHoldingWorkspaceSwitchGateAsync(SessionTab saved) =>
        RestoreClosedCoreAsync(saved, allowDuringWorkspaceSwitch: true);

    private async Task RestoreClosedCoreAsync(SessionTab saved, bool allowDuringWorkspaceSwitch)
    {
        if ((_workspaceSwitchPending && !allowDuringWorkspaceSwitch)
            || !_closedTabs.Any(tab => tab.Id == saved.Id)
            || !_workspace.ClosedTabOperations.Add(saved.Id))
        {
            return;
        }

        _closedTabRestoreOperations++;
        try
        {
            await RestoreClosedCoreAsync(saved);
        }
        finally
        {
            _closedTabRestoreOperations--;
            _workspace.ClosedTabOperations.Remove(saved.Id);
        }
    }

    private async Task RestoreClosedCoreAsync(SessionTab saved)
    {
        if (saved is { Path: { } path, DraftRef: null })
        {
            var isLog = saved.Log is not null || saved.LogOffset is not null;
            var opensAsLog = isLog && saved.Log?.EditPages != true;
            var existing = _tabs.Values.FirstOrDefault(tab =>
                tab.OpensAsLog == opensAsLog && tab.Recovery is null && SamePath(tab.Path, path));
            if (existing is not null)
            {
                if (existing.Loading)
                {
                    Select(existing);
                    return;
                }

                if (!existing.Dirty && existing.Log?.HasPendingEdit != true && existing.Deferred?.DraftRef is null)
                {
                    existing.CustomTitle = saved.CustomTitle ?? existing.CustomTitle;
                    if (existing.Deferred is not null)
                    {
                        SetDeferred(existing, saved with { Id = existing.Id });
                        await ActivateDeferredAsync(existing);
                    }
                    else if (isLog)
                    {
                        RestoreSavedLog(existing, path, saved);
                    }
                    else if (existing.Editor is not null)
                    {
                        await RestoreClosedEditorAsync(existing, path, saved);
                    }
                }

                if (_tabs.ContainsKey(existing.Id))
                {
                    Select(existing);
                    RemoveClosedEntry(saved);
                }
                return;
            }
        }

        if (_tabs.TryGetValue(saved.Id, out var sameTab))
        {
            Select(sameTab);
            return;
        }

        var tab = AddTab(saved.Id);
        SetDeferred(tab, saved);
        try
        {
            await ActivateDeferredAsync(tab);
            RemoveClosedEntry(saved);
        }
        catch
        {
            RemoveTab(tab);
            throw;
        }
    }

    private async Task RestoreClosedEditorAsync(WorkTab tab, string path, SessionTab saved)
    {
        if (tab.Encoding != saved.Encoding)
        {
            var revision = tab.Revision;
            var loaded = await DocumentFiles.ReadAsync(path, saved.Encoding, _lifetime.Token);
            if (tab.Dirty || tab.Revision != revision || !_tabs.ContainsKey(tab.Id))
            {
                return;
            }

            AttachLoadedEditor(tab, loaded);
        }

        tab.JsonDisabled = saved.JsonValidationDisabled;
        RestoreEditorView(tab, saved);
        ScheduleJsonValidation(tab);
    }

    private void RemoveClosedEntry(SessionTab saved)
    {
        _closedTabs.RemoveAll(item => item.Id == saved.Id);
        RefreshHistoryMenus();
    }

    private async Task RecordRecentAsync(WorkTab tab)
    {
        if (tab.Path is not { } path || tab.Recovery is not null)
        {
            return;
        }

        var editor = tab.Editor;
        var log = tab.Log?.CaptureState();
        var selection = editor is null ? default : MultiCaretEditing.Primary(editor.TextArea);
        var entry = new RecentFileEntry(path, tab.Encoding, tab.IsLog, selection.Start,
            selection.Length, log?.Offset, editor?.Document.LineCount,
            Reversed: editor is not null && editor.CaretOffset < selection.End,
            ScrollX: editor?.HorizontalOffset ?? 0,
            ScrollY: editor?.VerticalOffset ?? 0,
            JsonValidationDisabled: tab.JsonDisabled,
            Log: log);
        await _storage.AddRecentAsync(entry, _lifetime.Token);
        _recent = await _storage.GetRecentFilesAsync(_lifetime.Token);
        RefreshHistoryMenus();
    }

    private async Task OpenRecentAsync(RecentFileEntry entry)
    {
        var encoding = entry.Log is { } bookmark ? bookmark.Encoding : entry.Encoding;
        var tab = entry.Log?.EditPages == true
            ? await OpenSinglePathAsync(entry.Path, asLog: false, encoding, editPages: true)
            : await OpenPathAsync(entry.Path, entry.ViewAsLog, encoding);
        if (tab is not null && _tabs.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, tab))
        {
            if (tab.Log is { } paged)
            {
                if (paged.HasPendingEdit || tab.Recovery is not null || entry.Log is null && entry.LogOffset is null) return;
                var log = (entry.Log ?? new LogTabState(entry.Encoding, entry.LogOffset ?? 0)) with
                {
                    WordWrap = _wordWrap,
                    EditPages = paged.EditsPagesByDefault
                };
                AttachLog(tab, entry.Path, encoding, log);
                if (paged.EditsPagesByDefault) RestorePagedEditorCaret(tab, tab.Log!, null, focus: true);
            }
            else if (tab.Editor is not null)
            {
                tab.JsonDisabled = entry.JsonValidationDisabled;
                ScheduleJsonValidation(tab);
                RestoreEditorView(tab, new SessionTab(
                    tab.Id,
                    entry.Path,
                    entry.Encoding ?? tab.Encoding,
                    entry.SelectionStart,
                    entry.SelectionLength,
                    entry.Reversed,
                    entry.ScrollX,
                    entry.ScrollY)
                {
                    SelectionUsesUtf8 = entry.SelectionUsesUtf8
                });
            }
        }
    }

    private void RefreshHistoryMenus()
    {
        foreach (var window in _workspace.Windows.ToArray())
        {
            window._recent = _recent;
            window.RefreshHistoryMenusLocal();
            window._refreshHome?.Invoke();
        }
    }

    private void RefreshHistoryMenusLocal()
    {
        if (_recentMenu is { } recent)
        {
            recent.Items.Clear();
            foreach (var item in _recent.Take(16))
            {
                recent.Items.Add(Command(Path.GetFileName(item.Path) + DescribeWork(item.LineCount, item.LastWorkedMs),
                    () => Run(() => OpenRecentAsync(item))));
            }

            recent.Items.Add(Command("Вся история", () => Run(ShowHomeAsync)));
        }

        if (_closedFilesMenu is { } closedFiles)
        {
            closedFiles.Items.Clear();
            foreach (var item in _closedTabs.Where(item => item.Path is not null))
            {
                var name = ClosedTabDisplayName(item);
                closedFiles.Items.Add(Command(name + DescribeWork(item.LineCount, item.LastWorkedMs),
                    () => Run(() => RestoreClosedAsync(item))));
            }
        }

        if (_closedDraftsMenu is { } closedDrafts)
        {
            closedDrafts.Items.Clear();
            foreach (var item in _closedTabs.Where(item => item.Path is null))
            {
                var name = ClosedTabDisplayName(item);
                closedDrafts.Items.Add(Command(name + DescribeWork(Settings.ShowDraftLineCount ? item.LineCount : null, item.LastWorkedMs),
                    () => Run(() => RestoreClosedAsync(item))));
            }
        }
    }

    private string ClosedTabDisplayName(SessionTab tab)
    {
        if (tab.CustomTitle is { Length: > 0 } title) return title;
        if (tab.Path is { } path)
        {
            return Path.GetFileName(path);
        }

        var line = tab.UntitledName;
        if (string.IsNullOrWhiteSpace(line) && tab.DraftRef is not null && SessionStorage.GetDraftPath(tab) is { } draftPath)
        {
            try
            {
                using var reader = new StreamReader(draftPath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
                var buffer = new char[1600];
                var count = reader.Read(buffer, 0, buffer.Length);
                line = new string(buffer, 0, count).Split(['\r', '\n'], 2)[0].Trim();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return "Без имени";
        }

        var builder = new StringBuilder();
        foreach (var rune in line.EnumerateRunes().Take(Settings.UntitledTabLimit))
        {
            builder.Append(rune);
        }

        return builder.Length == 0 ? "Без имени" : builder.ToString();
    }

    private static string DescribeWork(long? lines, long? stamp)
    {
        var summary = lines is { } count ? $" · {LineNumbers.Format(count)} строк" : "";
        if (stamp is { } milliseconds)
        {
            var worked = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            var ago = DateTimeOffset.UtcNow - worked;
            summary += ago switch
            {
                { TotalMinutes: < 1 } => " · только что",
                { TotalHours: < 1 } => $" · {(int)ago.TotalMinutes} мин назад",
                { TotalDays: < 1 } => $" · {(int)ago.TotalHours} ч назад",
                _ => $" · {worked.LocalDateTime:g}"
            };
        }

        return summary;
    }
}
