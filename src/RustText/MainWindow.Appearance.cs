using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RustText.Demo.Editor;

namespace RustText.Demo;

public partial class MainWindow
{
    private static readonly (AppTheme Theme, string Key, string Label)[] ThemeOptions =
    [
        (AppTheme.Focus, "focus", "Фокус"),
        (AppTheme.Neon, "neon", "Неон"),
        (AppTheme.ViceCity, "vice_city", "Vice City")
    ];

    private MenuItem _themeMenu = null!;
    private MenuItem _smoothCaretMenu = null!;
    private Slider _glassSlider = null!;
    private TextBlock _glassValue = null!;
    private int? _glassMenuRevision;
    private Task _glassMenuSave = Task.CompletedTask;

    private AppTheme SelectedTheme => ThemeOptions.FirstOrDefault(option => option.Key == WorkspaceThemeKey).Theme;
    private string WorkspaceThemeKey => GetWorkspaceThemeKey(_workspaceId);

    private string GetWorkspaceThemeKey(string workspaceId)
    {
        var saved = workspaceId == WorkspaceCatalogStore.DefaultWorkspaceId
            ? null : _workspace.Catalog?.Workspaces.FirstOrDefault(item => item.Id == workspaceId)?.Theme;
        return ThemeOptions.FirstOrDefault(option => option.Key == saved).Key
            ?? ThemeOptions.FirstOrDefault(option => option.Key == Settings.Theme).Key
            ?? ThemeOptions[0].Key;
    }

    private MenuItem BuildThemeMenu()
    {
        _themeMenu = new MenuItem { Header = "_Тема" };
        foreach (var option in ThemeOptions)
        {
            var item = Command(option.Label, () => Run(() => SelectThemeAsync(option.Key)));
            item.ToggleType = MenuItemToggleType.Radio;
            _themeMenu.Items.Add(item);
        }
        _themeMenu.Items.Add(new Separator());
        _smoothCaretMenu = new MenuItem { Header = "Плавный курсор", ToggleType = MenuItemToggleType.CheckBox };
        _themeMenu.Items.Add(_smoothCaretMenu);
        _themeMenu.Items.Add(BuildGlassMenuItem());
        _themeMenu.PropertyChanged += (_, change) =>
        {
            if (change.Property != MenuItem.IsSubMenuOpenProperty || _themeMenu.IsSubMenuOpen) return;
            if (_glassMenuRevision is { } revision && revision == _workspace.GlassPreviewRevision
                && _workspace.GlassPreviewIntensity is { } intensity)
                _glassMenuSave = GuardAsync(() => SaveGlassMenuAsync(intensity, revision));
            _glassMenuRevision = null;
        };
        RefreshThemeMenu();
        return _themeMenu;
    }

    private MenuItem BuildGlassMenuItem()
    {
        const string label = "Liquid Glass";
        _glassValue = new TextBlock { Width = 68, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _glassSlider = new Slider
        {
            Minimum = 0, Maximum = 100, Width = 114,
            TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10
        };
        AutomationProperties.SetName(_glassSlider, "Интенсивность Liquid Glass");
        var header = new StackPanel
        {
            Spacing = 10, Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, _glassSlider, _glassValue }
        };
        var item = new MenuItem { Header = header, Padding = new Thickness(9, 2) };
        AutomationProperties.SetName(item, label);
        item.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ReferenceEquals(e.Source, item))
                e.Handled = _glassSlider.Focus();
        };
        // Let the slider finish dragging without treating its release as a menu command.
        item.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            _glassSlider.Focus(NavigationMethod.Pointer);
            e.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _glassSlider.ValueChanged += (_, _) =>
        {
            var intensity = (int)_glassSlider.Value;
            if (intensity == EffectiveGlassIntensity) return;
            _workspace.GlassPreviewIntensity = intensity;
            _glassMenuRevision = ++_workspace.GlassPreviewRevision;
            ApplyWorkspaceGlass();
        };
        return item;
    }

    private async Task SaveGlassMenuAsync(int intensity, int revision)
    {
        try
        {
            await SaveWorkspaceSettingsAsync(settings => settings with { GlassIntensity = intensity });
        }
        finally
        {
            if (revision == _workspace.GlassPreviewRevision)
            {
                _workspace.GlassPreviewIntensity = null;
                ApplyWorkspaceGlass();
            }
        }
    }

    private void RefreshGlassMenu(int intensity)
    {
        _glassSlider.Value = intensity;
        _glassValue.Text = intensity == 0 ? "0% · Выкл." : $"{intensity}%";
    }

    private void UpdateTitlePalmClip(object? sender, EventArgs e)
    {
        if (TitlePalm.Parent is not Visual host || TitlePalmAnchor.TranslatePoint(default, host) is not { } origin) return;
        var offset = (TranslateTransform)TitlePalm.RenderTransform!;
        offset.X = origin.X;
        offset.Y = origin.Y;
        var height = TitleBar.Bounds.Height;
        if (!_home)
        {
            // Only extend into the top tab strip underneath the motif, not into the editor or a lower split.
            var center = new Point(TitlePalmAnchor.Bounds.Width / 2, height);
            foreach (var group in Groups())
            {
                var bar = group.Tabs.HeaderBar;
                if (bar is null || TitlePalmAnchor.TranslatePoint(center, bar) is not { } point
                    || Math.Abs(point.Y) > 1 || point.X < 0 || point.X >= bar.Bounds.Width)
                    continue;
                height += bar.Bounds.Height - point.Y;
                break;
            }
        }
        var clip = new Rect(0, 0, TitlePalm.Bounds.Width, height);
        if (TitlePalm.Clip?.Bounds != clip)
            TitlePalm.Clip = new RectangleGeometry(clip);
    }

    private async Task SelectThemeAsync(string key)
    {
        try
        {
            var workspaceId = _workspaceId;
            if (workspaceId == WorkspaceCatalogStore.DefaultWorkspaceId)
            {
                await SaveWorkspaceSettingsAsync(settings => settings with { Theme = key });
                return;
            }

            await _workspace.WorkspaceSwitchGate.WaitAsync(_lifetime.Token);
            try
            {
                _workspace.Catalog = await _workspace.CatalogStore.SetThemeAsync(workspaceId, key, _lifetime.Token);
                ApplyWorkspaceTheme();
            }
            finally { _workspace.WorkspaceSwitchGate.Release(); }
        }
        finally { RefreshThemeMenu(); }
    }

    private void ApplyWorkspaceTheme()
    {
        var active = _workspace.Windows.FirstOrDefault(window => window.IsActive && !window._closed) ?? this;
        FocusTheme.Apply(active.SelectedTheme);
        ApplyWorkspaceGlass();
        foreach (var window in _workspace.Windows)
        {
            window.RefreshThemeMenu();
            window.RefreshWorkspaceButton();
        }
    }

    private void SmoothCaretMenuChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != MenuItem.IsCheckedProperty) return;
        var enabled = _smoothCaretMenu.IsChecked;
        Run(async () =>
        {
            try { await SaveWorkspaceSettingsAsync(settings => settings with { SmoothCaret = enabled }); }
            finally { RefreshThemeMenu(); }
        });
    }

    private void RefreshThemeMenu()
    {
        _themeMenu.IsEnabled = _workspace.SettingsReady;
        _smoothCaretMenu.PropertyChanged -= SmoothCaretMenuChanged;
        _smoothCaretMenu.IsChecked = Settings.SmoothCaret;
        _smoothCaretMenu.PropertyChanged += SmoothCaretMenuChanged;
        SetValue(EditorCaret.SmoothProperty, Settings.SmoothCaret);
        var selected = SelectedTheme;
        for (var index = 0; index < ThemeOptions.Length; index++)
            ((MenuItem)_themeMenu.Items[index]!).IsChecked = ThemeOptions[index].Theme == selected;
        RefreshGlassMenu(EffectiveGlassIntensity);
    }
}
