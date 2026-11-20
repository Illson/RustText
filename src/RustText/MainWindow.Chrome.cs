using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private bool _nativeBorderColorSubscribed;

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

    private void WindowChromeOpened(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;

        FocusTheme.ThemeChanged += ApplyNativeBorderColor;
        _nativeBorderColorSubscribed = true;
        ApplyNativeBorderColor();
    }

    private void WindowChromeClosed(object? sender, EventArgs e)
    {
        if (!_nativeBorderColorSubscribed) return;

        FocusTheme.ThemeChanged -= ApplyNativeBorderColor;
        _nativeBorderColorSubscribed = false;
    }

    private void ApplyNativeBorderColor()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } platformHandle)
            return;

        var colorRef = WindowFrameColor.ToColorRef(FocusTheme.ChromeColor);
        WindowFrameColor.SetBorderColor(platformHandle.Handle, colorRef);
    }
}

internal static class WindowFrameColor
{
    internal const uint DwmBorderColorAttribute = 34;

    internal static uint ToColorRef(Color color) =>
        (uint)(color.R | color.G << 8 | color.B << 16);

    internal static void SetBorderColor(IntPtr windowHandle, uint colorRef) =>
        _ = DwmSetWindowAttribute(windowHandle, DwmBorderColorAttribute, ref colorRef, sizeof(uint));

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle, uint attribute, ref uint value, uint valueSize);
}
