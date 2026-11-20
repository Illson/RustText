using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace RustText.Demo;

public partial class MainWindow
{
    private TabPress? _tabPress;
    private long? _draggedTabId;

    private bool CanDragTab(WorkTab tab) => !_closing && !_closed && !_validatingStartupSession && !tab.Loading && !tab.CheckingSession
        && tab.Log?.IsConsole != true
        && !tab.Closing && !tab.Saving && tab.Log?.HasPendingEdit != true && _tabs.ContainsKey(tab.Id);

    private void RegisterTabDrag(WorkTab tab, Button close)
    {
        tab.Item.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !IsWithin(e.Source as Visual, close)
                && new Rect(tab.Item.Bounds.Size).Contains(e.GetPosition(tab.Item))
                && CanDragTab(tab) && _workspace.Drag is null)
            {
                if (e.ClickCount == 1) _draggedTabId = null;
                _tabPress = new(tab, e.Pointer, e.GetPosition(this));
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static bool IsWithin(Visual? visual, Visual ancestor)
    {
        for (; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, ancestor)) return true;
        }
        return false;
    }

    private void TabDragMoved(object? sender, PointerEventArgs e)
    {
        var drag = _workspace.Drag;
        if (drag is null)
        {
            if (_tabPress is not { } press || !ReferenceEquals(press.Pointer, e.Pointer)) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CanDragTab(press.Tab))
            {
                _tabPress = null;
                return;
            }
            var distance = e.GetPosition(this) - press.Origin;
            if (distance.X * distance.X + distance.Y * distance.Y < 49) return;

            _tabPress = null;
            drag = new TabDrag(this, press.Tab, press.Pointer);
            _workspace.Drag = drag;
            press.Tab.Item.Opacity = 0.32;
            e.Pointer.Capture(this);
        }

        if (!ReferenceEquals(drag.Source, this) || !ReferenceEquals(drag.Pointer, e.Pointer)) return;
        var screenPoint = this.PointToScreen(e.GetPosition(this));
        UpdateTabDrag(drag, screenPoint);
        e.Handled = true;
    }

    private void TabDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        _tabPress = null;
        if (_workspace.Drag is not { } drag || !ReferenceEquals(drag.Source, this)
            || !ReferenceEquals(drag.Pointer, e.Pointer)) return;

        var screenPoint = this.PointToScreen(e.GetPosition(this));
        var window = DropWindowAt(screenPoint);
        var target = window?.TabDropAt(screenPoint, drag.Tab);
        CancelTabDrag();
        e.Handled = true;
        if (!CanDragTab(drag.Tab)) return;

        Run(() =>
        {
            if (target is { } drop) TransferTab(drag.Tab, drop.Window, drop.Group, drop.Before);
            else if (window is null) OpenTabWindow(drag.Tab, screenPoint);
            return Task.CompletedTask;
        });
    }

    private void TabDragCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_workspace.Drag is { } drag && ReferenceEquals(drag.Source, this) && ReferenceEquals(drag.Pointer, e.Pointer)
            && !ReferenceEquals(e.Pointer.Captured, this)) CancelTabDrag();
    }

    private bool CancelTabDrag()
    {
        _tabPress = null;
        if (_workspace.Drag is not { } drag || !ReferenceEquals(drag.Source, this)) return false;
        _workspace.Drag = null;
        _draggedTabId = drag.Tab.Id;
        drag.Tab.Item.ClearValue(OpacityProperty);
        foreach (var item in drag.Shifts.Keys) item.ClearValue(RenderTransformProperty);
        if (drag.Preview.IsVisible) drag.Preview.Close();
        if (ReferenceEquals(drag.Pointer.Captured, this)) drag.Pointer.Capture(null);
        return true;
    }

    private MainWindow? DropWindowAt(PixelPoint screenPoint)
    {
        if (ContainsPoint(this)) return this;
        return _workspace.Windows.LastOrDefault(ContainsPoint);

        bool ContainsPoint(MainWindow window) => !window._closing && window.IsVisible && window.WindowState != WindowState.Minimized
            && new Rect(window.Bounds.Size).Contains(window.PointToClient(screenPoint));
    }

    private TabDrop? TabDropAt(PixelPoint screenPoint, WorkTab source)
    {
        if (_home) return new(this, _active, null);
        var point = this.PointToClient(screenPoint);
        var group = Groups().FirstOrDefault(candidate => BoundsInWindow(candidate.View).Contains(point));
        if (group is null) return null;
        var header = group.Tabs.HeaderViewport;
        if (header is null || !BoundsInWindow(header).Contains(point))
            return ReferenceEquals(group, source.Group) ? null : new(this, group, null);

        var before = group.Tabs.Items.OfType<TabItem>()
            .Where(item => !ReferenceEquals(item, source.Item))
            .FirstOrDefault(item => point.X < BoundsInWindow(item).Center.X);
        return new(this, group, before is null ? null : _tabs.Values.First(tab => ReferenceEquals(tab.Item, before)));
    }

    private Rect BoundsInWindow(Visual visual) => visual.GetVisualParent()?.TranslatePoint(visual.Bounds.Position, this) is { } point
        ? new Rect(point, visual.Bounds.Size) : default;

    private void UpdateTabDrag(TabDrag drag, PixelPoint screenPoint)
    {
        var window = DropWindowAt(screenPoint);
        var target = window?.TabDropAt(screenPoint, drag.Tab);
        drag.Preview.Follow(screenPoint, detached: window is null);
        if (target == drag.Target) return;
        drag.Target = target;
        var offsets = new Dictionary<TabItem, double>();
        if (target is { } drop)
        {
            var items = drop.Group.Tabs.Items;
            var from = items.IndexOf(drag.Tab.Item);
            var to = drop.Before is { } before ? items.IndexOf(before.Item) : items.Count;
            var width = drag.Tab.Item.Bounds.Width + drag.Tab.Item.Margin.Left + drag.Tab.Item.Margin.Right;
            for (var index = 0; index < items.Count; index++)
            {
                var offset = from < 0 ? (index >= to ? width : 0)
                    : index > from && index < to ? -width : index >= to && index < from ? width : 0;
                if (offset != 0 && items[index] is TabItem item) offsets.Add(item, offset);
            }
        }

        foreach (var (item, shift) in drag.Shifts) shift.X = offsets.GetValueOrDefault(item);
        foreach (var (item, offset) in offsets)
        {
            if (drag.Shifts.ContainsKey(item)) continue;
            var shift = new TranslateTransform
            {
                Transitions = [new DoubleTransition
                {
                    Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new QuadraticEaseOut()
                }]
            };
            drag.Shifts.Add(item, shift);
            item.RenderTransform = shift;
            shift.X = offset;
        }
    }

    private sealed record TabPress(WorkTab Tab, IPointer Pointer, Point Origin);
    private sealed record TabDrop(MainWindow Window, Group Group, WorkTab? Before);

    private sealed class TabDrag(MainWindow source, WorkTab tab, IPointer pointer)
    {
        public MainWindow Source { get; } = source;
        public WorkTab Tab { get; } = tab;
        public IPointer Pointer { get; } = pointer;
        public TabDragPreview Preview { get; } = new(tab.Label.Text ?? "Без имени", tab.Dirty, tab.Item.Bounds.Width);
        public TabDrop? Target { get; set; }
        public Dictionary<TabItem, TranslateTransform> Shifts { get; } = [];
    }

    private sealed class TabDragPreview : Window
    {
        private readonly ScaleTransform _scale = new(.92, .92);

        public TabDragPreview(string title, bool dirty, double width)
        {
            Title = "Перетаскивание вкладки";
            Width = Math.Clamp(width + 24, 142, 260);
            Height = 48;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowDecorations = WindowDecorations.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            Topmost = true;
            IsHitTestVisible = false;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            _scale.Transitions =
            [
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new QuadraticEaseOut() },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new QuadraticEaseOut() }
            ];
            Content = new Border
            {
                Margin = new Thickness(4),
                Padding = new Thickness(11, 0),
                CornerRadius = new CornerRadius(6),
                Background = FocusTheme.Editor,
                BorderBrush = FocusTheme.Accent,
                BorderThickness = new Thickness(1, 2, 1, 1),
                RenderTransform = _scale,
                Child = new TextBlock
                {
                    Text = dirty ? title + "  ●" : title,
                    Foreground = FocusTheme.Text,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        public void Follow(PixelPoint pointer, bool detached)
        {
            Position = new PixelPoint(pointer.X + 10, pointer.Y + 12);
            if (!IsVisible) Show();
            _scale.ScaleX = _scale.ScaleY = detached ? 1.03 : 1;
        }
    }
}
