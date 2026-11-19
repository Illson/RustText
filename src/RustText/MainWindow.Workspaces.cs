using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private async Task ShowWorkspacesAsync()
    {
        _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        var catalog = _workspace.Catalog;
        var rows = new StackPanel { Spacing = 8 };
        var dialog = Dialog("Рабочие пространства", DialogPanel(rows), 720);
        foreach (var workspace in catalog.Workspaces)
        {
            var selected = workspace.Id == _workspaceId;
            var name = new TextBlock
            {
                Text = workspace.Name + (selected ? " · открыто" : ""),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal
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
                    _workspace.Catalog = await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
                    dialog.Close();
                    await ShowWorkspacesAsync();
                }))));
                buttons.Children.Add(FocusButton(Button("Удалить", () => Run(async () =>
                {
                    await DeleteWorkspaceAsync(workspace, dialog);
                }))));
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(name);
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
                    _workspace.Catalog = await _workspace.CatalogStore.RestoreDeletedAsync(item.Workspace.Id, _lifetime.Token);
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
        await _workspace.WorkspaceSwitchGate.WaitAsync(_lifetime.Token);
        try
        {
            await SwitchWorkspaceCoreAsync(workspaceId);
        }
        finally
        {
            _workspace.WorkspaceSwitchGate.Release();
        }
    }

    private async Task SwitchWorkspaceCoreAsync(string workspaceId)
    {
        if (workspaceId == _workspaceId) return;
        if (_tabs.Values.Any(tab => tab.Saving || tab.Log?.HasPendingEdit == true))
            throw new InvalidOperationException("Сначала завершите сохранение вкладки или отмените правки страницы.");

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
        if (!_workspace.Catalog.Workspaces.Any(item => item.Id == workspaceId))
            throw new KeyNotFoundException("Рабочее пространство не найдено.");
        var cachedView = _workspaceViews.TryGetValue(workspaceId, out var existingView) ? existingView : null;
        var targetStorage = _workspace.GetStorage(workspaceId, _windowId, _shareDefaultSession);
        var targetState = cachedView is null ? await targetStorage.LoadSessionAsync(_lifetime.Token) : null;
        if (cachedView is null && targetState is null && targetStorage.HasSessionCheckpoint)
        {
            _workspace.UnavailableSessions.Add(StorageCacheKey(targetStorage));
            throw new InvalidDataException("Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.");
        }
        if (cachedView is null && targetState is null && _windowId != WorkspaceCatalogStore.PrimaryWindowId)
        {
            var primaryState = await _workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId)
                .LoadSessionAsync(_lifetime.Token);
            if (primaryState is not null)
            {
                await CopyWorkspaceSessionAsync(_workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId),
                    _workspace.GetStorage(workspaceId, _windowId, _shareDefaultSession));
                targetState = await _workspace.GetStorage(workspaceId, _windowId, _shareDefaultSession).LoadSessionAsync(_lifetime.Token);
            }
            else if (_workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId).HasSessionCheckpoint)
            {
                _workspace.UnavailableSessions.Add(StorageCacheKey(_workspace.GetStorage(workspaceId, WorkspaceCatalogStore.PrimaryWindowId)));
                throw new InvalidDataException("Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.");
            }
        }

        await SaveWorkspaceSessionAsync(excludeWindow: null);
        await _workspace.CatalogStore.SetWindowWorkspaceAsync(_windowId, workspaceId, _lifetime.Token);
        _workspace.Catalog = await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        var currentContent = WorkspaceHost.Content;
        _workspaceViews[_workspaceId] = new WorkspaceViewState(_tabs, _root, _active, _home,
            _home ? currentContent as Control : null, Width, Height, WindowState == WindowState.Maximized,
            _wordWrap, _fontSize);
        CloseSearchPanel();
        WorkspaceHost.Content = null;
        foreach (var grid in _splitGrids) grid.Children.Clear();
        _splitGrids.Clear();

        _workspaceId = workspaceId;
        _closedTabs = _workspace.GetClosedTabs(SessionStorage);

        if (cachedView is { } view)
        {
            _tabs = view.Tabs;
            _root = view.Root;
            _active = view.Active;
            Width = Math.Clamp(view.Width, MinWidth, 4000);
            Height = Math.Clamp(view.Height, MinHeight, 2400);
            WindowState = view.Maximized ? WindowState.Maximized : WindowState.Normal;
            if (view.Home) await ShowHomeAsync();
            else ShowGroups();
            ApplyTextAppearance();
            RefreshHistoryMenus();
            return;
        }

        _tabs = [];
        _root = _active = MakeGroup();
        _closedTabs.Clear();
        if (targetState is not null)
        {
            _closedTabs.AddRange((targetState.ClosedTabs ?? []).Take(100));
            MountSessionTabs(targetState, pending: false);
            ApplyTextAppearance();
            if (SelectedIn(_active) is { } selected) await ActivateDeferredAsync(selected);
        }
        else
        {
            await ShowHomeAsync();
        }
        RefreshHistoryMenus();
    }

    private async Task OpenWorkspaceWindowAsync(string workspaceId)
    {
        _workspace.Catalog ??= await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
        if (!_workspace.Catalog.Workspaces.Any(item => item.Id == workspaceId))
            throw new KeyNotFoundException("Рабочее пространство не найдено.");
        StorageService sourceStorage;
        if (_workspaceId == workspaceId)
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
            _settings = _settings,
            _titleLimit = _titleLimit,
            _recent = _recent
        };
        App.TrackWindow(window);
        window.Show();
        window.Activate();
    }

    private async Task DuplicateWorkspaceAsync(WorkspaceEntry source, Window dialog)
    {
        var name = await PromptAsync("Дублировать рабочее пространство", "Имя копии", source.Name + " (копия)");
        if (name is null) return;
        if (source.Id == _workspaceId) await SaveWorkspaceSessionAsync(excludeWindow: null);
        var sourceStorage = _workspace.GetStorage(source.Id, _windowId, _shareDefaultSession);
        var state = await sourceStorage.LoadSessionAsync(_lifetime.Token);
        if (state is null && _windowId != WorkspaceCatalogStore.PrimaryWindowId)
        {
            sourceStorage = _workspace.GetStorage(source.Id, WorkspaceCatalogStore.PrimaryWindowId);
            state = await sourceStorage.LoadSessionAsync(_lifetime.Token);
        }
        if (state is null && sourceStorage.HasSessionCheckpoint)
            throw new InvalidDataException("Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.");
        var copy = await _workspace.CatalogStore.CreateAsync(name, _lifetime.Token);
        try
        {
            if (state is not null)
            {
                await CopyWorkspaceSessionAsync(sourceStorage, _workspace.GetStorage(copy.Id, _windowId));
                if (_windowId != WorkspaceCatalogStore.PrimaryWindowId)
                    await CopyWorkspaceSessionAsync(sourceStorage, _workspace.GetStorage(copy.Id, WorkspaceCatalogStore.PrimaryWindowId));
            }
            _workspace.Catalog = await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
            dialog.Close();
            await SwitchWorkspaceAsync(copy.Id);
        }
        catch
        {
            await _workspace.CatalogStore.DeleteAsync(copy.Id, 0, CancellationToken.None);
            _workspace.Catalog = await _workspace.CatalogStore.LoadAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task CreateWorkspaceFromCurrentAsync(string name)
    {
        await SaveWorkspaceSessionAsync(excludeWindow: null);
        var created = await _workspace.CatalogStore.CreateAsync(name, _lifetime.Token);
        try
        {
            await CopyWorkspaceSessionAsync(SessionStorage, _workspace.GetStorage(created.Id, _windowId));
            if (_windowId != WorkspaceCatalogStore.PrimaryWindowId)
                await CopyWorkspaceSessionAsync(SessionStorage, _workspace.GetStorage(created.Id, WorkspaceCatalogStore.PrimaryWindowId));
            _workspace.Catalog = await _workspace.CatalogStore.LoadAsync(_lifetime.Token);
            await SwitchWorkspaceAsync(created.Id);
        }
        catch
        {
            await _workspace.CatalogStore.DeleteAsync(created.Id, 0, CancellationToken.None);
            _workspace.Catalog = await _workspace.CatalogStore.LoadAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task CopyWorkspaceSessionAsync(StorageService source, StorageService destination)
    {
        if (ReferenceEquals(source, destination)) return;
        var state = await source.LoadSessionAsync(_lifetime.Token);
        if (state is null)
        {
            if (source.HasSessionCheckpoint)
                throw new InvalidDataException("Сохранённое рабочее пространство не удалось прочитать. Его файлы оставлены без изменений.");
            return;
        }
        var readers = new Dictionary<long, Func<TextReader>>();
        foreach (var tab in state.Tabs.Concat(state.ClosedTabs ?? []).Where(item => item.DraftRef is not null))
        {
            if (source.GetDraftPath(tab) is { } path && File.Exists(path))
                readers[tab.Id] = () => new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
        }
        await destination.SaveSessionWithDraftReadersAsync(state, readers, _lifetime.Token);
    }

    private async Task DeleteWorkspaceAsync(WorkspaceEntry workspace, Window dialog)
    {
        if (_workspace.Windows.Any(window => !window._closed && window._workspaceId == workspace.Id))
            throw new InvalidOperationException("Закройте окна с этим пространством перед удалением.");
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
        var deletedCatalog = await _workspace.CatalogStore.DeleteAsync(workspace.Id,
            _settings.WorkspaceHistoryLimit, _lifetime.Token);
        ForgetCachedWorkspaceViews(workspace.Id);
        _workspace.Catalog = deletedCatalog;
        dialog.Close();
        await ShowWorkspacesAsync();
    }

    private void ForgetCachedWorkspaceViews(string workspaceId)
    {
        foreach (var window in _workspace.Windows)
        {
            if (!window._workspaceViews.Remove(workspaceId, out var view)) continue;
            foreach (var tab in view.Tabs.Values)
            {
                window.DetachEditor(tab);
                tab.Log?.Close();
                tab.Log = null;
            }
        }

        foreach (var key in _workspace.SessionStores.Keys.Where(key => key.WorkspaceId == workspaceId).ToArray())
        {
            var storage = _workspace.SessionStores[key];
            var cacheKey = StorageCacheKey(storage);
            _workspace.LiveDrafts.Remove(cacheKey);
            _workspace.LastCheckpoints.Remove(cacheKey);
            _workspace.UnavailableSessions.Remove(cacheKey);
            _workspace.ClosedTabs.Remove(cacheKey);
            _workspace.SessionStores.Remove(key);
        }
    }
}
