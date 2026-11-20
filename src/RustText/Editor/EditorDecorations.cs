using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class DocumentMinimap : Control, IDisposable
{
    private const int Padding = 3;
    private const int PreviewColumns = 66;
    private const int RowPitch = 2;
    private const int MaxRows = 512;
    private readonly TextEditor _editor;
    private WriteableBitmap? _preview;
    private bool _previewDirty = true;
    private bool _dragging;
    private bool _disposed;

    public DocumentMinimap(TextEditor editor)
    {
        _editor = editor;
        Width = PreviewColumns + Padding * 2;
        Cursor = new Cursor(StandardCursorType.Arrow);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        ToolTip.SetTip(this, "Перейти к области документа");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_disposed) return;
        FocusTheme.ThemeChanged += OnThemeChanged;
        _editor.TextChanged += OnTextChanged;
        _editor.TextArea.TextView.VisualLinesChanged += OnVisualLinesChanged;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleasePreview();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        _disposed = true;
        ReleasePreview();
    }

    private void ReleasePreview()
    {
        FocusTheme.ThemeChanged -= OnThemeChanged;
        _editor.TextChanged -= OnTextChanged;
        _editor.TextArea.TextView.VisualLinesChanged -= OnVisualLinesChanged;
        _preview?.Dispose();
        _preview = null;
        _previewDirty = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_disposed || _editor.Document is not { } document) return;

        context.FillRectangle(FocusTheme.Editor, new Rect(Bounds.Size));
        var rows = GetRowCount(document.LineCount, Bounds.Height);
        if (rows == 0) return;

        var height = PreviewHeight;
        var navigationHeight = NavigationHeight;
        var viewport = _editor.ViewportHeight;
        var maximum = _editor.ExtentHeight - viewport;
        if (viewport > 0 && maximum > 0)
        {
            var markerHeight = Math.Clamp(navigationHeight * viewport / _editor.ExtentHeight,
                Math.Min(8, navigationHeight), navigationHeight);
            var fraction = Math.Clamp(_editor.VerticalOffset / maximum, 0, 1);
            var top = Padding + fraction * (navigationHeight - markerHeight);
            var marker = navigationHeight > height
                ? new Rect(Bounds.Width - 2, top, 2, markerHeight)
                : new Rect(0, top, Bounds.Width, markerHeight);
            context.FillRectangle(FocusTheme.MinimapViewport, marker);
        }

        if (_preview?.PixelSize.Height != rows * RowPitch)
        {
            _preview?.Dispose();
            _preview = new WriteableBitmap(new PixelSize(PreviewColumns, rows * RowPitch),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _previewDirty = true;
        }

        if (_previewDirty)
        {
            PaintPreview(_preview, document, rows);
            _previewDirty = false;
        }
        context.DrawImage(_preview, new Rect(Padding, Padding, PreviewColumns, height));
    }

    internal static int GetRowCount(int lineCount, double height) =>
        Math.Min(MaxRows, (int)(GetPreviewHeight(lineCount, height) / RowPitch));

    private static double GetPreviewHeight(int lineCount, double height) =>
        Math.Clamp(height - Padding * 2, 0, lineCount * (double)RowPitch);

    private unsafe void PaintPreview(WriteableBitmap preview, TextDocument document, int rows)
    {
        using var pixels = preview.Lock();
        new Span<byte>((void*)pixels.Address, pixels.RowBytes * pixels.Size.Height).Clear();

        var color = FocusTheme.DocumentMinimapInk.Color;
        var letter = PremultipliedPixel(color, 65);
        var punctuation = PremultipliedPixel(color, 40);
        var tabWidth = Math.Max(1, _editor.Options.IndentationSize);
        var lastLineIndex = document.LineCount - 1;
        for (var row = 0; row < rows; row++)
        {
            var index = rows == 1 ? 0 : (int)((long)row * lastLineIndex / (rows - 1));
            var line = document.GetLineByNumber(index + 1);
            var scanline = (uint*)(pixels.Address + row * RowPitch * pixels.RowBytes);
            var text = document.GetTextAsMemory(line.Offset, Math.Min(line.Length, PreviewColumns)).Span;
            var column = 0;
            foreach (var character in text)
            {
                if (column >= PreviewColumns) break;
                if (character == '\t')
                {
                    column += tabWidth - column % tabWidth;
                }
                else
                {
                    if (!char.IsWhiteSpace(character))
                        scanline[column] = char.IsLetterOrDigit(character) ? letter : punctuation;
                    column++;
                }
            }
        }
    }

    private static uint PremultipliedPixel(Color color, uint opacity)
    {
        var alpha = color.A * opacity / 100;
        return (alpha << 24) | (color.R * alpha / 255 << 16)
            | (color.G * alpha / 255 << 8) | (color.B * alpha / 255);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || point.Position.Y > Padding + NavigationHeight) return;

        _dragging = true;
        e.Pointer.Capture(this);
        Jump(e);
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

    private double PreviewHeight => GetPreviewHeight(_editor.Document?.LineCount ?? 0, Bounds.Height);
    private double NavigationHeight
    {
        get
        {
            var previewHeight = PreviewHeight;
            var availableHeight = Math.Max(0, Bounds.Height - Padding * 2);
            if (previewHeight >= availableHeight) return previewHeight;

            var view = _editor.TextArea.TextView;
            var lineHeight = Math.Max(1, view.DefaultLineHeight);
            var document = _editor.Document;
            var logicalHeight = (document?.LineCount ?? 0) * lineHeight;
            var documentHeight = view.DocumentHeight;
            var extraHeight = documentHeight - logicalHeight;
            var paddingAllowance = lineHeight / 2;
            var hasWrappedRows = _editor.WordWrap && (extraHeight > paddingAllowance || HasWrappedLines(view, document));
            return documentHeight > _editor.ViewportHeight && hasWrappedRows
                ? Math.Max(previewHeight, availableHeight)
                : previewHeight;
        }
    }

    private static bool HasWrappedLines(AvaloniaEdit.Rendering.TextView view, TextDocument? document)
    {
        if (document is null || document.LineCount > MaxRows || view.Bounds.Width <= 0 || view.WideSpaceWidth <= 0)
            return false;

        var likelyWrapLength = (int)(view.Bounds.Width / view.WideSpaceWidth);
        var longestCandidate = 0;
        foreach (var line in document.Lines)
        {
            longestCandidate = Math.Max(longestCandidate, line.Length);
        }
        if (longestCandidate <= likelyWrapLength) return false;
        if (longestCandidate > likelyWrapLength * 2) return true;

        var candidate = document.Lines.First(line => line.Length == longestCandidate);
        var first = view.GetVisualPosition(new TextViewPosition(candidate.LineNumber, 1), VisualYPosition.LineMiddle).Y;
        var last = view.GetVisualPosition(new TextViewPosition(candidate.LineNumber, candidate.Length + 1), VisualYPosition.LineMiddle).Y;
        return last - first > view.DefaultLineHeight / 2;
    }

    private void Jump(PointerEventArgs e)
    {
        var height = NavigationHeight;
        if (height > 0 && FindScrollViewer() is { } scroll)
        {
            var fraction = Math.Clamp((e.GetPosition(this).Y - Padding) / height, 0, 1);
            ScrollTo(scroll, fraction * Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            e.Handled = true;
        }
    }

    private ScrollViewer? FindScrollViewer() => _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void OnTextChanged(object? sender, EventArgs e) => InvalidatePreview();
    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
    private void OnThemeChanged() => InvalidatePreview();

    private void InvalidatePreview()
    {
        _previewDirty = true;
        InvalidateVisual();
    }

    private static void ScrollTo(ScrollViewer scroll, double y) => scroll.SetCurrentValue(
        ScrollViewer.OffsetProperty, new Vector(scroll.Offset.X, y));
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
