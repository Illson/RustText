using System.Runtime.CompilerServices;
using Avalonia;
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
        string JsonLiteral)
    {
        public string Toolbar { get; init; } = Muted;
        public string AppIconForeground { get; init; } = Warning;
        public string AppIconBackground { get; init; } = UpdateIconSurface;
        public string AppIconBorder { get; init; } = UpdateBorder;
        public string StatusBorder { get; init; } = Border;
        public string ActionBorder { get; init; } = Border;
        public string MinimapBackground { get; init; } = Hover;
        public string MinimapViewport { get; init; } = Selection;
        public string DocumentMinimapInk { get; init; } = Border;
        public string LogMinimapInk { get; init; } = Muted;
        public string TitleMotif { get; init; } = "#00000000";
        public string TitleGlow { get; init; } = Chrome;
    }

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
        public SolidColorBrush Toolbar { get; } = Brush(Focus.Toolbar);
        public SolidColorBrush AppIconForeground { get; } = Brush(Focus.AppIconForeground);
        public SolidColorBrush AppIconBackground { get; } = Brush(Focus.AppIconBackground);
        public SolidColorBrush AppIconBorder { get; } = Brush(Focus.AppIconBorder);
        public SolidColorBrush StatusBorder { get; } = Brush(Focus.StatusBorder);
        public SolidColorBrush ActionBorder { get; } = Brush(Focus.ActionBorder);
        public SolidColorBrush MinimapBackground { get; } = Brush(Focus.MinimapBackground);
        public SolidColorBrush MinimapViewport { get; } = Brush(Focus.MinimapViewport);
        public SolidColorBrush DocumentMinimapInk { get; } = Brush(Focus.DocumentMinimapInk);
        public SolidColorBrush LogMinimapInk { get; } = Brush(Focus.LogMinimapInk);
        public SolidColorBrush TitleMotif { get; } = Brush(Focus.TitleMotif);
        public LinearGradientBrush TitleBar { get; } = new()
        {
            StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new(Color.Parse(Focus.Chrome), 0),
                new(Color.Parse(Focus.Chrome), .42),
                new(Color.Parse(Focus.TitleGlow), .80),
                new(Color.Parse(Focus.Chrome), 1)
            }
        };

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
            Set(Toolbar, palette.Toolbar);
            Set(AppIconForeground, palette.AppIconForeground);
            Set(AppIconBackground, palette.AppIconBackground);
            Set(AppIconBorder, palette.AppIconBorder);
            Set(StatusBorder, palette.StatusBorder);
            Set(ActionBorder, palette.ActionBorder);
            Set(MinimapBackground, palette.MinimapBackground);
            Set(MinimapViewport, palette.MinimapViewport);
            Set(DocumentMinimapInk, palette.DocumentMinimapInk);
            Set(LogMinimapInk, palette.LogMinimapInk);
            Set(TitleMotif, palette.TitleMotif);
            // Keep the brush and stops bound by XAML when switching themes.
            var chrome = Color.Parse(palette.Chrome);
            TitleBar.GradientStops[0].Color = chrome;
            TitleBar.GradientStops[1].Color = chrome;
            TitleBar.GradientStops[2].Color = Color.Parse(palette.TitleGlow);
            TitleBar.GradientStops[3].Color = chrome;
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
            "#15121E", "#120F1A", "#191524", "#221B30", "#201A2D", "#4A3659",
            "#E8DEF3", "#B9AACA", "#9886AE", "#FF62AD", "#FF91C5", "#2D233D",
            "#493052", "#554334", "#76503C", "#FFD08F", "#FF829C", "#3B1E32",
            "#89E0CD", "#69E0D6", "#2F213C", "#825478", "#382541", "#F5DFEC",
            "#FF83BC", "#89E0CD", "#FFD08F", "#B6A4F4")
        {
            Toolbar = "#69E0D6",
            AppIconForeground = "#69E0D6",
            AppIconBackground = "#221B30",
            AppIconBorder = "#FF62AD",
            StatusBorder = "#69E0D6",
            ActionBorder = "#69E0D6",
            MinimapBackground = "#171321",
            MinimapViewport = "#70493052",
            DocumentMinimapInk = "#927AA8",
            LogMinimapInk = "#927AA8",
            TitleMotif = "#191321",
            TitleGlow = "#4F2349"
        }
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
    public static SolidColorBrush Toolbar => State.Toolbar;
    public static SolidColorBrush AppIconForeground => State.AppIconForeground;
    public static SolidColorBrush AppIconBackground => State.AppIconBackground;
    public static SolidColorBrush AppIconBorder => State.AppIconBorder;
    public static SolidColorBrush StatusBorder => State.StatusBorder;
    public static SolidColorBrush ActionBorder => State.ActionBorder;
    public static SolidColorBrush MinimapBackground => State.MinimapBackground;
    public static SolidColorBrush MinimapViewport => State.MinimapViewport;
    public static SolidColorBrush DocumentMinimapInk => State.DocumentMinimapInk;
    public static SolidColorBrush LogMinimapInk => State.LogMinimapInk;
    public static SolidColorBrush TitleMotif => State.TitleMotif;
    public static LinearGradientBrush TitleBar => State.TitleBar;

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
