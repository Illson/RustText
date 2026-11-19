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
        RefreshThemeMenu();
        return _themeMenu;
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
