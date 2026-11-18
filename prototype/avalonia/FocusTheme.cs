using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace RustText.Demo;

internal static class FocusTheme
{
    public const double EditorFontSize = 12;

    public static Color AccentColor { get; } = Color.Parse("#CCC6C6");
    public static Color ChromeColor { get; } = Color.Parse("#1D2227");
    public static Color FieldColor { get; } = Color.Parse("#20262C");
    public static Color BorderColor { get; } = Color.Parse("#414B54");
    public static Color TextColor { get; } = Color.Parse("#D8DEE9");
    public static Color MutedColor { get; } = Color.Parse("#A6B0BC");

    public static IBrush Chrome { get; } = new ImmutableSolidColorBrush(ChromeColor);
    public static IBrush Deep { get; } = Brush("#181C21");
    public static IBrush Editor { get; } = Brush("#303841");
    public static IBrush Panel { get; } = Brush("#252C33");
    public static IBrush Field { get; } = new ImmutableSolidColorBrush(FieldColor);
    public static IBrush Border { get; } = new ImmutableSolidColorBrush(BorderColor);
    public static IBrush Text { get; } = new ImmutableSolidColorBrush(TextColor);
    public static IBrush Muted { get; } = new ImmutableSolidColorBrush(MutedColor);
    public static IBrush Gutter { get; } = Brush("#96A1AE");
    public static IBrush Accent { get; } = new ImmutableSolidColorBrush(AccentColor);
    public static IBrush AccentHover { get; } = Brush("#DBD6D6");
    public static IBrush Hover { get; } = Brush("#2C343C");
    public static IBrush Selection { get; } = Brush("#444D58");
    public static IBrush Match { get; } = Brush("#69583A");
    public static IBrush CurrentMatch { get; } = Brush("#7B6543");
    public static IBrush Warning { get; } = Brush("#E5BD85");
    public static IBrush Danger { get; } = Brush("#F0A09D");
    public static IBrush DangerSurface { get; } = Brush("#4A3033");
    public static IBrush Success { get; } = Brush("#B3CEB4");

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
