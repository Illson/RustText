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
    private const int ClosedDraftsMenuLimit = 16;
    private MenuItem? _showArchivedDrafts;
    private Action? _refreshHome;
    private Action? _refreshHomeTimes;
    private DateTimeOffset _lastHomeRefresh;
    private SessionState? _pendingSession;
    private bool _validatingStartupSession;
    private bool _mountingSessionTabs;
    private bool _emptyStartupPreserveAllHistory;
    private SessionTab[] _emptyHistoryBaseline = [];
    private const string FallbackSessionWarning = "Последний снимок не удалось прочитать полностью. Открыт предыдущий, файлы оставлены без изменений. Сохраните нужные черновики в отдельные файлы.";

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
                }
                if (OperatingSystem.IsWindows())
                {
                    // Older profiles could save the disabled default without recording a choice.
                    await SaveWorkspaceSettingsAsync(static settings =>
                        settings is { AutoCheckUpdates: false, AutoCheckUpdatesChoiceMade: false }
                            ? settings with { AutoCheckUpdates = true, AutoCheckUpdatesChoiceMade = true }
                            : settings);
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
        ApplyWorkspaceTheme();
        _closedTabs = _workspace.GetClosedTabs(SessionStorage);
        await LoadArchivedHistoryAsync();

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

        if (state is null && (_pendingSession is not null || SessionStorage.HasSessionCheckpoint || SessionStorage.CheckpointReadBlocked))
        {
            _workspace.UnavailableSessions.Add(WorkspaceCacheKey);
            _workspaceLoadWarning ??= "Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.";
            SetStatus(_workspaceLoadWarning);
            if (_pendingSession is not null) MountSessionTabs(new SessionState([]), pending: false);
            _pendingSession = null;
            RefreshHistoryMenus();
            return;
        }

        if (SessionStorage.CheckpointReadBlocked)
        {
            _workspace.UnavailableSessions.Add(WorkspaceCacheKey);
            if (state is not null) _workspaceLoadWarning = FallbackSessionWarning;
        }
        _closedTabs.RemoveAll(tab => IsKnownEmptyUntitledDraft(tab, SessionStorage));
        if (state is not null && !_startEmpty) RestoreDetachedDraftTracking(SessionStorage, _workspaceId, state);
        if (Settings.WordWrap is null || Settings.FontSize is null)
        {
            await SaveWorkspaceSettingsAsync(settings => settings with
            {
                WordWrap = settings.WordWrap ?? state?.Wrap ?? true,
                FontSize = settings.FontSize ?? state?.FontSize ?? FocusTheme.EditorFontSize
            });
        }
        _wordWrap = Settings.WordWrap ?? true;
        _fontSize = Settings.FontSize ?? FocusTheme.EditorFontSize;
        ApplyTextAppearance();

        if (_startEmpty)
        {
            if (state is not null) AddEmptyStartupHistory(state);
            RefreshHistoryMenus();
            return;
        }

        if (state is null)
        {
            RefreshHistoryMenus();
            return;
        }

        _closedTabs.AddRange((state.ClosedTabs ?? []).Where(tab => !IsKnownEmptyUntitledDraft(tab, SessionStorage)));
        StorageService.TrimClosedBookmarks(_closedTabs);
        if (!ReferenceEquals(state, _pendingSession))
        {
            MountSessionTabs(state, pending: false);
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
            await GuardAsync(() => OpenDeferredTabAsync(selected));

        RefreshHistoryMenus();
    }

    private void AddEmptyStartupHistory(SessionState state)
    {
        _closedTabs.RemoveAll(tab => IsKnownEmptyUntitledDraft(tab, SessionStorage));
        var openHistory = state.Tabs.Where(tab => !IsKnownEmptyUntitledDraft(tab, SessionStorage)).ToArray();
        var closedHistory = (state.ClosedTabs ?? []).Where(tab => !IsKnownEmptyUntitledDraft(tab, SessionStorage)).ToArray();
        _emptyStartupPreserveAllHistory |= openHistory.Length > 0 || closedHistory.Length > 100;
        _workspace.NextId = Math.Max(_workspace.NextId,
            state.Tabs.Concat(state.ClosedTabs ?? []).Select(tab => tab.Id).DefaultIfEmpty().Max());
        var existingIds = _closedTabs.Select(tab => tab.Id).ToHashSet();
        var reopenedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var tab in openHistory)
        {
            if (existingIds.Add(tab.Id))
                _closedTabs.Insert(0, tab with { ClosedAtMs = tab.ClosedAtMs ?? reopenedAt });
        }
        foreach (var tab in closedHistory)
        {
            if (existingIds.Add(tab.Id)) _closedTabs.Add(tab);
        }
        if (!_emptyStartupPreserveAllHistory) StorageService.TrimClosedBookmarks(_closedTabs);
        _emptyHistoryBaseline = [.. _closedTabs];
    }

    private static bool IsKnownEmptyUntitledDraft(SessionTab tab, StorageService storage)
    {
        if (tab.Path is not null) return false;
        if (tab.DraftRef is null) return true;
        try { return storage.GetDraftLength(tab) == 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
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

            RestoreWorkspaceWindowSize(state.WindowWidth, state.WindowHeight, state.Maximized);

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
                .Select(item => tabs.Values.First(tab => ReferenceEquals(tab.Item, item)))
                .Where(tab => !tab.IsTransient).Select(tab => tab.Id).ToArray(),
                group.Tabs.SelectedItem is TabItem selected
                    ? tabs.Values.FirstOrDefault(tab => ReferenceEquals(tab.Item, selected) && !tab.IsTransient)?.Id : null);
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
        var lineEndings = tab.Format.MixedEndings?.Select(value => (byte)value).ToArray();
        return new SessionTab(tab.Id, tab.Path, tab.Encoding, selectionStart, selectionLength,
            caret < selectionStart + selectionLength, editor?.HorizontalOffset ?? 0, editor?.VerticalOffset ?? 0,
            LogOffset: log?.Offset, LineCount: editor?.Document.LineCount,
            LastWorkedMs: tab.LastWorked.ToUnixTimeMilliseconds(), LineEnding: tab.Format.Ending,
            LineEndingMap: lineEndings, Stamp: tab.Stamp, Log: log)
        {
            DiskEncoding = tab.DiskEncoding != tab.Encoding ? tab.DiskEncoding : null,
            AutoDetectEncoding = tab.AutoDetectEncoding,
            JsonValidationDisabled = tab.JsonDisabled,
            AutoRefresh = tab.AutoRefresh,
            DraftLogMode = editor is null ? null : false,
            CustomTitle = tab.CustomTitle,
            UntitledName = tab.Path is null ? UntitledName(tab) : null
        };
    }

    private Task SaveSessionAsync() => SaveWorkspaceSessionAsync(excludeWindow: null);

    private async Task PreserveDetachedDraftsOnCloseAsync(HashSet<StorageService>? restartStores = null)
    {
        var views = _workspaceViews.Append(KeyValuePair.Create(_startEmpty ? EmptyStartupWindowId : _workspaceId, CaptureWorkspaceView()))
            .ToLookup(pair => pair.Value.Storage);
        var stores = views.ToDictionary(group => group.Key, group => group.First().Key);
        if (restartStores is null && _windowId != WorkspaceCatalogStore.PrimaryWindowId && HasRestorableWindowAssignment())
        {
            // A previous run can have saved other workspaces under the same window assignment.
            foreach (var workspace in _workspace.Catalog!.Workspaces)
                if (!_workspace.SharedSessionAliases.Contains((workspace.Id, _windowId))
                    && Directory.Exists(StorageService.WorkspaceRootDirectory(_storage.ProfileBaseDirectory, workspace.Id, _windowId)))
                    stores.TryAdd(_workspace.GetStorage(workspace.Id, _windowId), workspace.Id);
        }

        var token = _lifetime.Token;
        var handled = new List<StorageService>();
        await _workspace.SaveGate.WaitAsync(token);
        try
        {
            foreach (var (source, workspaceId) in stores)
            {
                if (restartStores?.Contains(source) == true) continue;
                var otherViews = restartStores is null && SessionViews(source, this).Any();
                var complete = !otherViews && _windowId != WorkspaceCatalogStore.PrimaryWindowId && workspaceId != EmptyStartupWindowId
                    ? PrepareDetachedDraftRecovery(source, workspaceId) : null;
                if (!_workspace.DetachedDrafts.TryGetValue(source, out var recovery))
                {
                    if (complete is not { } target) continue;
                    recovery = new DetachedDraftRecovery(target.Primary);
                    _workspace.DetachedDrafts.Add(source, recovery);
                }
                restartStores?.Add(source);
                var closingTabs = views[source].SelectMany(view => view.Value.Tabs.Values);
                var closingIds = closingTabs.Select(tab => tab.Id).ToHashSet();
                var pending = recovery.DestinationIds.Keys.Where(id => !otherViews || closingIds.Contains(id)).ToHashSet();
                if (pending.Count == 0 && complete is null) continue;

                var saved = await source.LoadSessionAsync(token);
                if (source.CheckpointReadBlocked || saved is null && source.HasSessionCheckpoint)
                    throw new InvalidDataException("Не удалось прочитать черновики закрываемого окна. Сеанс оставлен без изменений.");
                var drafts = (saved?.Tabs ?? []).Concat(saved?.ClosedTabs ?? [])
                    .Where(tab => tab.DraftRef is not null && (complete is not null || pending.Contains(tab.Id)))
                    .DistinctBy(tab => tab.Id).ToArray();
                foreach (var draft in drafts)
                {
                    recovery.DestinationIds.TryAdd(draft.Id, null);
                    pending.Add(draft.Id);
                }
                var savedIds = drafts.Select(tab => tab.Id).ToHashSet();
                var recoverableDrafts = drafts.Where(tab => !IsKnownEmptyUntitledDraft(tab, source)).ToArray();
                if (closingTabs.Any(tab => (complete is not null || pending.Contains(tab.Id))
                    && !(tab.Path is null && tab.Editor?.Document.TextLength == 0
                        && tab.Recovery is null && tab.Deferred?.DraftRef is null)
                    && (tab.Dirty || tab.Recovery is not null || tab.Deferred?.DraftRef is not null || tab.Path is null && tab.Editor is not null)
                    && !savedIds.Contains(tab.Id)))
                    throw new IOException("Черновик вынесенной вкладки ещё не сохранён. Повторите закрытие окна.");
                if (recoverableDrafts.Length > 0) await MergeDetachedDraftsAsync(source, recovery, recoverableDrafts, token);

                if (complete is not null || pending.Count != savedIds.Count)
                {
                    foreach (var entry in await source.DraftArchive.GetEntriesAsync(token))
                        if (complete is not null || pending.Contains(entry.Tab.Id) && !savedIds.Contains(entry.Tab.Id))
                            await source.DraftArchive.CopyEntryToAsync(entry, recovery.PrimaryStorage.DraftArchive, token);
                }

                // A remaining view or failed restart may hand off the same draft again.
                if (restartStores is null && !otherViews) handled.Add(source);
            }
            foreach (var source in handled) _workspace.DetachedDrafts.Remove(source);
        }
        finally { _workspace.SaveGate.Release(); }
    }

    private long GetDetachedDraftDestinationId(DetachedDraftRecovery recovery, long sourceId, HashSet<long> activeIds)
    {
        var destinationId = recovery.DestinationIds[sourceId];
        if (destinationId is { } existing && !activeIds.Contains(existing)) return existing;
        var freshId = NextId();
        recovery.DestinationIds[sourceId] = freshId;
        return freshId;
    }

    private async Task MergeDetachedDraftsAsync(StorageService source, DetachedDraftRecovery recovery,
        SessionTab[] drafts, CancellationToken token)
    {
        var primary = recovery.PrimaryStorage;
        var cacheKey = StorageCacheKey(primary);
        if (_workspace.UnavailableSessions.Contains(cacheKey) && SessionViews(primary, null).Any())
            throw new InvalidDataException("Основное окно не восстановило сеанс. Закройте его перед повторным закрытием этого окна.");
        var checkpoint = await primary.LoadSessionAsync(token);
        if (primary.CheckpointReadBlocked || checkpoint is null && primary.HasSessionCheckpoint)
            throw new InvalidDataException("Не удалось прочитать основной сеанс рабочего пространства для восстановления черновика.");
        checkpoint ??= new SessionState([]);

        var liveViews = SessionViews(primary, null).ToList();
        if (liveViews.Count > 0 && (_workspace.UnavailableSessions.Contains(cacheKey)
            || liveViews.Any(view => view.Owner._validatingStartupSession)))
            throw new InvalidDataException("Основное окно ещё не восстановило сеанс.");
        var liveHistory = liveViews.Count > 0 ? _workspace.GetClosedTabs(primary) : null;
        var existing = liveHistory ?? checkpoint.ClosedTabs ?? [];
        _workspace.NextId = Math.Max(_workspace.NextId, checkpoint.Tabs.Concat(checkpoint.ClosedTabs ?? []).Concat(existing)
            .Select(tab => tab.Id).DefaultIfEmpty().Max());
        var activeIds = checkpoint.Tabs.Select(tab => tab.Id).Concat(liveViews.SelectMany(view => view.View.Tabs.Keys)).ToHashSet();
        var committedDrafts = checkpoint.Tabs.Concat(existing).Select(tab => (tab.Id, tab.DraftSha256)).ToHashSet();

        var readers = new Dictionary<long, Func<TextReader>>();
        var recovered = new List<SessionTab>();
        var closedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var draft in drafts)
        {
            var previouslyRecovered = recovery.DestinationIds[draft.Id];
            if (draft.DraftSha256 is { } hash && (committedDrafts.Contains((draft.Id, hash))
                || previouslyRecovered is { } idInPrimary && committedDrafts.Contains((idInPrimary, hash))))
                continue;
            var id = GetDetachedDraftDestinationId(recovery, draft.Id, activeIds);
            var path = source.GetDraftPath(draft)!;
            readers.Add(id, () => new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false));
            recovered.Add(draft with { Id = id, DraftRef = null, DraftSha256 = null, ClosedAtMs = closedAt });
        }
        if (recovered.Count == 0) return;

        if (liveHistory is not null)
        {
            // Include live tabs and their drafts so checkpoint pruning keeps any newly opened recovery files.
            var committed = (await SaveSessionViewsAsync(primary, liveViews, forget: null, force: true, recovered, readers))!;
            liveHistory.RemoveAll(tab => readers.ContainsKey(tab.Id));
            liveHistory.InsertRange(0, committed.ClosedTabs!.Where(tab => readers.ContainsKey(tab.Id)));
            if (!liveViews[0].Owner.PreserveEmptyHistory(primary)) StorageService.TrimClosedBookmarks(liveHistory);
            RefreshHistoryMenus();
        }
        else
        {
            // Only the closed history changes when the primary session is on disk.
            var updated = checkpoint with { ClosedTabs = [.. recovered, .. existing.Where(tab => !readers.ContainsKey(tab.Id))] };
            await primary.SaveSessionWithDraftReadersAsync(updated, readers, token);
        }
        _workspace.SetLastCheckpoint(cacheKey, null);
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
                var isActive = ReferenceEquals(storage, activeStorage);
                if (_workspace.UnavailableSessions.Contains(StorageCacheKey(storage)))
                {
                    if (isActive && forgetClosedTab is not null)
                        throw new IOException("Сеанс недоступен. История оставлена без изменений.");
                    if (_closing && SessionViews(storage, null).Where(view => ReferenceEquals(view.Owner, this))
                        .SelectMany(view => view.View.Tabs.Values).Any(tab =>
                            (tab.Dirty || tab.Recovery is not null || tab.Deferred?.DraftRef is not null)
                            && (tab.Revision != 0 || !storage.HasFallbackDraft(tab.Id, (tab.Recovery ?? tab.Deferred)?.DraftRef))))
                        throw new IOException("Сеанс недоступен. Сохраните новые правки в отдельные файлы перед закрытием окна.");
                    continue;
                }
                var empty = isActive && _startEmpty && _tabs.Count == 0;
                if (empty && forgetClosedTab is null && _closedTabs.SequenceEqual(_emptyHistoryBaseline)) continue;
                var views = SessionViews(storage, isActive ? excludeWindow : null).ToList();
                if (views.Count == 0) continue;
                var saved = await SaveSessionViewsAsync(storage, views, isActive ? forgetClosedTab : null, isActive && _closing);
                if (empty && saved is { Tabs.Count: 0, ClosedTabs: { } history })
                    _emptyHistoryBaseline = [.. history];
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
                yield return (window, window.CaptureWorkspaceViewForPersistence());
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
                .Select(item => (pair.Owner, Tab: byItem[item])).Where(entry => !entry.Tab.IsTransient);
        }).ToArray();
        var tabs = entries.Select(entry => entry.Tab).ToArray();
        var recoveries = tabs.Select(tab => tab.Recovery).ToArray();
        var state = entries.Select(entry => entry.Owner.CaptureTab(entry.Tab)).ToList();
        var selected = home.Tabs.Values.FirstOrDefault(tab => ReferenceEquals(tab.Item, home.Active.Tabs.SelectedItem));
        var history = _workspace.GetClosedTabs(storage);
        var preserveHistory = owner.PreserveEmptyHistory(storage);
        var historyToSave = recovered is null ? history : recovered.Concat(history).DistinctBy(tab => tab.Id);
        var capturedHistory = historyToSave.Where(tab => tab.Id != forget?.Id).ToList();
        if (!preserveHistory) StorageService.TrimClosedBookmarks(capturedHistory, draftReaders);
        var detachedDrafts = _workspace.DetachedDrafts.TryGetValue(storage, out var tracked)
            ? tracked.DestinationIds.OrderBy(pair => pair.Key).ToDictionary() : null;
        var session = new SessionState(state, capturedHistory, Math.Max(0, Array.IndexOf(tabs, selected)),
            double.IsFinite(home.Width) ? home.Width : 1080, double.IsFinite(home.Height) ? home.Height : 700,
            home.Maximized, home.WordWrap, home.FontSize, owner.CaptureLayout(home.Root, home.Tabs), home.Active.Id, detachedDrafts);
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
        foreach (var mapping in state.DetachedDrafts ?? []) hash.Add(mapping);
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
        tab.Format = EditorFileFormat.From(saved.Encoding, saved.LineEnding,
            saved.LineEndingMap?.Select(value => (LineEndingKind)value));
        tab.DiskEncoding = saved.DiskEncoding ?? saved.Encoding;
        tab.AutoDetectEncoding = SavedAutoDetectEncoding(saved);
        tab.Stamp = saved.Stamp;
        tab.JsonDisabled = saved.JsonValidationDisabled;
        tab.AutoRefresh = saved.AutoRefresh ?? true;
        tab.LastWorked = saved.LastWorkedMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.Now;
        tab.Deferred = saved;
        tab.Item.Content = DeferredContent(tab);
        UpdateTab(tab);
    }

    private Control DeferredContent(WorkTab tab) => tab.CheckingSession
        ? new TextBlock { Text = "Проверка сохранённой вкладки…", Margin = new Thickness(18) }
        : FocusButton(Button("Загрузить вкладку", () => Run(() => OpenDeferredTabAsync(tab))), primary: true);

    private Task ActivateDeferredAsync(WorkTab tab) =>
        ActivateDeferredCoreAsync(tab, allowDuringWorkspaceSwitch: false, focusEditor: false);

    private Task OpenDeferredTabAsync(WorkTab tab) =>
        ActivateDeferredCoreAsync(tab, allowDuringWorkspaceSwitch: false, focusEditor: true);

    private Task ActivateDeferredDuringWorkspaceSwitchAsync(WorkTab tab) =>
        ActivateDeferredCoreAsync(tab, allowDuringWorkspaceSwitch: true, focusEditor: true);

    private async Task ActivateDeferredCoreAsync(WorkTab tab, bool allowDuringWorkspaceSwitch, bool focusEditor)
    {
        if ((_workspaceSwitchPending && !allowDuringWorkspaceSwitch)
            || tab.Closing || tab.Deferred is not { } saved || tab.Loading || tab.CheckingSession || _closed)
        {
            return;
        }
        if (saved is { Path: { } encryptedPath, DraftRef: null } && EncryptedTabFile.HasExtension(encryptedPath))
        {
            RemoveTab(tab);
            if (_restoreFinished && _tabs.Count == 0) await ShowHomeAsync();
            await DecryptTabPathAsync(encryptedPath);
            return;
        }

        var focus = focusEditor ? CaptureEditorFocusRequest() : null;
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
                if (draftPath is not null && (saved.DraftLogMode == true || saved.DraftOffset.HasValue
                    || saved.DraftLogMode is null && new FileInfo(draftPath).Length > LineGeneratorThreshold))
                {
                    AttachPagedDraftEditor(tab, saved, draftPath);
                }
                else
                {
                    var draft = await storage.LoadDraftAsync(saved, token, fullEditor: saved.DraftLogMode == false) ?? "";
                    if (!IsCurrentTab()) return;
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
            else if (saved.Path is { } path)
            {
                var encoding = SavedReadEncoding(saved);
                if (encoding is null || !TryShareFileEditor(tab, encoding.Value))
                {
                    if (new FileInfo(path).Length > DocumentFiles.MaximumDocumentBytes)
                        AttachLog(tab, path, encoding,
                            new LogTabState(encoding, WordWrap: _wordWrap, AutoRefresh: tab.AutoRefresh, EditPages: true));
                    else
                    {
                        var loaded = await DocumentFiles.ReadAsync(path, encoding, token);
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
            focus?.Invoke(tab);
        }
        catch (Exception error)
        {
            if (IsCurrentTab())
            {
                var retry = FocusButton(Button("Повторить загрузку", () => Run(() => OpenDeferredTabAsync(tab))), primary: true);
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
        tab.AutoDetectEncoding = SavedAutoDetectEncoding(saved);
        if (tab.AutoDetectEncoding) state = state with { Encoding = null };
        AttachLog(tab, path, state.Encoding, state);
    }

    private static bool SavedAutoDetectEncoding(SessionTab saved) =>
        saved.AutoDetectEncoding ?? (saved.Log?.Encoding is null && saved.Encoding != TextEncodingKind.Windows1251);

    private static TextEncodingKind? SavedReadEncoding(SessionTab saved) =>
        SavedAutoDetectEncoding(saved) ? null : saved.DiskEncoding ?? saved.Encoding;

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
        bool focus = false)
    {
        var name = saved.Path is { } path ? Path.GetFileName(path) : saved.UntitledName;
        var state = new LogTabState(TextEncodingKind.Utf8, saved.DraftOffset ?? 0, WordWrap: _wordWrap,
            EditPages: saved.DraftLogMode != true);
        var lineCount = saved.DraftOffset.HasValue ? saved.LineCount : null;
        AttachLog(tab, draftPath, TextEncodingKind.Utf8, state, name ?? "Черновик", lineCount);
        tab.Recovery = saved;
        UpdateTab(tab);
        var editor = tab.Log!;
        if (pageCaret is not null || focus) RestorePagedEditorCaret(tab, editor, pageCaret, focus);
        editor.CommitDraftPage = async (temporary, hash, offset, caret, focusEditor, token) =>
        {
            await _workspace.SaveGate.WaitAsync(token);
            try
            {
                if (_workspace.UnavailableSessions.Contains(WorkspaceCacheKey))
                    throw new IOException("Сеанс недоступен. Сохраните правки в отдельный файл.");
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
        if (tab.IsTransient) return;
        await _workspace.SaveGate.WaitAsync(_lifetime.Token);
        try
        {
            var saved = CaptureTab(tab) with { ClosedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var isEmptyUntitled = tab.Path is null && (tab.Editor is { } liveEditor
                ? liveEditor.Document.TextLength == 0
                : IsKnownEmptyUntitledDraft(saved, SessionStorage));
            if (_workspace.UnavailableSessions.Contains(WorkspaceCacheKey)
                && (saved.DraftRef is not null || !isEmptyUntitled && tab.Path is null || preserveUnsavedText && tab.Dirty))
                throw new IOException("Сеанс недоступен. Сохраните черновик в отдельный файл перед закрытием вкладки.");

            if (!isEmptyUntitled && tab.Editor is { } editor && (tab.Path is null || (preserveUnsavedText && tab.Dirty)))
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
            if (!isEmptyUntitled) _closedTabs.Insert(0, saved);
            if (!deferHistoryLimit && !PreserveEmptyHistory(SessionStorage)) StorageService.TrimClosedBookmarks(_closedTabs);
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
            if (saved is { Path: { } encryptedPath, DraftRef: null } && EncryptedTabFile.HasExtension(encryptedPath))
            {
                RemoveClosedEntry(saved);
                await DecryptTabPathAsync(encryptedPath);
            }
            else await RestoreClosedCoreAsync(saved);
        }
        finally
        {
            _closedTabRestoreOperations--;
            _workspace.ClosedTabOperations.Remove(saved.Id);
        }
    }

    private async Task RestoreClosedCoreAsync(SessionTab saved)
    {
        var focusEditor = CaptureEditorFocusRequest();
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
                    focusEditor(existing);
                    RemoveClosedEntry(saved);
                }
                return;
            }
        }

        if (_tabs.TryGetValue(saved.Id, out var sameTab))
        {
            Select(sameTab);
            focusEditor(sameTab);
            return;
        }

        var tab = AddTab(saved.Id);
        SetDeferred(tab, saved);
        try
        {
            await OpenDeferredTabAsync(tab);
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
        tab.AutoDetectEncoding = SavedAutoDetectEncoding(saved);
        tab.DiskEncoding = saved.DiskEncoding ?? saved.Encoding;
        if (tab.AutoDetectEncoding || tab.Encoding != saved.Encoding)
        {
            var revision = tab.Revision;
            var loaded = await DocumentFiles.ReadAsync(path, SavedReadEncoding(saved), _lifetime.Token);
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

    private async Task<string?> RecordRecentAsync(WorkTab tab)
    {
        if (tab.Path is not { } path || tab.Recovery is not null)
        {
            return null;
        }

        var editor = tab.Editor;
        var log = tab.Log?.CaptureState();
        var selection = editor is null ? default : MultiCaretEditing.Primary(editor.TextArea);
        var entry = new RecentFileEntry(path, tab.ReadEncoding, tab.IsLog, selection.Start,
            selection.Length, log?.Offset, editor?.Document.LineCount,
            Reversed: editor is not null && editor.CaretOffset < selection.End,
            ScrollX: editor?.HorizontalOffset ?? 0,
            ScrollY: editor?.VerticalOffset ?? 0,
            JsonValidationDisabled: tab.JsonDisabled,
            Log: log);
        try
        {
            await _storage.AddRecentAsync(entry, _lifetime.Token);
            _recent = await _storage.GetRecentFilesAsync(_lifetime.Token);
        }
        catch (Exception error) when (IsUserError(error))
        {
            return $"Не удалось обновить список недавних файлов. {error.Message}";
        }
        RefreshHistoryMenus();
        return null;
    }

    private async Task OpenRecentAsync(RecentFileEntry entry)
    {
        var encoding = entry.Log is { } bookmark ? bookmark.Encoding : entry.Encoding;
        var tab = entry.Log?.EditPages == true
            ? await OpenSinglePathAsync(entry.Path, asLog: false, encoding, editPages: true)
            : await OpenSinglePathCoreAsync(entry.Path, entry.ViewAsLog, encoding, editPages: false, RestoreRecentEditor);
        if (tab?.Log is { } paged && _tabs.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, tab))
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

        void RestoreRecentEditor(WorkTab opened)
        {
            if (opened.Editor is null || !_tabs.TryGetValue(opened.Id, out var current) || !ReferenceEquals(current, opened))
                return;
            opened.JsonDisabled = entry.JsonValidationDisabled;
            ScheduleJsonValidation(opened);
            RestoreEditorView(opened, new SessionTab(
                opened.Id,
                entry.Path,
                entry.Encoding ?? opened.Encoding,
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
        if (_showArchivedDrafts is { } toggle) toggle.IsChecked = Settings.ShowArchivedDrafts;
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

        if (_closedDraftsMenu is { IsSubMenuOpen: true } closedDrafts)
            PopulateClosedDraftsMenu(closedDrafts);
    }

    private void PopulateClosedDraftsMenu(MenuItem menu)
    {
        var storage = SessionStorage;
        ResetClosedDraftsMenu(menu);
        var index = 0;
        foreach (var item in DraftHistory(storage).Take(ClosedDraftsMenuLimit))
        {
            menu.Items.Insert(index++, Command(item.Label + DescribeWork(Settings.ShowDraftLineCount ? item.Tab.LineCount : null, item.Tab.LastWorkedMs),
                () => Run(() => item.Archive is { } entry ? RestoreArchivedDraftAsync(storage, entry) : RestoreClosedAsync(item.Tab))));
        }
    }

    private void ResetClosedDraftsMenu(MenuItem menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Command("Вся история", () => Run(ShowHomeAsync)));
    }

    private string ClosedTabDisplayName(SessionTab tab, StorageService? storage = null)
    {
        if (tab.CustomTitle is { Length: > 0 } title) return title;
        if (tab.Path is { } path)
        {
            return Path.GetFileName(path);
        }

        var line = tab.UntitledName;
        if (string.IsNullOrWhiteSpace(line) && tab.DraftRef is not null && (storage ?? SessionStorage).GetDraftPath(tab) is { } draftPath)
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

        return LimitUntitledHistoryName(line);
    }

    private string LimitUntitledHistoryName(string? line)
    {
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
