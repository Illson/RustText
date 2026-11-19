using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RustText.Demo.Logs;

namespace RustText.Demo;

public partial class MainWindow : Window
{
    private readonly string[] _startupPaths;
    private readonly List<Grid> _splitGrids = [];
    private Dictionary<long, WorkTab> _tabs = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _checkpoint;
    private Group _root;
    private Group _active;
    private bool _home;
    private bool _closing;
    private bool _closed;
    private bool _restoreFinished;
    private bool _startEmpty;
    private bool _closeAfterStartupValidation;
    private bool _wordWrap = true;
    private double _fontSize = FocusTheme.EditorFontSize;
    private int _titleLimit = 10;
    private string _workspaceId = WorkspaceCatalogStore.DefaultWorkspaceId;
    private string _windowId = WorkspaceCatalogStore.PrimaryWindowId;
    private bool _shareDefaultSession;
    private string? _workspaceLoadWarning;
    private readonly Dictionary<string, WorkspaceViewState> _workspaceViews = new(StringComparer.Ordinal);



    public MainWindow() : this([])
    {
    }

    internal MainWindow(string[] startupPaths) : this(startupPaths, null)
    {
    }

    private MainWindow(string[] startupPaths, WorkspaceSession? workspace, string? workspaceId = null, string? windowId = null,
        bool shareDefaultSession = false)
    {
        _workspace = workspace ?? new WorkspaceSession();
        _storage = _workspace.Storage;
        _workspaceId = workspaceId ?? WorkspaceCatalogStore.DefaultWorkspaceId;
        _windowId = windowId ?? WorkspaceCatalogStore.PrimaryWindowId;
        _shareDefaultSession = shareDefaultSession;
        _closedTabs = _workspace.GetClosedTabs(_workspaceId, _windowId);
        _restoreWorkspace = workspace is null;
        _workspace.Windows.Add(this);
        Closed += async (_, _) =>
        {
            _workspace.Windows.Remove(this);
            if (_windowId == WorkspaceCatalogStore.PrimaryWindowId) return;
            try
            {
                _workspace.Catalog = await _workspace.CatalogStore.RemoveWindowAsync(_windowId);
            }
            catch (Exception error) when (IsUserError(error))
            {
                SetStatus($"Не удалось обновить список окон. {error.Message}");
            }
        };
        _startupPaths = startupPaths;
        InitializeComponent();
        PropertyChanged += CaptionStateChanged;
        ToolTip.SetTip(CaptionMinimize, "Свернуть");
        ToolTip.SetTip(CaptionMaximize, "Развернуть");
        ToolTip.SetTip(CaptionClose, "Закрыть окно");
        _root = _active = MakeGroup();
        BuildMenus();
        BuildSearchPanel();
        _checkpoint = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _checkpoint.Tick += (_, _) =>
        {
            if (_home && DateTimeOffset.Now - _lastHomeRefresh > TimeSpan.FromSeconds(10))
            {
                _lastHomeRefresh = DateTimeOffset.Now;
                _refreshHome?.Invoke();
            }

            if (_workspace.SaveGate.CurrentCount > 0)
            {
                Run(SaveSessionAsync);
            }
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AddHandler(PointerMovedEvent, TabDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, TabDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerCaptureLost += TabDragCaptureLost;
        AddHandler(KeyDownEvent, OnWorkspaceKey, RoutingStrategies.Tunnel);
        Closing += WindowClosing;
        ShowGroups();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        EnsureIdleUnloadTimer();
        if (!_restoreWorkspace)
        {
            _restoreFinished = true;
            _checkpoint.Start();
            return;
        }

        await GuardAsync(async () =>
        {
            await RestoreSessionAsync();
            if (_closed) return;
            await RestoreAssignedWorkspaceWindowsAsync();
            if (_closeAfterStartupValidation)
            {
                Close();
                return;
            }
            await OpenPathsAsync(_startupPaths);
            if (!_closed && _tabs.Count == 0)
            {
                await ShowHomeAsync();
            }
            if (!_closed && _workspaceLoadWarning is not null)
                SetStatus(_workspaceLoadWarning);
        });
        if (_closed || _closing) return;
        _restoreFinished = true;
        _checkpoint.Start();
        if (_settings.AutoCheckUpdates && Environment.GetEnvironmentVariable("RUSTTEXT_PROFILE_DIR") is null)
        {
            Run(() => CheckUpdatesAsync(manual: false));
        }
    }

    private Group MakeGroup(long? id = null)
    {
        var group = new Group(id ?? NextId());
        var add = Button("+", () => { _active = group; NewTab(); });
        ToolTip.SetTip(add, "Новая вкладка");
        var restore = Button("↶", () => Run(RestoreLastClosedAsync));
        ToolTip.SetTip(restore, "Восстановить закрытую вкладку");
        var across = Button("│", () => SplitGroup(group, Orientation.Horizontal));
        ToolTip.SetTip(across, "Разделить слева и справа");
        var below = Button("─", () => SplitGroup(group, Orientation.Vertical));
        ToolTip.SetTip(below, "Разделить сверху и снизу");
        group.CloseButton = Button("×", () => CloseGroup(group));
        ToolTip.SetTip(group.CloseButton, "Закрыть группу");
        AutomationProperties.SetName(group.CloseButton, "Закрыть группу");
        var search = Button("⌕", () =>
        {
            if (SelectedIn(group) is { } tab)
            {
                ActivateTab(tab);
            }

            OpenSearchPanel();
        });
        ToolTip.SetTip(search, "Найти (Ctrl+F)");
        AutomationProperties.SetName(search, "Найти");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var button in new[] { add, restore, across, below, group.CloseButton, search })
        {
            button.Classes.Add("focus-icon");
            tools.Children.Add(button);
        }

        group.Tabs.HeaderActions = tools;
        group.View.Children.Add(group.Tabs);
        group.Tabs.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, group.Tabs))
            {
                var selected = SelectedIn(group);
                if (selected is not null && ReferenceEquals(group, _active))
                {
                    ActivateTab(selected);
                }
            }
        };
        group.View.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!ReferenceEquals(_active, group))
            {
                _active = group;
                if (SelectedIn(group) is { } tab)
                {
                    ActivateTab(tab);
                }
            }
        }, RoutingStrategies.Tunnel);
        group.Tabs.DoubleTapped += (_, e) =>
        {
            var inTabBar = false;
            for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is TabItem || visual is Avalonia.Controls.Button || visual is Avalonia.Controls.Primitives.ScrollBar)
                {
                    break;
                }

                if (visual is Border { Name: "TabBar" })
                {
                    inTabBar = true;
                    break;
                }
            }

            if (inTabBar)
            {
                _active = group;
                NewTab();
            }
        };
        return group;
    }

    private WorkTab AddTab(long? id = null, Group? group = null, bool select = true)
    {
        group ??= _active;
        var tab = new WorkTab(id ?? NextId(), group);
        _workspace.NextId = Math.Max(_workspace.NextId, tab.Id);
        _tabs.Add(tab.Id, tab);
        var label = new TextBlock
        {
            Text = "Без имени", TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200, VerticalAlignment = VerticalAlignment.Center
        };
        var dirtyMark = new TextBlock
        {
            Text = "●", FontSize = 9, Foreground = FocusTheme.Accent, Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        var close = Button("", () => Run(() => CloseTabAsync(tab)));
        close.Content = new Avalonia.Controls.Shapes.Path
        {
            Width = 12, Height = 12, Data = Geometry.Parse("M 2,2 L 10,10 M 10,2 L 2,10"),
            Stroke = FocusTheme.Muted, StrokeThickness = 1.65, StrokeLineCap = PenLineCap.Round
        };
        close.Classes.Add("focus-icon");
        AutomationProperties.SetName(close, "Закрыть вкладку");
        ToolTip.SetTip(close, "Закрыть вкладку");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 5 };
        header.Children.Add(label);
        var loadMark = new TextBlock
        {
            IsVisible = false, FontSize = 12, Foreground = FocusTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        var marks = new Grid { Width = 13, Background = Brushes.Transparent };
        dirtyMark.HorizontalAlignment = HorizontalAlignment.Center;
        marks.Children.Add(dirtyMark);
        marks.Children.Add(loadMark);
        Grid.SetColumn(marks, 1);
        header.Children.Add(marks);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        tab.Label = label;
        tab.DirtyMark = dirtyMark;
        tab.LoadMark = loadMark;
        tab.HeaderMarks = marks;
        tab.Item.Classes.Add("focus-tab");
        tab.Item.Header = header;
        var saveCopy = Command("Сохранить копию…", () => Run(() => SaveCurrentDraftCopyAsync(tab)));
        var autoRefresh = Command("Автообновление с диска", () => SetTabAutoRefresh(tab, !TabAutoRefresh(tab)));
        autoRefresh.ToggleType = MenuItemToggleType.CheckBox;
        var context = new ContextMenu
        {
            Items =
            {
                Command("Показать в Проводнике", () => RevealTab(tab)),
                Command("Скопировать путь", () => Run(() => CopyTextAsync(tab.Path))),
                saveCopy,
                autoRefresh,
                Command("Переместить в новое окно", () => OpenTabWindow(tab)),
                Command("Закрыть", () => Run(() => CloseTabAsync(tab)))
            }
        };
        context.Opening += (_, _) =>
        {
            saveCopy.IsVisible = tab.Recovery is not null;
            saveCopy.IsEnabled = tab.Log?.HasPendingEdit != true;
            autoRefresh.IsVisible = tab.Path is not null && tab.Recovery is null;
            autoRefresh.IsEnabled = !tab.Loading;
            autoRefresh.IsChecked = TabAutoRefresh(tab);
        };
        tab.Item.ContextMenu = context;
        RegisterTabDrag(tab, close);
        tab.Item.DoubleTapped += (_, e) =>
        {
            if (IsWithin(e.Source as Visual, close)) return;
            e.Handled = true;
            if (_draggedTabId != tab.Id) Run(() => CloseTabAsync(tab));
        };
        group.Tabs.Items.Add(tab.Item);
        if (select)
        {
            _active = group;
            group.Tabs.SelectedItem = tab.Item;
            ActivateTab(tab);
        }

        return tab;
    }

    private void MoveTab(WorkTab tab, Group target, WorkTab? before)
    {
        if (ReferenceEquals(tab, before))
        {
            return;
        }

        tab.Group.Tabs.Items.Remove(tab.Item);
        var insertion = before is null ? target.Tabs.Items.Count : target.Tabs.Items.IndexOf(before.Item);
        target.Tabs.Items.Insert(Math.Max(0, insertion), tab.Item);
        tab.Group = target;

        _active = target;
        target.Tabs.SelectedItem = tab.Item;
        ActivateTab(tab);
    }

    private void SplitGroup(Group group, Orientation orientation)
    {
        if (group.Axis is not null)
        {
            return;
        }

        var previous = group;
        var second = MakeGroup();
        var split = new Group(NextId()) { Axis = orientation, First = previous, Second = second };
        _root = ReplaceGroup(_root, group, split);
        _active = second;
        ShowGroups();
        if (SelectedIn(previous) is { } tab && previous.Tabs.Items.Count > 1)
        {
            MoveTab(tab, second, null);
        }
        else
        {
            NewTab();
        }
    }

    private static Group ReplaceGroup(Group node, Group source, Group target)
    {
        if (ReferenceEquals(node, source))
        {
            return target;
        }

        if (node.First is { } first && node.Second is { } second)
        {
            node.First = ReplaceGroup(first, source, target);
            node.Second = ReplaceGroup(second, source, target);
        }

        return node;
    }

    private void CloseGroup(Group group)
    {
        var parent = FindParentGroup(_root, group);
        if (group.Axis is not null || parent is not { First: { } first, Second: { } second })
        {
            return;
        }

        var closingFirst = ReferenceEquals(group, first);
        var sibling = closingFirst ? second : first;
        var neighbor = closingFirst ? Groups(sibling).First() : Groups(sibling).Last();
        var keepActive = ReferenceEquals(_active, group);
        var selected = keepActive ? SelectedIn(group) ?? SelectedIn(neighbor) : SelectedIn(neighbor);
        if (keepActive)
        {
            _active = neighbor;
        }

        var tabsByItem = _tabs.Values.Where(tab => ReferenceEquals(tab.Group, group)).ToDictionary(tab => tab.Item);
        var insertion = closingFirst ? 0 : neighbor.Tabs.Items.Count;
        foreach (var item in group.Tabs.Items.OfType<TabItem>().ToArray())
        {
            var tab = tabsByItem[item];
            group.Tabs.Items.Remove(item);
            tab.Group = neighbor;
            neighbor.Tabs.Items.Insert(insertion++, item);
        }

        _root = ReplaceGroup(_root, parent, sibling);
        ShowGroups();
        if (selected is not null)
        {
            neighbor.Tabs.SelectedItem = selected.Item;
        }
        if (SelectedIn(_active) is { } active)
        {
            ActivateTab(active);
        }
    }

    private static Group? FindParentGroup(Group node, Group child)
    {
        if (node is not { First: { } first, Second: { } second })
        {
            return null;
        }

        return ReferenceEquals(first, child) || ReferenceEquals(second, child) ? node
            : FindParentGroup(first, child) ?? FindParentGroup(second, child);
    }

    private void ShowGroups()
    {
        _refreshHome = null;
        _home = false;
        WorkspaceHost.Content = null;
        foreach (var grid in _splitGrids)
        {
            grid.Children.Clear();
        }

        _splitGrids.Clear();
        foreach (var group in Groups())
        {
            group.CloseButton.IsEnabled = !ReferenceEquals(group, _root);
            group.CloseButton.IsVisible = group.CloseButton.IsEnabled;
        }
        WorkspaceHost.Content = BuildGroupView(_root);
        SetCaption(SelectedIn(_active)?.Label.Text);
    }

    private Control BuildGroupView(Group group)
    {
        if (group.Axis is null || group.First is null || group.Second is null)
        {
            return group.View;
        }

        var first = BuildGroupView(group.First);
        var second = BuildGroupView(group.Second);
        var horizontal = group.Axis == Orientation.Horizontal;
        var split = new Grid();
        _splitGrids.Add(split);
        var splitter = new GridSplitter
        {
            Background = FocusTheme.Border,
            ResizeDirection = horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows
        };
        if (horizontal)
        {
            split.ColumnDefinitions = new ColumnDefinitions
            {
                new(new GridLength(group.Ratio, GridUnitType.Star)),
                new(new GridLength(5)),
                new(new GridLength(1 - group.Ratio, GridUnitType.Star))
            };
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(second, 2);
        }
        else
        {
            split.RowDefinitions = new RowDefinitions
            {
                new(new GridLength(group.Ratio, GridUnitType.Star)),
                new(new GridLength(5)),
                new(new GridLength(1 - group.Ratio, GridUnitType.Star))
            };
            Grid.SetRow(splitter, 1);
            Grid.SetRow(second, 2);
        }

        splitter.DragCompleted += (_, _) =>
        {
            var left = horizontal ? first.Bounds.Width : first.Bounds.Height;
            var right = horizontal ? second.Bounds.Width : second.Bounds.Height;
            if (left + right > 0)
            {
                group.Ratio = Math.Clamp(left / (left + right), .1, .9);
            }
        };
        split.Children.Add(first);
        split.Children.Add(splitter);
        split.Children.Add(second);
        return split;
    }

    private IEnumerable<Group> Groups(Group? node = null)
    {
        node ??= _root;
        if (node.Axis is null)
        {
            yield return node;
            yield break;
        }

        foreach (var group in Groups(node.First!))
        {
            yield return group;
        }

        foreach (var group in Groups(node.Second!))
        {
            yield return group;
        }
    }

    private WorkTab? SelectedIn(Group group) =>
        group.Tabs.SelectedItem is TabItem item ? _tabs.Values.FirstOrDefault(tab => ReferenceEquals(tab.Item, item)) : null;

    private WorkTab? ActiveTab => _home ? null : SelectedIn(_active);
    private long NextId() => ++_workspace.NextId;

    private void ActivateTab(WorkTab tab)
    {
        if (_home)
        {
            ShowGroups();
        }

        _active = tab.Group;
        tab.LastWorked = DateTimeOffset.Now;
        if (tab.Deferred is not null && !tab.Loading && !tab.CheckingSession && !_mountingSessionTabs)
        {
            Run(() => ActivateDeferredAsync(tab));
        }

        UpdateStatus(tab);
        RefreshSearchForActiveTab();
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            var paths = new List<string>(files.Length);
            foreach (var file in files)
            {
                using (file)
                {
                    if (file.TryGetLocalPath() is { } path)
                    {
                        paths.Add(path);
                    }
                }
            }

            e.Handled = true;
            Run(() => OpenPathsAsync(paths));
        }
    }

    private async void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (_validatingStartupSession)
        {
            if (_tabs.Values.Any(tab => !tab.CheckingSession && (tab.Dirty || tab.Saving || tab.Log?.HasPendingEdit == true)))
            {
                _closeAfterStartupValidation = true;
                e.Cancel = true;
            }
            else
            {
                CloseWorkspaceContents();
            }
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        if (_tabs.Values.Any(tab => tab.Closing))
        {
            SetStatus("Дождитесь сохранения закрываемой вкладки.");
            return;
        }
        if (_tabs.Values.FirstOrDefault(tab => tab.Saving || tab.Log?.HasPendingEdit == true) is { } pending)
        {
            Select(pending);
            SetStatus("Завершите сохранение или отмените правки страницы перед закрытием окна.");
            return;
        }

        _closing = true;
        CancelTabDrag();
        _checkpoint.Stop();
        var closeGateHeld = false;
        var sessionSaved = false;
        WorkTab[] archivedTabs = [];
        var priorHistory = new List<(int Index, SessionTab Tab)>();
        try
        {
            await _workspace.CloseGate.WaitAsync();
            closeGateHeld = true;
            if (FindOtherSessionWindow() is not null)
            {
                archivedTabs = TabsInVisualOrder().ToArray();
                var closingIds = archivedTabs.Select(tab => tab.Id).ToHashSet();
                for (var index = 0; index < _closedTabs.Count; index++)
                {
                    if (closingIds.Contains(_closedTabs[index].Id))
                    {
                        priorHistory.Add((index, _closedTabs[index]));
                    }
                }

                foreach (var tab in archivedTabs)
                    await RememberClosedAsync(tab, preserveUnsavedText: true, deferHistoryLimit: true);

                await SaveWorkspaceSessionAsync(excludeWindow: this);
                if (_closedTabs.Count > 100)
                {
                    _closedTabs.RemoveRange(100, _closedTabs.Count - 100);
                    RefreshHistoryMenus();
                }
            }
            else
            {
                await SaveSessionAsync();
            }
            sessionSaved = true;

            CloseWorkspaceContents();
            Close();
        }
        catch (Exception error) when (IsUserError(error) && !sessionSaved)
        {
            if (archivedTabs.Length != 0)
            {
                var closingIds = archivedTabs.Select(tab => tab.Id).ToHashSet();
                _closedTabs.RemoveAll(item => closingIds.Contains(item.Id));
                foreach (var (index, tab) in priorHistory.OrderBy(entry => entry.Index))
                {
                    _closedTabs.Insert(Math.Clamp(index, 0, _closedTabs.Count), tab);
                }
                RefreshHistoryMenus();
            }

            SetStatus($"Не удалось записать сессию. {error.Message}");
            _closing = false;
            _checkpoint.Start();
        }
        finally
        {
            if (closeGateHeld)
            {
                _workspace.CloseGate.Release();
            }
        }
    }

    private void CloseWorkspaceContents()
    {
        _closing = _closed = true;
        CancelTabDrag();
        _idleUnloadTimer?.Stop();
        _lifetime.Cancel();
        _searchCancellation?.Cancel();
        foreach (var tab in _tabs.Values)
        {
            tab.LoadCancellation?.Cancel();
            tab.Log?.Close();
            DetachEditor(tab);
        }
    }

    private void HomeClicked(object? sender, RoutedEventArgs e) => Run(ShowHomeAsync);

    private void Run(Func<Task> action) => _ = GuardAsync(action);

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("Операция отменена.");
            }
        }
        catch (Exception error) when (IsUserError(error))
        {
            if (!_closed)
            {
                SetStatus(error.Message);
            }
        }
    }

    private static bool IsUserError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or FormatException or ArgumentException or InvalidOperationException or OverflowException or TimeoutException
        or System.Text.Json.JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException
        or System.ComponentModel.Win32Exception or HttpRequestException;

    private void SetStatus(string? message) => StatusText.Text = message;

    private void SetCaption(string? document)
    {
        Title = document is null ? "RustText" : $"{document} — RustText";
    }

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        return button;
    }

    private static MenuItem Command(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem MenuGroup(string label, params object[] items)
    {
        var group = new MenuItem { Header = label };
        foreach (var item in items)
        {
            group.Items.Add(item);
        }

        return group;
    }

    private async Task CopyTextAsync(string? text)
    {
        if (text is not null && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    internal async Task ReceivePathsAsync(IReadOnlyList<string> paths, bool activate)
    {
        await GuardAsync(() => OpenPathsAsync(paths));
        if (activate)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }
    }

    private sealed class Group(long id)
    {
        public long Id { get; } = id;
        public Grid View { get; } = new();
        public WorkspaceTabs Tabs { get; } = new();
        public Button CloseButton { get; set; } = null!;
        public Orientation? Axis { get; init; }
        public Group? First { get; set; }
        public Group? Second { get; set; }
        public double Ratio { get; set; } = .5;
    }
}
