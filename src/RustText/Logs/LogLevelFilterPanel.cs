using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using RustText.Demo.Editor;

namespace RustText.Demo.Logs;

internal sealed class LogLevelFilterPanel : UserControl
{
    private readonly Dictionary<LogLevel, ToggleButton> _options = [];
    private readonly Button _toggleAll;
    private bool _updating;

    internal LogLevel SelectedLevels { get; private set; } = LogLevel.All;
    internal event Action? LevelsChanged;
    internal event Action? CloseRequested;

    public LogLevelFilterPanel()
    {
        var row = new WrapPanel { ItemSpacing = 4, LineSpacing = 3 };
        AutomationProperties.SetName(this, "Видимые уровни лога");
        foreach (var (level, label) in new[] { (LogLevel.Fatal, "Fatal"), (LogLevel.Error, "Error"),
            (LogLevel.Warning, "Warn"), (LogLevel.Info, "Info"), (LogLevel.Debug, "Debug"),
            (LogLevel.Trace, "Trace"), (LogLevel.Other, "Без уровня") })
        {
            var option = SearchBar.Option(label, $"Показывать строки · {label}");
            option.IsChecked = true;
            option.IsCheckedChanged += (_, _) => UpdateMask();
            _options.Add(level, option);
            row.Children.Add(option);
        }
        _toggleAll = SearchBar.Command("Снять все", () =>
            SetLevels(SelectedLevels == LogLevel.All ? 0 : LogLevel.All));
        row.Children.Add(_toggleAll);
        row.Children.Add(SearchBar.Icon("×", "Сбросить и закрыть фильтр уровней", () =>
        {
            SetLevels(LogLevel.All);
            CloseRequested?.Invoke();
        }));
        Content = new Border { Background = FocusTheme.Chrome, Padding = new Thickness(6, 3), Child = row };
    }

    internal void SetLevels(LogLevel levels)
    {
        _updating = true;
        try
        {
            foreach (var (level, option) in _options) option.IsChecked = (levels & level) != 0;
        }
        finally { _updating = false; }
        UpdateMask();
    }

    private void UpdateMask()
    {
        if (_updating) return;
        LogLevel mask = 0;
        foreach (var (level, option) in _options)
            if (option.IsChecked == true) mask |= level;
        if (mask == SelectedLevels) return;
        SelectedLevels = mask;
        _toggleAll.Content = mask == LogLevel.All ? "Снять все" : "Выбрать все";
        LevelsChanged?.Invoke();
    }
}
