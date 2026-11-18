using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RustText.Demo.Logs;

namespace RustText.Demo;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> TabDrag = DataFormat.CreateStringApplicationFormat("rusttext-tab");
    private readonly string[] _startupPaths;
    private readonly List<Grid> _splitGrids = [];
    private readonly Dictionary<long, WorkTab> _tabs = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _checkpoint;
    private Group _root;
    private Group _active;
    private long _nextId;
    private bool _home;
    private bool _closing;
    private bool _closed;
    private bool _restoreFinished;
    private bool _wordWrap = true;
    private double _fontSize = 14;
    private int _titleLimit = 10;



    public MainWindow() : this([])
    {
    }

    internal MainWindow(string[] startupPaths)
    {
        _startupPaths = startupPaths;
        InitializeComponent();
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

            if (_sessionGate.CurrentCount > 0)
            {
                Run(SaveSessionAsync);
            }

            Run(CheckActiveFileAsync);
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AddHandler(KeyDownEvent, OnWorkspaceKey, RoutingStrategies.Tunnel);
        Closing += WindowClosing;
        ShowGroups();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await GuardAsync(async () =>
        {
            await RestoreSessionAsync();
            await OpenPathsAsync(_startupPaths);
            if (_tabs.Count == 0)
            {
                await ShowHomeAsync();
            }
        });
        _restoreFinished = true;
        _checkpoint.Start();
        if (_settings.AutoCheckUpdates && Environment.GetEnvironmentVariable("RUSTTEXT_PROFILE_DIR") is null)
        {
            Run(CheckUpdatesAsync);
        }
    }

    private Group MakeGroup(long? id = null)
    {
        var group = new Group(id ?? NextId());
        var add = Button("+", () => { _active = group; NewTab(); });
        ToolTip.SetTip(add, "Новая вкладка");
        var restore = Button("↶", () => Run(RestoreLastClosedAsync));
        ToolTip.SetTip(restore, "Восстановить закрытую вкладку");
        var across = Button("⇆", () => SplitGroup(group, Orientation.Horizontal));
        ToolTip.SetTip(across, "Разделить рядом");
        var below = Button("⇅", () => SplitGroup(group, Orientation.Vertical));
        ToolTip.SetTip(below, "Разделить снизу");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var button in new[] { add, restore, across, below })
        {
            tools.Children.Add(button);
        }

        group.View.RowDefinitions = new RowDefinitions("Auto,*");
        group.View.Children.Add(tools);
        Grid.SetRow(group.Tabs, 1);
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
            if (ReferenceEquals(e.Source, group.Tabs))
            {
                _active = group;
                NewTab();
            }
        };
        DragDrop.SetAllowDrop(group.View, true);
        AddTabDrop(group.View, group, null);
        return group;
    }

    private void AddTabDrop(Control control, Group group, WorkTab? insertBefore)
    {
        DragDrop.SetAllowDrop(control, true);
        control.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(TabDrag) is not null)
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        });
        control.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (long.TryParse(e.DataTransfer.TryGetValue(TabDrag), out var id) && _tabs.TryGetValue(id, out var tab))
            {
                MoveTab(tab, insertBefore?.Group ?? group, insertBefore);
                e.Handled = true;
            }
        });
    }

    private WorkTab AddTab(long? id = null, Group? group = null, bool select = true)
    {
        group ??= _active;
        var tab = new WorkTab(id ?? NextId(), group);
        _nextId = Math.Max(_nextId, tab.Id);
        _tabs.Add(tab.Id, tab);
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = "Без имени" };
        var close = Button("×", () => Run(() => CloseTabAsync(tab)));
        close.Padding = new Thickness(5, 0);
        close.Background = Brushes.Transparent;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        header.Children.Add(label);
        header.Children.Add(close);
        tab.Label = label;
        tab.Item.Header = header;
        tab.Item.ContextMenu = new ContextMenu
        {
            Items =
            {
                Command("Показать в Проводнике", () => RevealTab(tab)),
                Command("Скопировать путь", () => Run(() => CopyTextAsync(tab.Path))),
                Command("Закрыть", () => Run(() => CloseTabAsync(tab)))
            }
        };
        PointerPressedEventArgs? pressed = null;
        Point origin = default;
        header.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                pressed = e;
                origin = e.GetPosition(header);
            }
        }, RoutingStrategies.Tunnel);
        header.PointerReleased += (_, _) => pressed = null;
        header.PointerMoved += async (_, e) =>
        {
            if (pressed is { } start && e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                var distance = e.GetPosition(header) - origin;
                if (Math.Abs(distance.X) + Math.Abs(distance.Y) >= 8)
                {
                    pressed = null;
                    var data = new DataTransfer();
                    data.Add(DataTransferItem.Create(TabDrag, tab.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    await DragDrop.DoDragDropAsync(start, data, DragDropEffects.Move);
                }
            }
        };
        header.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            Run(() => CloseTabAsync(tab));
        };
        AddTabDrop(header, group, tab);
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
        WorkspaceHost.Content = BuildGroupView(_root);
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
            Background = new SolidColorBrush(Color.Parse("#3C4D5C")),
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
    private long NextId() => ++_nextId;

    private void ActivateTab(WorkTab tab)
    {
        if (_home)
        {
            ShowGroups();
        }

        _active = tab.Group;
        if (tab.Deferred is not null && !tab.Loading)
        {
            Run(() => ActivateDeferredAsync(tab));
        }

        UpdateStatus(tab);
        RefreshSearchHighlights();
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

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _checkpoint.Stop();
        try
        {
            await SaveSessionAsync();
        }
        catch (Exception error) when (IsUserError(error))
        {
            SetStatus($"Не удалось записать сессию. {error.Message}");
            _closing = false;
            _checkpoint.Start();
            return;
        }

        _closed = true;
        _lifetime.Cancel();
        _searchCancellation?.Cancel();
        foreach (var tab in _tabs.Values)
        {
            tab.LoadCancellation?.Cancel();
            tab.Log?.Close();
        }

        Close();
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
        public TabControl Tabs { get; } = new();
        public Orientation? Axis { get; init; }
        public Group? First { get; set; }
        public Group? Second { get; set; }
        public double Ratio { get; set; } = .5;
    }
}
