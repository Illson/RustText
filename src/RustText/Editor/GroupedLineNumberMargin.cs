using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class GroupedLineNumberMargin : LineNumberMargin
{
    private IReadOnlyList<int>? _sourceLines;

    internal void SetSourceLineNumbers(IReadOnlyList<int> lineNumbers)
    {
        _sourceLines = lineNumbers;
        InvalidateMeasure();
        InvalidateVisual();
    }

    internal static GroupedLineNumberMargin? ReplaceExisting(TextEditor editor)
    {
        var margins = editor.TextArea.LeftMargins;
        var existing = margins.OfType<LineNumberMargin>().FirstOrDefault();
        if (existing is null)
        {
            return null;
        }

        var index = margins.IndexOf(existing);
        var replacement = new GroupedLineNumberMargin
        {
            Margin = existing.Margin,
            MinWidthInDigits = existing.MinWidthInDigits,
        };
        replacement.SetValue(TextBlock.FontFamilyProperty, editor.FontFamily);
        replacement.SetValue(TextBlock.FontSizeProperty, editor.FontSize);
        replacement.SetValue(TextBlock.ForegroundProperty, editor.LineNumbersForeground);

        margins.Remove(existing);
        margins.Insert(index, replacement);
        return replacement;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        var lastNumber = _sourceLines is { Count: > 0 } ? _sourceLines[^1] : Document?.LineCount ?? 1;
        var number = LineNumbers.Format(lastNumber);
        var text = new FormattedText(number, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface, EmSize, GetValue(TextBlock.ForegroundProperty));
        return new Size(Math.Max(size.Width, text.Width), size.Height);
    }

    public override void Render(DrawingContext drawingContext)
    {
        if (TextView is not { VisualLinesValid: true } textView)
        {
            return;
        }

        var foreground = GetValue(TextBlock.ForegroundProperty);
        foreach (var line in textView.VisualLines)
        {
            var row = line.FirstDocumentLine.LineNumber;
            if (_sourceLines is not null && row > _sourceLines.Count) continue;
            var number = LineNumbers.Format(_sourceLines is null ? row : _sourceLines[row - 1]);
            var text = new FormattedText(number, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface, EmSize, foreground);
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop);
            drawingContext.DrawText(text, new Point(Bounds.Width - text.Width, y - textView.VerticalOffset));
        }
    }
}
