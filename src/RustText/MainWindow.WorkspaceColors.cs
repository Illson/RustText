using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace RustText.Demo;

public partial class MainWindow
{
    private static readonly (string Name, string Hex)[] WorkspaceColors =
    [
        ("Синий", "#75A9FF"), ("Голубой", "#8DD9F5"), ("Бирюзовый", "#60D4CD"), ("Мятный", "#78DCB0"),
        ("Зелёный", "#8AD196"), ("Салатовый", "#B7D982"), ("Жёлтый", "#F2DE79"), ("Золотой", "#F1BF69"),
        ("Персиковый", "#F6AE82"), ("Красный", "#F18A83"), ("Розовый", "#E88EB9"), ("Сиреневый", "#BAA0F3"),
        ("Фиолетовый", "#A58BF0"), ("Серо-синий", "#8199B6"), ("Серый", "#A5ABB6"), ("Белый", "#EDF0F6")
    ];

    private static WrapPanel CreateWorkspaceColorPalette(ColorView editor)
    {
        var palette = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        var presets = new List<(Color Color, ToggleButton Button)>(WorkspaceColors.Length);
        foreach (var (name, hex) in WorkspaceColors)
        {
            var shade = Color.Parse(hex);
            var button = new ToggleButton
            {
                IsChecked = editor.Color == shade,
                Content = new Border { Width = 18, Height = 18, CornerRadius = new(9), Background = new SolidColorBrush(shade) }
            };
            button.Classes.Add("workspace-swatch");
            AutomationProperties.SetName(button, name);
            AutomationProperties.SetAutomationId(button, $"WorkspaceColor-{hex[1..]}");
            ToolTip.SetTip(button, name);
            button.Click += (_, _) =>
            {
                editor.Color = shade;
                editor.IsVisible = false;
                button.IsChecked = true;
            };
            presets.Add((shade, button));
            palette.Children.Add(button);
        }

        var custom = new Button { Content = "+", FontSize = 17 };
        custom.Classes.Add("workspace-swatch");
        AutomationProperties.SetName(custom, "Свой цвет");
        AutomationProperties.SetAutomationId(custom, "WorkspaceCustomColor");
        ToolTip.SetTip(custom, "Свой цвет");
        custom.Click += (_, _) => editor.IsVisible = !editor.IsVisible;
        palette.Children.Add(custom);
        editor.ColorChanged += (_, _) =>
        {
            foreach (var (shade, button) in presets)
                button.IsChecked = editor.Color == shade;
        };
        return palette;
    }

    private static string WorkspaceColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
