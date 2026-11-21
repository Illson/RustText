using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RustText.Demo;

public partial class MainWindow
{
    private bool _glassRequested;

    private static int SavedGlassIntensity(UserSettings settings) =>
        settings.GlassEnabled ? Math.Clamp(settings.GlassIntensity, 0, 100) : 0;

    private int CurrentGlassIntensity => _workspace.GlassPreviewIntensity ?? SavedGlassIntensity(Settings);

    private async Task ToggleGlassAsync()
    {
        try
        {
            await SaveWorkspaceSettingsAsync(settings => settings with
            {
                GlassEnabled = SavedGlassIntensity(settings) == 0,
                GlassIntensity = settings.GlassIntensity <= 0
                    ? UserSettings.DefaultGlassIntensity : Math.Min(settings.GlassIntensity, 100)
            });
        }
        finally { RefreshGlassMenu(); }
    }

    private async Task ShowGlassIntensityAsync()
    {
        if (_workspace.GlassPreviewIntensity is not null) return;

        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = SavedGlassIntensity(Settings),
            TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10
        };
        AutomationProperties.SetName(slider, "Интенсивность стекла");
        var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        void Preview()
        {
            var intensity = (int)slider.Value;
            value.Text = intensity == 0 ? "0% · Выкл." : $"{intensity}%";
            _workspace.GlassPreviewIntensity = intensity;
            ApplyWorkspaceGlass();
        }

        var body = DialogPanel(value, slider);
        var dialog = Dialog("Интенсивность стекла", body, scrollContent: false);
        dialog.MinHeight = 150;
        dialog.CanResize = false;
        body.Children.Add(Row(
            FocusButton(Button("Применить", () => dialog.Close(true)), primary: true),
            FocusButton(Button("Отмена", () => dialog.Close(false)))));
        slider.ValueChanged += (_, _) => Preview();
        dialog.Opened += (_, _) => slider.Focus();
        Preview();
        try
        {
            if (!await dialog.ShowDialog<bool>(this)) return;

            var intensity = (int)slider.Value;
            await SaveWorkspaceSettingsAsync(settings => settings with
            {
                GlassEnabled = intensity > 0,
                GlassIntensity = intensity > 0 ? intensity : settings.GlassIntensity
            });
        }
        finally
        {
            _workspace.GlassPreviewIntensity = null;
            ApplyWorkspaceGlass();
        }
    }

    private void RefreshGlassMenu()
    {
        var canChange = _workspace.GlassPreviewIntensity is null;
        _glassToggle.IsEnabled = _glassIntensityItem.IsEnabled = canChange;
        _glassToggle.IsChecked = CurrentGlassIntensity > 0;
    }

    private void ApplyWorkspaceGlass()
    {
        var intensity = CurrentGlassIntensity;
        FocusTheme.ApplyGlass(intensity);
        foreach (var window in _workspace.Windows)
        {
            window.ApplyGlassToWindow(intensity);
            window.RefreshGlassMenu();
        }
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
