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
    internal static void ReplaceExisting(TextEditor editor)
    {
        var margins = editor.TextArea.LeftMargins;
        var existing = margins.OfType<LineNumberMargin>().FirstOrDefault();
        if (existing is null)
        {
            return;
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
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        var number = LineNumbers.Format(Document?.LineCount ?? 1);
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
            var number = LineNumbers.Format(line.FirstDocumentLine.LineNumber);
            var text = new FormattedText(number, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface, EmSize, foreground);
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop);
            drawingContext.DrawText(text, new Point(Bounds.Width - text.Width, y - textView.VerticalOffset));
        }
    }
}
