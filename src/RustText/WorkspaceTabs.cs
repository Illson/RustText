using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RustText.Demo;

internal sealed class WorkspaceTabs : TabControl
{
    private const double WheelScrollStep = 96;

    public ScrollViewer? HeaderViewport { get; private set; }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        HeaderViewport?.RemoveHandler(PointerWheelChangedEvent, OnHeaderWheelChanged);
        HeaderViewport = e.NameScope.Find<ScrollViewer>("TabHeaderScroll");
        HeaderViewport?.AddHandler(PointerWheelChangedEvent, OnHeaderWheelChanged, RoutingStrategies.Tunnel);
    }

    private static void OnHeaderWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewport || e.Delta.X != 0 || e.Delta.Y == 0)
            return;

        var offset = viewport.Offset;
        var x = Math.Clamp(offset.X - e.Delta.Y * WheelScrollStep, 0, viewport.ScrollBarMaximum.X);
        if (x == offset.X)
            return;

        viewport.SetCurrentValue(ScrollViewer.OffsetProperty, offset.WithX(x));
        e.Handled = true;
    }

    public static readonly StyledProperty<Control?> HeaderActionsProperty =
        AvaloniaProperty.Register<WorkspaceTabs, Control?>(nameof(HeaderActions));

    public Control? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }
}
