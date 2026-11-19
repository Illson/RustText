using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace RustText.Demo;

internal sealed class WorkspaceTabs : TabControl
{
    public ScrollViewer? HeaderViewport { get; private set; }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        HeaderViewport = e.NameScope.Find<ScrollViewer>("TabHeaderScroll");
    }

    public static readonly StyledProperty<Control?> HeaderActionsProperty =
        AvaloniaProperty.Register<WorkspaceTabs, Control?>(nameof(HeaderActions));

    public Control? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }
}
