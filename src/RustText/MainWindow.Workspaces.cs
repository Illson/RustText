using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaloniaEdit;

namespace RustText.Demo;

public partial class MainWindow
{
    private Size? _lastNormalWindowSize;
    private bool _restoreNormalSizeOnNextRestore;

    private void InitializeWindowSizeTracking()
    {
        _lastNormalWindowSize = ValidWindowSize(new Size(Width, Height))
            ? new Size(Width, Height) : new Size(Math.Max(MinWidth, 1080), Math.Max(MinHeight, 700));
        Resized += TrackNormalWindowSize;
        PropertyChanged += RestoreTrackedWindowSize;
    }

    private void TrackNormalWindowSize(object? sender, WindowResizedEventArgs e)
    {
        if (WindowState != WindowState.Normal || _restoreNormalSizeOnNextRestore || !ValidWindowSize(e.ClientSize))
            return;

        var size = e.ClientSize;
        if (e.Reason is WindowResizeReason.Application or WindowResizeReason.Layout)
        {
            if (ClientSize == size) _lastNormalWindowSize = size;
            return;
        }

        // On Win32, WM_SIZE runs before WindowStateChanged. Defer user/native resizes
        // until the public state settles, then reject stale maximized callbacks.
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _closing || WindowState != WindowState.Normal || _restoreNormalSizeOnNextRestore
                || !ValidWindowSize(size) || ClientSize != size)
                return;
            _lastNormalWindowSize = size;
        });
    }

    private void RestoreTrackedWindowSize(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty || e.GetNewValue<WindowState>() != WindowState.Normal
            || e.GetOldValue<WindowState>() == WindowState.Normal || !_restoreNormalSizeOnNextRestore
            || _lastNormalWindowSize is not { } size)
            return;

        // Only a workspace restored as maximized may have stale HWND restore bounds from
        // another workspace. Ordinary user restore/snap behavior stays native.
        Width = size.Width;
        Height = size.Height;
        _restoreNormalSizeOnNextRestore = false;
    }

    private void RestoreWorkspaceWindowSize(double width, double height, bool maximized)
    {
        Width = Math.Clamp(width, MinWidth, 4000);
        Height = Math.Clamp(height, MinHeight, 2400);
        _lastNormalWindowSize = new Size(Width, Height);
        _restoreNormalSizeOnNextRestore = maximized;
        WindowState = maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private Size PersistedWindowSize()
    {
        if (WindowState == WindowState.Normal)
        {
            var current = new Size(Width, Height);
            if (ValidWindowSize(current))
            {
                _lastNormalWindowSize = current;
                _restoreNormalSizeOnNextRestore = false;
            }
            return current;
        }
        return _lastNormalWindowSize ?? new Size(Width, Height);
    }

    private WorkspaceViewState CaptureWorkspaceViewForPersistence()
    {
        var view = CaptureWorkspaceView();
        var size = PersistedWindowSize();
        return view with { Width = size.Width, Height = size.Height };
    }

    private static bool ValidWindowSize(Size size) =>
        double.IsFinite(size.Width) && size.Width > 0 && double.IsFinite(size.Height) && size.Height > 0;

    private async Task ShowWorkspacesAsync()
    {
        _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        var catalog = _workspace.Catalog;
        var rows = new StackPanel { Spacing = 8 };
        var dialog = Dialog("Рабочие пространства", DialogPanel(rows), 720);
        foreach (var workspace in catalog.Workspaces)
        {
            var selected = !_startEmpty && workspace.Id == _workspaceId;
            var name = new TextBlock
            {
                Text = workspace.Name + (selected ? " · открыто" : ""),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var buttons = new WrapPanel { ItemSpacing = 5, LineSpacing = 4 };
            buttons.Children.Add(FocusButton(Button("Переключить", () => Run(async () =>
            {
                await SwitchWorkspaceAsync(workspace.Id);
                dialog.Close();
            }))));
            buttons.Children.Add(FocusButton(Button("Новое окно", () => Run(async () =>
            {
                await OpenWorkspaceWindowAsync(workspace.Id);
                dialog.Close();
            }))));
            buttons.Children.Add(FocusButton(Button("Дублировать…", () => Run(async () =>
            {
                await DuplicateWorkspaceAsync(workspace, dialog);
            }))));
            if (workspace.Id != WorkspaceCatalogStore.DefaultWorkspaceId)
            {
                buttons.Children.Add(FocusButton(Button("Переименовать", () => Run(async () =>
                {
                    var name = await PromptAsync("Переименовать пространство", "Новое имя", workspace.Name);
                    if (name is null) return;
                    await _workspace.CatalogStore.RenameAsync(workspace.Id, name, _lifetime.Token);
                    PublishWorkspaceCatalog(await _workspace.CatalogStore.LoadAsync(_lifetime.Token));
                    dialog.Close();
                    await ShowWorkspacesAsync();
                }))));
                buttons.Children.Add(FocusButton(Button("Удалить", () => Run(async () =>
                {
                    await DeleteWorkspaceAsync(workspace, dialog);
                }))));
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            var identity = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            identity.Children.Add(CreateWorkspaceBadge(workspace.Symbol ?? DefaultWorkspaceSymbol, workspace.Color));
            Grid.SetColumn(name, 1);
            identity.Children.Add(name);
            row.Children.Add(identity);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            rows.Children.Add(new Border
            {
                Background = FocusTheme.Deep,
                BorderBrush = FocusTheme.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9),
                Child = row
            });
        }

        var create = FocusButton(Button("Сохранить текущее как…", () => Run(async () =>
        {
            var name = await PromptAsync("Новое рабочее пространство", "Имя пространства");
            if (name is null) return;
            await CreateWorkspaceFromCurrentAsync(name);
            dialog.Close();
        })), primary: true);
        rows.Children.Add(create);

        var deleted = catalog.DeletedWorkspaces ?? [];
        if (deleted.Count > 0)
        {
            rows.Children.Add(new TextBlock { Text = "Удалённые пространства", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            foreach (var item in deleted)
            {
                var restore = FocusButton(Button($"Восстановить «{item.Workspace.Name}»", () => Run(async () =>
                {
                    PublishWorkspaceCatalog(await _workspace.CatalogStore.RestoreDeletedAsync(item.Workspace.Id, _lifetime.Token));
                    dialog.Close();
                    await ShowWorkspacesAsync();
                })));
                rows.Children.Add(restore);
            }
        }

        rows.Children.Add(FocusButton(Button("Закрыть", () => dialog.Close())));
        await dialog.ShowDialog(this);
    }

    private async Task SwitchWorkspaceAsync(string workspaceId)
    {
        if (_workspaceSwitchPending || _closing || _closed) return;
        _workspaceSwitchPending = true;
        (TextEditor Editor, bool WasEnabled)[] editorStates = [];
        var gateHeld = false;
        try
        {
            await _workspace.WorkspaceSwitchGate.WaitAsync(_lifetime.Token);
            gateHeld = true;
            editorStates = LoadedWorkspaceTabs().Select(tab => tab.Editor)
                .OfType<TextEditor>()
                .Select(editor => (editor, editor.IsEnabled))
                .ToArray();
            foreach (var (editor, _) in editorStates) editor.IsEnabled = false;
            await SwitchWorkspaceCoreAsync(workspaceId);
        }
        finally
        {
            if (!_closing && !_closed)
                foreach (var (editor, wasEnabled) in editorStates) editor.IsEnabled = wasEnabled;
            _workspaceSwitchPending = false;
            if (gateHeld) _workspace.WorkspaceSwitchGate.Release();
        }
    }

    private async Task SwitchWorkspaceCoreAsync(string workspaceId)
    {
        var enteringEmpty = workspaceId == EmptyStartupWindowId;
        var sourceId = _startEmpty ? EmptyStartupWindowId : _workspaceId;
        if (workspaceId == sourceId || (enteringEmpty && _windowId != WorkspaceCatalogStore.PrimaryWindowId)) return;
        if (_closedTabRestoreOperations > 0
            || _tabs.Values.Any(tab => tab.Closing || tab.Loading || tab.CheckingSession
                || tab.Saving || tab.Log?.HasPendingEdit == true))
        {
            SetStatus("Дождитесь завершения закрытия или загрузки вкладки перед переключением пространства.");
            return;
        }

        var dirtyFiles = _tabs.Values.Where(tab => tab.Dirty && tab.Path is not null).ToArray();
        if (dirtyFiles.Length > 0)
        {
            var body = DialogPanel(new TextBlock
            {
                Text = $"Открытые файлы содержат несохранённые изменения ({dirtyFiles.Length}). Сохраните их перед переключением или оставьте черновиками в текущем пространстве.",
                TextWrapping = TextWrapping.Wrap
            });
            var dialog = Dialog("Переключить рабочее пространство?", body);
            var answer = (string?)null;
            var save = FocusButton(Button("Сохранить", () => { answer = "save"; dialog.Close(true); }), primary: true);
            var keep = FocusButton(Button("Оставить черновики", () => { answer = "drafts"; dialog.Close(true); }));
            var cancel = FocusButton(Button("Отмена", () => dialog.Close(false)));
            body.Children.Add(Row(save, keep, cancel));
            if (!await dialog.ShowDialog<bool>(this)) return;
            if (answer == "save")
            {
                foreach (var tab in dirtyFiles)
                {
                    Select(tab);
                    await SaveAsync();
                    if (tab.Dirty) return;
                }
            }
        }

        _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        if (!enteringEmpty && !_workspace.Catalog.Workspaces.Any(item => item.Id == workspaceId))
            throw new KeyNotFoundException("Рабочее пространство не найдено.");
        var cachedView = _workspaceViews.TryGetValue(workspaceId, out var existingView) ? existingView : null;
        var targetWindowId = _windowId;
        var targetStorage = enteringEmpty
            ? _workspace.GetStorage(WorkspaceCatalogStore.DefaultWorkspaceId, EmptyStartupWindowId)
            : _workspace.GetStorage(workspaceId, targetWindowId, _shareDefaultSession);
        var targetCacheKey = StorageCacheKey(targetStorage);
        var targetState = cachedView is null ? await targetStorage.LoadSessionAsync(_lifetime.Token) : null;
        if (cachedView is null && targetState is null && (targetStorage.HasSessionCheckpoint || targetStorage.CheckpointReadBlocked))
        {
            _workspace.UnavailableSessions.Add(targetCacheKey);
            throw new InvalidDataException("Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.");
        }
        if (cachedView is null && targetState is null && targetWindowId != WorkspaceCatalogStore.PrimaryWindowId)
        {
            var primaryState = await _workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId)
                .LoadSessionAsync(_lifetime.Token);
            if (primaryState is not null)
            {
                await CopyWorkspaceSessionAsync(_workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId),
                    _workspace.GetStorage(workspaceId, targetWindowId, _shareDefaultSession));
                targetState = await _workspace.GetStorage(workspaceId, targetWindowId, _shareDefaultSession).LoadSessionAsync(_lifetime.Token);
            }
            else if (_workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId).HasSessionCheckpoint)
            {
                _workspace.UnavailableSessions.Add(StorageCacheKey(_workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId)));
                throw new InvalidDataException("Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.");
            }
        }

        if (targetStorage.CheckpointReadBlocked) _workspace.UnavailableSessions.Add(targetCacheKey);
        else if (cachedView is null && targetStorage.LoadedLatestCheckpoint
            && _workspace.UnavailableSessions.Contains(targetCacheKey) && !SessionViews(targetStorage, null).Any())
            _workspace.UnavailableSessions.Remove(targetCacheKey);
        _workspaceLoadWarning = targetStorage.CheckpointReadBlocked ? FallbackSessionWarning
            : _workspace.UnavailableSessions.Contains(targetCacheKey)
                ? "Сеанс недоступен для записи. Перезапустите приложение после восстановления доступа." : null;
        await SaveWorkspaceSessionAsync(excludeWindow: null);
        if (!enteringEmpty)
        {
            await _workspace.CatalogStore.SetWindowWorkspaceAsync(targetWindowId, workspaceId, _lifetime.Token);
            _workspace.Catalog = await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        }
        if (!_startEmpty || _tabs.Count > 0) _workspaceViews[sourceId] = CaptureWorkspaceViewForPersistence();
        CloseSearchPanel();
        WorkspaceHost.Content = null;
        foreach (var grid in _splitGrids) grid.Children.Clear();
        _splitGrids.Clear();

        _workspaceId = enteringEmpty ? WorkspaceCatalogStore.DefaultWorkspaceId : workspaceId;
        _startEmpty = enteringEmpty;
        ApplyWorkspaceTheme();
        _closedTabs = _workspace.GetClosedTabs(SessionStorage);
        await LoadArchivedHistoryAsync();
        _closedTabs.RemoveAll(tab => IsKnownEmptyUntitledDraft(tab, SessionStorage));

        if (cachedView is { } view)
        {
            _tabs = view.Tabs;
            _root = view.Root;
            _active = view.Active;
            _workspaceViews.Remove(workspaceId);
            RestoreWorkspaceWindowSize(view.Width, view.Height, view.Maximized);
            if (view.Home) await ShowHomeAsync();
            else ShowGroups();
            ApplyTextAppearance();
            RefreshHistoryMenus();
            if (_workspaceLoadWarning is not null) SetStatus(_workspaceLoadWarning);
            return;
        }

        _tabs = [];
        _root = _active = MakeGroup();
        _closedTabs.Clear();
        if (enteringEmpty)
        {
            if (targetState is not null) AddEmptyStartupHistory(targetState);
            else _emptyHistoryBaseline = [];
            await ShowHomeAsync();
        }
        else if (targetState is not null)
        {
            RestoreDetachedDraftTracking(SessionStorage, _workspaceId, targetState);
            _closedTabs.AddRange((targetState.ClosedTabs ?? []).Where(tab => !IsKnownEmptyUntitledDraft(tab, SessionStorage)));
            StorageService.TrimClosedBookmarks(_closedTabs);
            MountSessionTabs(targetState, pending: false);
            ApplyTextAppearance();
            if (SelectedIn(_active) is { } selected)
                await GuardAsync(() => ActivateDeferredDuringWorkspaceSwitchAsync(selected));
        }
        else
        {
            await ShowHomeAsync();
        }
        RefreshHistoryMenus();
        if (_workspaceLoadWarning is not null) SetStatus(_workspaceLoadWarning);
    }

    private Task EnterEmptyStartupAsync() => SwitchWorkspaceAsync(EmptyStartupWindowId);

    private async Task OpenWorkspaceWindowAsync(string workspaceId)
    {
        await _workspace.WorkspaceSwitchGate.WaitAsync(_lifetime.Token);
        try
        {
            _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
            if (!_workspace.Catalog.Workspaces.Any(item => item.Id == workspaceId))
                throw new KeyNotFoundException("Рабочее пространство не найдено.");
            StorageService sourceStorage;
            if (!_startEmpty && _workspaceId == workspaceId)
            {
                await SaveWorkspaceSessionAsync(excludeWindow: null);
                sourceStorage = SessionStorage;
            }
            else
            {
                sourceStorage = _workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId);
            }
            var windowId = "window:" + Guid.NewGuid().ToString("N");
            var targetStorage = _workspace.GetStorage(workspaceId, windowId);
            if (sourceStorage != targetStorage)
                await CopyWorkspaceSessionAsync(sourceStorage, targetStorage);
            await _workspace.CatalogStore.SetWindowWorkspaceAsync(windowId, workspaceId, _lifetime.Token);
            _workspace.Catalog = await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
            var window = new MainWindow([], _workspace, workspaceId, windowId)
            {
                _recent = _recent
            };
            App.TrackWindow(window);
            window.Show();
            window.Activate();
        }
        finally { _workspace.WorkspaceSwitchGate.Release(); }
    }

    private async Task DuplicateWorkspaceAsync(WorkspaceEntry source, Window dialog)
    {
        var name = await PromptAsync("Дублировать рабочее пространство", "Имя копии", source.Name + " (копия)");
        if (name is null) return;
        if (source.Id == _workspaceId) await SaveWorkspaceSessionAsync(excludeWindow: null);
        var sourceStorage = _workspace.GetStorage(source.Id, _windowId, _shareDefaultSession);
        var primary = _workspace.GetStorage(source.Id, WorkspaceCatalogStore.PrimaryWindowId);
        var sessionSource = sourceStorage.HasSessionCheckpoint ? sourceStorage : primary;
        var (copy, catalog) = await _workspace.CatalogStore.CreateWithCatalogAsync(name, GetWorkspaceThemeKey(source.Id), _lifetime.Token,
            symbol: source.Symbol, color: source.Color);
        try
        {
            await CopyWorkspaceAsync(copy.Id, sessionSource, primary, sourceStorage);
        }
        catch
        {
            PublishWorkspaceCatalog(await _workspace.CatalogStore.DiscardIncompleteCopyAsync(copy.Id));
            throw;
        }
        PublishCreatedWorkspace(copy, catalog);
        dialog.Close();
        await SwitchWorkspaceAsync(copy.Id);
    }

    private async Task CreateWorkspaceFromCurrentAsync(string name)
    {
        await SaveWorkspaceSessionAsync(excludeWindow: null);
        var current = CurrentWorkspaceEntry();
        var (created, catalog) = await _workspace.CatalogStore.CreateWithCatalogAsync(name, WorkspaceThemeKey, _lifetime.Token,
            symbol: current?.Symbol, color: current?.Color);
        try
        {
            var primary = _startEmpty ? SessionStorage : _workspace.GetStorage(_workspaceId, WorkspaceCatalogStore.PrimaryWindowId);
            await CopyWorkspaceAsync(created.Id, SessionStorage, primary, SessionStorage);
        }
        catch
        {
            PublishWorkspaceCatalog(await _workspace.CatalogStore.DiscardIncompleteCopyAsync(created.Id));
            throw;
        }
        PublishCreatedWorkspace(created, catalog);
        await SwitchWorkspaceAsync(created.Id);
    }

    private void PublishCreatedWorkspace(WorkspaceEntry created, WorkspaceCatalog committedCatalog)
    {
        // Other windows can update the shared catalog while a workspace's files are copied.
        var current = _workspace.Catalog ?? committedCatalog;
        if (!current.Workspaces.Any(entry => entry.Id == created.Id))
            current = current with { Workspaces = [.. current.Workspaces, created] };
        PublishWorkspaceCatalog(current);
    }

    private void PublishWorkspaceCatalog(WorkspaceCatalog catalog)
    {
        _workspace.Catalog = catalog;
        foreach (var window in _workspace.Windows)
            window.RefreshWorkspaceButton();
    }

    private async Task CopyWorkspaceAsync(string workspaceId, StorageService sessionSource, params StorageService[] archiveSources)
    {
        foreach (var windowId in new[] { WorkspaceCatalogStore.PrimaryWindowId, _windowId }.Distinct())
        {
            var destination = _workspace.GetStorage(workspaceId, windowId);
            await CopyWorkspaceSessionAsync(sessionSource, destination);
            foreach (var source in archiveSources.Distinct())
                foreach (var entry in await source.DraftArchive.GetEntriesAsync(_lifetime.Token))
                    await source.DraftArchive.CopyEntryToAsync(entry, destination.DraftArchive, _lifetime.Token);
        }
    }

    private async Task CopyWorkspaceSessionAsync(StorageService source, StorageService destination)
    {
        if (ReferenceEquals(source, destination)) return;
        if (_workspace.UnavailableSessions.Contains(StorageCacheKey(source)) && SessionViews(source, null).Any())
            throw new InvalidDataException("Рабочее пространство недоступно для записи. Перезапустите приложение перед копированием.");
        var state = await source.LoadSessionAsync(_lifetime.Token);
        if (source.CheckpointReadBlocked || state is null && source.HasSessionCheckpoint)
            throw new InvalidDataException("Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.");
        if (state is null) return;

        var readers = new Dictionary<long, Func<TextReader>>();
        foreach (var tab in state.Tabs.Concat(state.ClosedTabs ?? []).Where(item => item.DraftRef is not null))
        {
            if (source.GetDraftPath(tab) is { } path)
                readers[tab.Id] = () => new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
                    new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        }
        await destination.SaveSessionWithDraftReadersAsync(state with { DetachedDrafts = null }, readers, _lifetime.Token);
    }

    private async Task DeleteWorkspaceAsync(WorkspaceEntry workspace, Window dialog)
    {
        EnsureInactive();
        var body = DialogPanel(new TextBlock
        {
            Text = $"Удалить «{workspace.Name}»? Сохранённые вкладки и черновики можно будет восстановить из истории удалённых пространств до истечения срока хранения.",
            TextWrapping = TextWrapping.Wrap
        });
        var confirm = Dialog("Удалить рабочее пространство?", body);
        body.Children.Add(Row(
            FocusButton(Button("Отмена", () => confirm.Close(false))),
            FocusButton(Button("Удалить", () => confirm.Close(true)))));
        if (!await confirm.ShowDialog<bool>(this)) return;
        await _workspace.WorkspaceSwitchGate.WaitAsync(_lifetime.Token);
        try
        {
            EnsureInactive();
            foreach (var window in _workspace.Windows.Where(window => !window._closed && window._workspaceViews.ContainsKey(workspace.Id)).ToArray())
                await window.SaveSessionAsync();
            await _workspace.SaveGate.WaitAsync(_lifetime.Token);
            try
            {
                var deletedCatalog = await _workspace.CatalogStore.DeleteAsync(workspace.Id,
                    Settings.WorkspaceHistoryLimit, _lifetime.Token);
                ForgetCachedWorkspaceViews(workspace.Id);
                PublishWorkspaceCatalog(deletedCatalog);
            }
            finally { _workspace.SaveGate.Release(); }
        }
        finally { _workspace.WorkspaceSwitchGate.Release(); }
        dialog.Close();
        await ShowWorkspacesAsync();

        void EnsureInactive()
        {
            if (_workspace.Windows.Any(window => !window._closed && window._workspaceId == workspace.Id))
                throw new InvalidOperationException("Закройте окна с этим пространством перед удалением.");
        }
    }

    private void ForgetCachedWorkspaceViews(string workspaceId)
    {
        foreach (var window in _workspace.Windows)
        {
            if (!window._workspaceViews.Remove(workspaceId, out var view)) continue;
            foreach (var tab in view.Tabs.Values) window.ReleaseTabContents(tab);
        }

        foreach (var key in _workspace.SessionStores.Keys.Where(key => key.WorkspaceId == workspaceId).ToArray())
        {
            var storage = _workspace.SessionStores[key];
            _workspace.DetachedDrafts.Remove(storage);
            var cacheKey = StorageCacheKey(storage);
            _workspace.LiveDrafts.Remove(cacheKey);
            _workspace.LastCheckpoints.Remove(cacheKey);
            _workspace.UnavailableSessions.Remove(cacheKey);
            _workspace.ClosedTabs.Remove(cacheKey);
            _workspace.SessionStores.Remove(key);
            _workspace.SharedSessionAliases.Remove(key);
        }
    }
}
