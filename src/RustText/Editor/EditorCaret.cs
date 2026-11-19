using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class EditorCaret(TextArea area) : IBackgroundRenderer
{
    private const double Width = 2;

    public KnownLayer Layer => KnownLayer.Caret;

    public static void Attach(TextArea area)
    {
        area.CaretBrush = FocusTheme.EditorCaret;
        area.TextView.BackgroundRenderers.Add(new EditorCaret(area));
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!area.IsKeyboardFocusWithin || area.OverstrikeMode
            || !textView.VisualLinesValid || textView.GetVisualLine(area.Caret.Line) is null)
            return;

        // Use AvaloniaEdit's glyph-aware caret geometry. The editor still owns navigation, IME and overstrike.
        var caret = area.Caret.CalculateCaretRectangle();
        var scale = TopLevel.GetTopLevel(textView)?.RenderScaling ?? 1;
        DrawLine(drawingContext, caret.Translate(-textView.ScrollOffset), FocusTheme.EditorCaret, scale);
    }

    internal static void DrawLine(DrawingContext drawingContext, Rect caret, IBrush brush, double scale)
    {
        var x = Math.Round(caret.X * scale) / scale;
        var y = Math.Round(caret.Y * scale) / scale;
        var bottom = Math.Round(caret.Bottom * scale) / scale;
        var width = Math.Ceiling(Width * scale) / scale;
        drawingContext.FillRectangle(brush, new Rect(x, y, width, Math.Max(0, bottom - y)));
    }
}
