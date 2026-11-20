using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class GroupedLineNumberMargin : LineNumberMargin
{
    private IReadOnlyList<int>? _sourceLines;
    private readonly Dictionary<long, TextLayout> _labels = [];
    private Typeface _labelTypeface;
    private double _labelSize;
    private IBrush? _labelBrush;

    internal void SetSourceLineNumbers(IReadOnlyList<int> lineNumbers)
    {
        _sourceLines = lineNumbers;
        InvalidateMeasure();
        InvalidateVisual();
    }

    internal long? FirstLineNumber
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    } = 1;

    private long SourceNumber(int row) => _sourceLines is null ? (FirstLineNumber + row - 1 ?? 0) : _sourceLines[row - 1];

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
        var lastNumber = _sourceLines is { Count: > 0 } ? _sourceLines[^1] : (FirstLineNumber + (Document?.LineCount ?? 1) - 1 ?? 0);
        UpdateLabelStyle();
        return new Size(Math.Max(size.Width, GetLabel(lastNumber).Width), size.Height);
    }

    public override void Render(DrawingContext drawingContext)
    {
        if (TextView is not { VisualLinesValid: true } textView)
        {
            return;
        }

        UpdateLabelStyle();
        foreach (var line in textView.VisualLines)
        {
            var row = line.FirstDocumentLine.LineNumber;
            if (_sourceLines is not null && row > _sourceLines.Count) continue;
            var text = GetLabel(SourceNumber(row));
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop);
            text.Draw(drawingContext, new Point(Bounds.Width - text.Width, y - textView.VerticalOffset));
        }
    }

    private void UpdateLabelStyle()
    {
        var brush = GetValue(TextBlock.ForegroundProperty);
        if (_labelTypeface == Typeface && _labelSize == EmSize && ReferenceEquals(_labelBrush, brush)) return;
        ClearLabels();
        (_labelTypeface, _labelSize, _labelBrush) = (Typeface, EmSize, brush);
    }

    private TextLayout GetLabel(long number)
    {
        if (_labels.TryGetValue(number, out var label)) return label;
        // Keep scrolling through a large document from retaining its entire line-number index.
        if (_labels.Count >= 256) ClearLabels();
        label = new TextLayout(number == 0 ? "?" : LineNumbers.Format(number), _labelTypeface, _labelSize, _labelBrush);
        _labels.Add(number, label);
        return label;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearLabels();
        base.OnDetachedFromVisualTree(e);
    }

    private void ClearLabels()
    {
        foreach (var label in _labels.Values) label.Dispose();
        _labels.Clear();
    }
}
