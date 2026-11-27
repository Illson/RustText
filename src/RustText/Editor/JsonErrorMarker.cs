using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class JsonErrorMarker(TextView view) : IBackgroundRenderer
{
    private const double MinimumWidth = 5;

    internal int? Offset { get; private set; }
    public KnownLayer Layer => KnownLayer.Selection;

    public void SetOffset(int? offset)
    {
        if (Offset == offset) return;
        Offset = offset;
        view.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Offset is not { } requestedOffset || !textView.VisualLinesValid
            || textView.Document is not { } document)
        {
            return;
        }

        var offset = Math.Clamp(requestedOffset, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);
        var length = offset < line.EndOffset ? 1 : 0;
        if (offset + 1 < line.EndOffset
            && char.IsSurrogatePair(document.GetCharAt(offset), document.GetCharAt(offset + 1)))
        {
            length = 2;
        }

        var folding = JsonFolding.FindCollapsed(textView.GetService(typeof(FoldingManager)) as FoldingManager, offset);
        var segment = folding ?? new TextSegment { StartOffset = offset, Length = length };
        var pen = new Pen(FocusTheme.Danger, 1.25, lineCap: PenLineCap.Round);
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
        {
            DrawUnderline(drawingContext, pen, rect);
        }
    }

    private static void DrawUnderline(DrawingContext drawingContext, IPen pen, Rect rect)
    {
        const double waveHeight = 1.5;
        const double waveWidth = 3;
        var point = rect.Left;
        var end = point + Math.Max(MinimumWidth, rect.Width);
        var baseline = rect.Bottom - waveHeight - 1;
        var direction = 1;

        while (point < end)
        {
            var next = Math.Min(point + waveWidth, end);
            var raised = baseline + waveHeight * direction;
            drawingContext.DrawLine(pen, new Point(point, baseline), new Point(next, raised));
            point = next;
            baseline = raised;
            direction = -direction;
        }
    }
}
