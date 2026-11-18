using Avalonia;
using Avalonia.Controls;

namespace RustText.Demo;

internal sealed class WorkspaceTabs : TabControl
{
    public static readonly StyledProperty<Control?> HeaderActionsProperty =
        AvaloniaProperty.Register<WorkspaceTabs, Control?>(nameof(HeaderActions));

    public Control? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }
}
