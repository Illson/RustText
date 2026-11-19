using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RustText.Demo;

public partial class MainWindow
{
    private void WindowDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) ToggleMaximized();
        else BeginMoveDrag(e);
    }

    private void MinimizeWindowClicked(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindowClicked(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseWindowClicked(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized()
    {
        if (CanMaximize) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CaptionStateChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.IsVisible = !maximized;
        RestoreGlyph.IsVisible = maximized;
        var label = maximized ? "Восстановить размер окна" : "Развернуть";
        AutomationProperties.SetName(CaptionMaximize, label);
        ToolTip.SetTip(CaptionMaximize, label);
    }
}
