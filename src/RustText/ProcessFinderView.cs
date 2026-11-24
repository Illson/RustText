using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RustText.Demo.Platform;

namespace RustText.Demo;

internal sealed class ProcessFinderView : UserControl, IDisposable
{
    private readonly PortOwnersView _ports = new();
    private readonly TextBox _query = new() { PlaceholderText = "PID или часть заголовка окна" };
    private readonly TabControl _modes;

    public ProcessFinderView()
    {
        _modes = new TabControl
        {
            Items =
            {
                new TabItem { Header = "По порту", Content = _ports },
                new TabItem { Header = "По заголовку окна", Content = CreateWindowPane() }
            },
            SelectedIndex = 0
        };
        Content = _modes;
    }

    public void FocusInput()
    {
        if (_modes.SelectedIndex == 0) _ports.FocusInput();
        else _query.Focus();
    }

    public void Dispose() => _ports.Dispose();

    private Control CreateWindowPane()
    {
        var matches = new ListBox();
        var status = new TextBlock { Foreground = FocusTheme.Muted, TextWrapping = TextWrapping.Wrap };
        var find = new Button { Content = "Найти", Classes = { "focus-primary" } };
        var activate = new Button { Content = "Вывести окно", IsEnabled = false };
        AutomationProperties.SetName(_query, "PID или часть заголовка окна");
        AutomationProperties.SetName(matches, "Найденные окна");
        find.Click += (_, _) => Find();
        _query.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Find();
        };
        matches.SelectionChanged += (_, _) => activate.IsEnabled = matches.SelectedItem is not null;
        activate.Click += (_, _) =>
        {
            if (matches.SelectedItem is not ListBoxItem { Tag: WindowIdentity window }) return;
            status.Text = WindowFinder.ActivateWindow(window)
                ? "Окно выведено на передний план."
                : "Windows отклонила переключение либо окно было закрыто.";
        };

        var header = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(0, 0, 0, 8),
            Children = { _query, new WrapPanel { ItemSpacing = 8, Children = { find, activate } }, status }
        };
        DockPanel.SetDock(header, Dock.Top);
        return new DockPanel { Margin = new Thickness(12), Children = { header, matches } };

        void Find()
        {
            var term = _query.Text?.Trim() ?? "";
            var byPid = uint.TryParse(term, out var pid);
            matches.Items.Clear();
            foreach (var window in WindowFinder.EnumerateVisibleWindows()
                .Where(window => byPid ? window.ProcessId == pid : window.Title.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Items.Add(new ListBoxItem { Content = $"{window.ProcessId} · {window.Title}", Tag = window });
            }

            status.Text = $"Окон найдено · {matches.Items.Count}";
        }
    }
}
