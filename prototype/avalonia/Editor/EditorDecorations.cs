using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class DocumentMinimap : Control
{
    private readonly TextEditor _editor;
    private bool _dragging;

    public DocumentMinimap(TextEditor editor)
    {
        _editor = editor;
        Width = 72;
        _editor.TextChanged += (_, _) => InvalidateVisual();
        _editor.TextArea.TextView.VisualLinesChanged += (_, _) => InvalidateVisual();
        ToolTip.SetTip(this, "Перейти к области документа");
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_editor.Document is not { } document || Bounds.Height < 2)
        {
            return;
        }

        context.FillRectangle(FocusTheme.Hover, new Rect(Bounds.Size));
        context.FillRectangle(FocusTheme.Border, new Rect(0, 0, 1, Bounds.Height));
        var count = document.LineCount;
        var rows = Math.Clamp((int)(Bounds.Height / 3), 1, 160);
        var view = _editor.TextArea.TextView;
        var first = view.VisualLinesValid ? view.VisualLines.FirstOrDefault()?.FirstDocumentLine.LineNumber : null;
        var current = Math.Clamp((first ?? _editor.TextArea.Caret.Line) - 1, 0, count - 1);
        var viewY = Bounds.Height * current / Math.Max(count, 1);
        context.FillRectangle(FocusTheme.Selection, new Rect(1, viewY, Bounds.Width - 1, Math.Min(18, Bounds.Height - viewY)));
        for (var row = 0; row < rows; row++)
        {
            var line = document.GetLineByNumber(1 + (int)((long)row * count / rows));
            var width = Math.Min(Bounds.Width - 12, line.Length * .6);
            if (width > 0)
            {
                context.FillRectangle(FocusTheme.Border, new Rect(7, row * Bounds.Height / rows, width, 1));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            e.Pointer.Capture(this);
            Jump(e);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Jump(e);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    private void Jump(PointerEventArgs e)
    {
        if (Bounds.Height > 0 && _editor.Document is { } document)
        {
            var fraction = Math.Clamp(e.GetPosition(this).Y / Bounds.Height, 0, 1);
            _editor.ScrollToLine(1 + (int)(fraction * (document.LineCount - 1)));
            e.Handled = true;
        }
    }
}

internal sealed class SearchHighlights(TextEditor editor) : IBackgroundRenderer
{
    public Regex? Pattern { get; set; }
    public bool OnlyCurrent { get; set; }
    public (int Offset, int Length)? Current { get; set; }
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView view, DrawingContext drawingContext)
    {
        if (!view.VisualLinesValid || editor.Document is not { } document)
        {
            return;
        }

        var all = new BackgroundGeometryBuilder();
        if (Pattern is { } regex && !OnlyCurrent)
        {
            try
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var count = 0;
                foreach (var visual in view.VisualLines)
                {
                    for (var number = visual.FirstDocumentLine.LineNumber; number <= visual.LastDocumentLine.LineNumber; number++)
                    {
                        var line = document.GetLineByNumber(number);
                        if (line.Length > 16 * 1024)
                        {
                            continue;
                        }

                        foreach (Match match in regex.Matches(document.GetText(line.Offset, line.Length)))
                        {
                            if (match.Length > 0)
                            {
                                all.AddSegment(view, new TextSegment { StartOffset = line.Offset + match.Index, Length = match.Length });
                            }

                            if (++count >= 5000 || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds > 25)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        if (all.CreateGeometry() is { } matches)
        {
            drawingContext.DrawGeometry(FocusTheme.Match, null, matches);
        }

        if (Current is { Length: > 0 } current && current.Offset + current.Length <= document.TextLength)
        {
            var active = new BackgroundGeometryBuilder();
            active.AddSegment(view, new TextSegment { StartOffset = current.Offset, Length = current.Length });
            if (active.CreateGeometry() is { } selected)
            {
            drawingContext.DrawGeometry(FocusTheme.CurrentMatch, null, selected);
            }
        }
    }
}
