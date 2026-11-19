using Avalonia.Controls;

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

    private AppTheme SelectedTheme => ThemeOptions.FirstOrDefault(option => option.Key == Settings.Theme).Theme;

    private MenuItem BuildThemeMenu()
    {
        _themeMenu = new MenuItem { Header = "_Тема" };
        foreach (var option in ThemeOptions)
        {
            var item = Command(option.Label, () => Run(() => SelectThemeAsync(option.Key)));
            item.ToggleType = MenuItemToggleType.Radio;
            _themeMenu.Items.Add(item);
        }
        RefreshThemeMenu();
        return _themeMenu;
    }

    private async Task SelectThemeAsync(string key)
    {
        try { await SaveWorkspaceSettingsAsync(settings => settings with { Theme = key }); }
        finally { RefreshThemeMenu(); }
    }

    private void ApplyWorkspaceTheme()
    {
        FocusTheme.Apply(SelectedTheme);
        foreach (var window in _workspace.Windows)
            window.RefreshThemeMenu();
    }

    private void RefreshThemeMenu()
    {
        _themeMenu.IsEnabled = _workspace.SettingsReady;
        var selected = SelectedTheme;
        for (var index = 0; index < ThemeOptions.Length; index++)
            ((MenuItem)_themeMenu.Items[index]!).IsChecked = ThemeOptions[index].Theme == selected;
    }
}
