using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RustText.Demo;

public partial class MainWindow
{
    private bool _glassRequested;

    private void ApplyWorkspaceGlass()
    {
        var intensity = _workspace.GlassPreviewIntensity ?? Settings.GlassIntensity;
        FocusTheme.ApplyGlass(intensity);
        foreach (var window in _workspace.Windows)
            window.ApplyGlassToWindow(intensity);
    }

    private void ApplyGlassToWindow(int intensity)
    {
        var requestGlass = intensity > 0 && OperatingSystem.IsWindows();
        if (requestGlass == _glassRequested) return;
        _glassRequested = requestGlass;
        TransparencyLevelHint = requestGlass
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        UpdateGlassBackground();
    }

    private void GlassTransparencyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ActualTransparencyLevelProperty) UpdateGlassBackground();
    }

    private void UpdateGlassBackground()
    {
        var hasBlur = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
            || ActualTransparencyLevel == WindowTransparencyLevel.Blur;
        Background = _glassRequested && hasBlur ? Brushes.Transparent : FocusTheme.Editor;
    }
}
