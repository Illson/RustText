using Avalonia;
using Avalonia.Media;

namespace RustText.Demo.Editor;

internal static class MinimapSearchMarkers
{
    public static void Draw(DrawingContext context, double width, double top, double height, double fraction, bool current)
    {
        if (width <= 6 || height <= 0) return;
        var thickness = Math.Min(height, current ? 3 : 2);
        var y = Math.Clamp(Math.Round(height * fraction - thickness / 2), 0, height - thickness);
        var length = (width - 6) * (current ? 1 : 0.6);
        context.FillRectangle(current ? FocusTheme.CurrentMatchMarker : FocusTheme.MatchMarker,
            new Rect(width - 3 - length, top + y, length, thickness));
    }
}
