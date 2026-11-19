using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace RustText.Demo;

internal enum AppTheme
{
    Focus,
    Neon,
    ViceCity
}

internal static class FocusTheme
{
    private sealed record Palette(
        string Chrome,
        string Deep,
        string Editor,
        string Panel,
        string Field,
        string Border,
        string Text,
        string Muted,
        string Gutter,
        string Accent,
        string AccentHover,
        string Hover,
        string Selection,
        string Match,
        string CurrentMatch,
        string Warning,
        string Danger,
        string DangerSurface,
        string Success,
        string Link,
        string UpdateSurface,
        string UpdateBorder,
        string UpdateIconSurface,
        string UpdateText,
        string JsonKey,
        string JsonString,
        string JsonNumber,
        string JsonLiteral);

    private sealed class ThemeState
    {
        public event Action? Changed;

        public SolidColorBrush Chrome { get; } = Brush(Focus.Chrome);
        public SolidColorBrush Deep { get; } = Brush(Focus.Deep);
        public SolidColorBrush Editor { get; } = Brush(Focus.Editor);
        public SolidColorBrush Panel { get; } = Brush(Focus.Panel);
        public SolidColorBrush Field { get; } = Brush(Focus.Field);
        public SolidColorBrush Border { get; } = Brush(Focus.Border);
        public SolidColorBrush Text { get; } = Brush(Focus.Text);
        public SolidColorBrush Muted { get; } = Brush(Focus.Muted);
        public SolidColorBrush Gutter { get; } = Brush(Focus.Gutter);
        public SolidColorBrush Accent { get; } = Brush(Focus.Accent);
        public SolidColorBrush AccentHover { get; } = Brush(Focus.AccentHover);
        public SolidColorBrush Hover { get; } = Brush(Focus.Hover);
        public SolidColorBrush Selection { get; } = Brush(Focus.Selection);
        public SolidColorBrush Match { get; } = Brush(Focus.Match);
        public SolidColorBrush CurrentMatch { get; } = Brush(Focus.CurrentMatch);
        public SolidColorBrush Warning { get; } = Brush(Focus.Warning);
        public SolidColorBrush Danger { get; } = Brush(Focus.Danger);
        public SolidColorBrush DangerSurface { get; } = Brush(Focus.DangerSurface);
        public SolidColorBrush Success { get; } = Brush(Focus.Success);
        public SolidColorBrush Link { get; } = Brush(Focus.Link);
        public SolidColorBrush UpdateSurface { get; } = Brush(Focus.UpdateSurface);
        public SolidColorBrush UpdateBorder { get; } = Brush(Focus.UpdateBorder);
        public SolidColorBrush UpdateIconSurface { get; } = Brush(Focus.UpdateIconSurface);
        public SolidColorBrush UpdateText { get; } = Brush(Focus.UpdateText);
        public SolidColorBrush JsonKey { get; } = Brush(Focus.JsonKey);
        public SolidColorBrush JsonString { get; } = Brush(Focus.JsonString);
        public SolidColorBrush JsonNumber { get; } = Brush(Focus.JsonNumber);
        public SolidColorBrush JsonLiteral { get; } = Brush(Focus.JsonLiteral);

        public void RaiseChanged() => Changed?.Invoke();

        public void Apply(Palette palette)
        {
            Set(Chrome, palette.Chrome);
            Set(Deep, palette.Deep);
            Set(Editor, palette.Editor);
            Set(Panel, palette.Panel);
            Set(Field, palette.Field);
            Set(Border, palette.Border);
            Set(Text, palette.Text);
            Set(Muted, palette.Muted);
            Set(Gutter, palette.Gutter);
            Set(Accent, palette.Accent);
            Set(AccentHover, palette.AccentHover);
            Set(Hover, palette.Hover);
            Set(Selection, palette.Selection);
            Set(Match, palette.Match);
            Set(CurrentMatch, palette.CurrentMatch);
            Set(Warning, palette.Warning);
            Set(Danger, palette.Danger);
            Set(DangerSurface, palette.DangerSurface);
            Set(Success, palette.Success);
            Set(Link, palette.Link);
            Set(UpdateSurface, palette.UpdateSurface);
            Set(UpdateBorder, palette.UpdateBorder);
            Set(UpdateIconSurface, palette.UpdateIconSurface);
            Set(UpdateText, palette.UpdateText);
            Set(JsonKey, palette.JsonKey);
            Set(JsonString, palette.JsonString);
            Set(JsonNumber, palette.JsonNumber);
            Set(JsonLiteral, palette.JsonLiteral);
        }
    }

    private static readonly IReadOnlyDictionary<AppTheme, Palette> Palettes = new Dictionary<AppTheme, Palette>
    {
        [AppTheme.Focus] = new(
            "#1D2227", "#181C21", "#303841", "#252C33", "#20262C", "#414B54",
            "#D8DEE9", "#A6B0BC", "#96A1AE", "#CCC6C6", "#DBD6D6", "#2C343C",
            "#444D58", "#69583A", "#7B6543", "#E5BD85", "#F0A09D", "#4A3033",
            "#B3CEB4", "#72C7E6", "#342E28", "#6B5742", "#47392C", "#F3E4D2",
            "#AEC9E1", "#BDD3AA", "#E5BD85", "#CDB9E1"),
        [AppTheme.Neon] = new(
            "#090B15", "#070912", "#101426", "#171B2D", "#0D1120", "#33405D",
            "#E9F6FF", "#A4B4D0", "#7888AA", "#22E6FF", "#75F2FF", "#202D49",
            "#284468", "#5A4A28", "#806127", "#FFD166", "#FF5C91", "#48233D",
            "#66F5C8", "#76CFFF", "#262047", "#5E4F91", "#37275D", "#F0E9FF",
            "#8CDFFF", "#82F2D2", "#FFD166", "#FF7DB8"),
        [AppTheme.ViceCity] = new(
            "#201724", "#17121D", "#2B1D2D", "#352239", "#211A29", "#69485F",
            "#F8E9E2", "#C7A9B8", "#A98EAA", "#FF7898", "#FF9BAE", "#4C2B48",
            "#573451", "#785044", "#A35F4B", "#FFC38A", "#FF6E79", "#56303B",
            "#72D6C4", "#78D9E8", "#4A263A", "#A64B6A", "#66324E", "#FFE6DD",
            "#88DDE8", "#A4E2C5", "#FFC38A", "#FFA1B8")
    };

    // Mutable Avalonia brushes belong to the dispatcher that created them.
    private static readonly ConditionalWeakTable<Dispatcher, ThemeState> States = new();
    private static ThemeState State => States.GetValue(Dispatcher.UIThread, _ => new ThemeState());
    private static Palette Focus => Palettes[AppTheme.Focus];

    public const double EditorFontSize = 12;

    public static event Action? ThemeChanged
    {
        add => State.Changed += value;
        remove => State.Changed -= value;
    }

    public static SolidColorBrush Chrome => State.Chrome;
    public static SolidColorBrush Deep => State.Deep;
    public static SolidColorBrush Editor => State.Editor;
    public static SolidColorBrush Panel => State.Panel;
    public static SolidColorBrush Field => State.Field;
    public static SolidColorBrush Border => State.Border;
    public static SolidColorBrush Text => State.Text;
    public static SolidColorBrush Muted => State.Muted;
    public static SolidColorBrush Gutter => State.Gutter;
    public static SolidColorBrush Accent => State.Accent;
    public static SolidColorBrush AccentHover => State.AccentHover;
    public static SolidColorBrush Hover => State.Hover;
    public static SolidColorBrush Selection => State.Selection;
    public static SolidColorBrush Match => State.Match;
    public static SolidColorBrush CurrentMatch => State.CurrentMatch;
    public static SolidColorBrush Warning => State.Warning;
    public static SolidColorBrush Danger => State.Danger;
    public static SolidColorBrush DangerSurface => State.DangerSurface;
    public static SolidColorBrush Success => State.Success;
    public static SolidColorBrush Link => State.Link;
    public static SolidColorBrush UpdateSurface => State.UpdateSurface;
    public static SolidColorBrush UpdateBorder => State.UpdateBorder;
    public static SolidColorBrush UpdateIconSurface => State.UpdateIconSurface;
    public static SolidColorBrush UpdateText => State.UpdateText;
    public static SolidColorBrush JsonKey => State.JsonKey;
    public static SolidColorBrush JsonString => State.JsonString;
    public static SolidColorBrush JsonNumber => State.JsonNumber;
    public static SolidColorBrush JsonLiteral => State.JsonLiteral;

    public static Color AccentColor => Accent.Color;
    public static Color ChromeColor => Chrome.Color;
    public static Color FieldColor => Field.Color;
    public static Color BorderColor => Border.Color;
    public static Color TextColor => Text.Color;
    public static Color MutedColor => Muted.Color;
    public static Color PanelColor => Panel.Color;
    public static Color HoverColor => Hover.Color;
    public static Color SelectionColor => Selection.Color;

    public static void Apply(AppTheme theme)
    {
        var palette = Palettes.GetValueOrDefault(theme, Focus);
        var state = State;
        state.Apply(palette);
        RefreshFluentPalette(palette);
        state.RaiseChanged();
    }

    private static void RefreshFluentPalette(Palette palette)
    {
        if (Avalonia.Application.Current is not { } application)
        {
            return;
        }

        foreach (var style in application.Styles)
        {
            if (style is not FluentTheme fluentTheme ||
                !fluentTheme.Palettes.TryGetValue(Avalonia.Styling.ThemeVariant.Dark, out var resources) ||
                resources is not ColorPaletteResources colors)
            {
                continue;
            }

            colors.RegionColor = Color.Parse(palette.Chrome);
            colors.AltHigh = Color.Parse(palette.Field);
            colors.BaseHigh = Color.Parse(palette.Text);
            colors.BaseMediumHigh = Color.Parse(palette.Muted);
            colors.BaseMedium = Color.Parse(palette.Muted);
            colors.BaseLow = Color.Parse(palette.Border);
            colors.ChromeLow = Color.Parse(palette.Chrome);
            colors.ChromeMediumLow = Color.Parse(palette.Panel);
            colors.ListLow = Color.Parse(palette.Hover);
            colors.ListMedium = Color.Parse(palette.Selection);
            // Changing Accent notifies Fluent controls after the rest of the palette is ready.
            colors.Accent = Color.Parse(palette.Accent);
        }
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private static void Set(SolidColorBrush brush, string hex) => brush.Color = Color.Parse(hex);
}
