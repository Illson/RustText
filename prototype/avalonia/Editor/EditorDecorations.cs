using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class DocumentMinimap : Control
{
    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Color.Parse("#17212A"));
    private static readonly IBrush LineBrush = new ImmutableSolidColorBrush(Color.Parse("#516775"));
    private static readonly IBrush ViewBrush = new ImmutableSolidColorBrush(Color.Parse("#314D66"));
    private readonly TextEditor _editor;
    private bool _dragging;

    public DocumentMinimap(TextEditor editor)
    {
        _editor = editor;
        Width = 55;
        Cursor = new Cursor(StandardCursorType.Arrow);
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

        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        var count = document.LineCount;
        var rows = Math.Clamp((int)(Bounds.Height / 3), 1, 160);
        if (FindScrollViewer() is { } scroll && scroll.Extent.Height > 0)
        {
            var height = Math.Clamp(Bounds.Height * scroll.Viewport.Height / scroll.Extent.Height,
                Math.Min(10, Bounds.Height), Bounds.Height);
            var maximum = scroll.Extent.Height - scroll.Viewport.Height;
            var fraction = maximum > 0 ? Math.Clamp(scroll.Offset.Y / maximum, 0, 1) : 0;
            context.FillRectangle(ViewBrush, new Rect(0, fraction * (Bounds.Height - height), Bounds.Width, height));
        }
        for (var row = 0; row < rows; row++)
        {
            var line = document.GetLineByNumber(1 + (int)((long)row * count / rows));
            var width = Math.Min(Bounds.Width - 12, line.Length * .6);
            if (width > 0)
            {
                context.FillRectangle(LineBrush, new Rect(6, row * Bounds.Height / rows, width, 1));
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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (FindScrollViewer() is { } scroll)
        {
            ScrollTo(scroll, scroll.Offset.Y - e.Delta.Y * _editor.FontSize * 3);
            e.Handled = true;
        }
    }

    private void Jump(PointerEventArgs e)
    {
        if (Bounds.Height > 0 && FindScrollViewer() is { } scroll)
        {
            var fraction = Math.Clamp(e.GetPosition(this).Y / Bounds.Height, 0, 1);
            ScrollTo(scroll, fraction * Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            e.Handled = true;
        }
    }

    private ScrollViewer? FindScrollViewer() => _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private static void ScrollTo(ScrollViewer scroll, double y) => scroll.SetCurrentValue(
        ScrollViewer.OffsetProperty, new Vector(scroll.Offset.X, y));
}

internal sealed class SearchHighlights(TextEditor editor) : IBackgroundRenderer
{
    private static readonly IBrush Brush = new ImmutableSolidColorBrush(Color.Parse("#665528"));
    private static readonly IBrush CurrentBrush = new ImmutableSolidColorBrush(Color.Parse("#366187"));
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
            drawingContext.DrawGeometry(Brush, null, matches);
        }

        if (Current is { Length: > 0 } current && current.Offset + current.Length <= document.TextLength)
        {
            var active = new BackgroundGeometryBuilder();
            active.AddSegment(view, new TextSegment { StartOffset = current.Offset, Length = current.Length });
            if (active.CreateGeometry() is { } selected)
            {
                drawingContext.DrawGeometry(CurrentBrush, null, selected);
            }
        }
    }
}
