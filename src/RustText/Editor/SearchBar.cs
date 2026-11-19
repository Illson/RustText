using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace RustText.Demo.Editor;

internal static class SearchBar
{
    public static Grid MakeRows() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
        RowDefinitions = new RowDefinitions("Auto,Auto"),
        ColumnSpacing = 4,
        RowSpacing = 3,
    };

    public static void AddRow(Grid layout, int row, params Control[] cells)
    {
        for (var column = 0; column < cells.Length; column++)
        {
            var control = cells[column];
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            layout.Children.Add(control);
        }
    }

    public static StackPanel Options(params Control[] options)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var option in options) panel.Children.Add(option);
        return panel;
    }

    public static ToggleButton Option(string label, string description)
    {
        var button = new ToggleButton { Content = label, Classes = { "search-option" } };
        Name(button, description);
        return button;
    }

    public static Button Command(string label, Action action, bool primary = false, string? description = null)
    {
        var button = new Button { Content = label };
        StyleCommand(button, primary, description);
        button.Click += (_, _) => action();
        return button;
    }

    public static void StyleCommand(Button button, bool primary = false, string? description = null)
    {
        button.Classes.Add(primary ? "search-primary" : "search-command");
        if (description is not null) Name(button, description);
    }

    public static Button Icon(string label, string description, Action action)
    {
        var button = new Button { Content = label, Classes = { "focus-icon" } };
        Name(button, description);
        button.Click += (_, _) => action();
        return button;
    }

    public static void StyleInput(TextBox input)
    {
        input.Classes.Add("search-input");
        AutomationProperties.SetName(input, input.PlaceholderText ?? "Поиск");
    }

    public static TextBlock Status() => new()
    {
        Foreground = FocusTheme.Muted,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 12,
    };

    public static DockPanel Footer(TextBlock status, params Control[] actions)
    {
        var controls = Options(actions);
        DockPanel.SetDock(controls, Dock.Right);
        return new DockPanel { Children = { controls, status } };
    }

    public static Button ResultsToggle(Control results)
    {
        var button = new Button { Classes = { "focus-quiet" }, FontSize = 12 };
        button.Click += (_, _) =>
        {
            results.IsVisible = !results.IsVisible;
            SetLabel();
        };
        SetLabel();
        return button;

        void SetLabel()
        {
            button.Content = results.IsVisible ? "Список ▾" : "Список ▸";
            Name(button, results.IsVisible ? "Скрыть список результатов" : "Показать список результатов");
        }
    }

    private static void Name(Control control, string description)
    {
        AutomationProperties.SetName(control, description);
        ToolTip.SetTip(control, description);
    }
}
