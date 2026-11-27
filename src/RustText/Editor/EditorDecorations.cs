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
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed class DocumentMinimap : Control, IDisposable
{
    private const int Padding = 3;
    private const int PreviewColumns = 66;
    private const int RowPitch = 2;
    private const int MaxRows = 512;
    private const int MinMarkerHeight = 8;
    private readonly TextEditor _editor;
    private readonly Pen _viewportBorder = new(FocusTheme.Accent, 1);
    private WriteableBitmap? _preview;
    private (double Height, double Width, double LineHeight) _previewLayout;
    private bool _previewDirty = true;
    private bool _dragging;
    private bool _disposed;
    private IReadOnlyList<int> _searchOffsets = [];
    private bool _showAllMatches;
    private int? _currentMatchOffset;

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
        _editor.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
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
        _editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
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
        var (lineCount, layoutHeight) = GetPreviewRows(document);
        var rows = GetRowCount(lineCount, Bounds.Height);
        if (rows == 0) return;

        var height = GetPreviewHeight(lineCount, Bounds.Height);
        var view = _editor.TextArea.TextView;
        var scrollEnd = view.ScrollOffset.Y > 0 ? view.ScrollOffset.Y + view.Bounds.Height : 0;
        var trackHeight = Math.Max(layoutHeight, scrollEnd);
        var marker = ViewportMarker(document, view, height, trackHeight);
        if (marker.Height > 0)
            context.FillRectangle(FocusTheme.MinimapViewport, marker);

        if (_preview?.PixelSize.Height != rows * RowPitch)
        {
            _preview?.Dispose();
            _preview = new WriteableBitmap(new PixelSize(PreviewColumns, rows * RowPitch),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _previewDirty = true;
        }

        var layout = layoutHeight > 0 ? (layoutHeight, view.Bounds.Width, view.DefaultLineHeight) : default;
        if (_previewDirty || _previewLayout != layout)
        {
            PaintPreview(_preview, document, rows, lineCount, layoutHeight);
            _previewLayout = layout;
            _previewDirty = false;
        }
        // Leave room for the visible empty area below the end of the document.
        var previewHeight = layoutHeight > 0 ? height * layoutHeight / trackHeight : height;
        context.DrawImage(_preview, new Rect(Padding, Padding, PreviewColumns, previewHeight));
        if (marker.Height > 1 && marker.Width > 1)
            context.DrawRectangle(null, _viewportBorder, marker.Deflate(.5));

        var folding = view.GetService(typeof(FoldingManager)) as FoldingManager;
        if (_showAllMatches)
        {
            foreach (var offset in _searchOffsets)
                DrawSearchMarker(offset, current: false);
        }
        if (_currentMatchOffset is { } current)
            DrawSearchMarker(current, current: true);

        void DrawSearchMarker(int offset, bool current)
        {
            if (offset >= document.TextLength) return;
            offset = JsonFolding.FindCollapsed(folding, offset)?.StartOffset ?? offset;
            var line = document.GetLineByOffset(offset);
            var fraction = (line.LineNumber - 0.5) / lineCount;
            if (layoutHeight > 0)
            {
                var top = view.GetVisualTopByDocumentLine(line.LineNumber);
                var bottom = line.NextLine is { } next ? view.GetVisualTopByDocumentLine(next.LineNumber) : layoutHeight;
                if (bottom <= top) return;
                var rowCount = Math.Max(1, (int)Math.Round((bottom - top) / view.DefaultLineHeight));
                if (rowCount > 1 && view.VisualLinesValid && view.GetVisualLine(line.LineNumber) is not null)
                {
                    var position = new TextViewPosition(document.GetLocation(offset));
                    fraction = view.GetVisualPosition(position, VisualYPosition.LineMiddle).Y / trackHeight;
                }
                else
                {
                    var row = (int)((long)(offset - line.Offset) * rowCount / Math.Max(1, line.Length));
                    var position = (Math.Min(row, rowCount - 1) + 0.5) / rowCount;
                    fraction = (top + (bottom - top) * position) / trackHeight;
                }
            }
            MinimapSearchMarkers.Draw(context, Bounds.Width, Padding, height, fraction, current);
        }
    }

    public void SetSearchOffsets(IReadOnlyList<int> offsets)
    {
        _searchOffsets = offsets;
        InvalidateVisual();
    }

    public void ShowSearch(bool hasPattern, bool onlyCurrent, (int Offset, int Length)? current)
    {
        if (!hasPattern) _searchOffsets = [];
        _showAllMatches = hasPattern && !onlyCurrent;
        _currentMatchOffset = current is { Length: > 0 } match ? match.Offset : null;
        InvalidateVisual();
    }

    private Rect ViewportMarker(TextDocument document, TextView view, double track, double trackHeight)
    {
        if (document.TextLength == 0 || view.Bounds.Height <= 0 || trackHeight <= 0) return default;

        var minimum = Math.Min(MinMarkerHeight, Math.Max(0, Bounds.Height - Padding * 2));
        track = Math.Max(track, minimum);
        var scale = track / trackHeight;
        var start = Math.Clamp(view.ScrollOffset.Y * scale, 0, track);
        var end = Math.Clamp((view.ScrollOffset.Y + view.Bounds.Height) * scale, start, track);
        var markerHeight = Math.Max(minimum, end - start);
        var y = Math.Clamp((start + end - markerHeight) / 2, 0, track - markerHeight);
        return new Rect(0, Padding + y, Bounds.Width, markerHeight);
    }

    internal static int GetRowCount(int lineCount, double height) =>
        Math.Min(MaxRows, (int)(GetPreviewHeight(lineCount, height) / RowPitch));

    private static double GetPreviewHeight(int lineCount, double height) =>
        Math.Clamp(height - Padding * 2, 0, lineCount * (double)RowPitch);

    private unsafe void PaintPreview(WriteableBitmap preview, TextDocument document, int rows, int lineCount, double layoutHeight)
    {
        using var pixels = preview.Lock();
        new Span<byte>((void*)pixels.Address, pixels.RowBytes * pixels.Size.Height).Clear();

        var color = FocusTheme.DocumentMinimapInk.Color;
        var letter = PremultipliedPixel(color, 65);
        var punctuation = PremultipliedPixel(color, 40);
        var tabWidth = Math.Max(1, _editor.Options.IndentationSize);
        var view = _editor.TextArea.TextView;
        var lineHeight = view.DefaultLineHeight;
        var sampleHeight = layoutHeight / lineCount;
        for (var row = 0; row < rows; row++)
        {
            var index = rows == 1 ? 0 : (int)((long)row * (lineCount - 1) / (rows - 1));
            var y = (index + .5) * sampleHeight;
            var line = layoutHeight > 0 ? view.GetDocumentLineByVisualTop(y) : document.GetLineByNumber(index + 1);
            var start = 0;
            var length = line.Length;
            if (layoutHeight > 0 && length > 0)
            {
                var top = view.GetVisualTopByDocumentLine(line.LineNumber);
                var bottom = line.NextLine is { } next ? view.GetVisualTopByDocumentLine(next.LineNumber) : layoutHeight;
                var wrappedRows = (int)Math.Round((bottom - top) / lineHeight);
                if (wrappedRows > 1)
                {
                    // shortcut: sample wrapped text without formatting off-screen lines.
                    var part = Math.Clamp((int)((y - top) / (bottom - top) * wrappedRows), 0, wrappedRows - 1);
                    start = (int)((long)part * length / wrappedRows);
                    length = (int)((long)(part + 1) * length / wrappedRows) - start;
                }
            }
            var scanline = (uint*)(pixels.Address + row * RowPitch * pixels.RowBytes);
            var text = document.GetTextAsMemory(line.Offset + start, Math.Min(length, PreviewColumns)).Span;
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
        if (!point.Properties.IsLeftButtonPressed || point.Position.Y > Padding + PreviewHeight) return;

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

    internal double PreviewHeight => _editor.Document is { } document
        ? GetPreviewHeight(GetPreviewRows(document).Count, Bounds.Height) : 0;

    private (int Count, double LayoutHeight) GetPreviewRows(TextDocument document)
    {
        var view = _editor.TextArea.TextView;
        if (view.DocumentHeight <= 0) return (document.LineCount, 0);
        var rows = Math.Max(1, (int)Math.Round(view.DocumentHeight / view.DefaultLineHeight));
        return (rows, view.DocumentHeight);
    }

    private void Jump(PointerEventArgs e)
    {
        var height = PreviewHeight;
        if (height > 0 && FindScrollViewer() is { } scroll)
        {
            var fraction = Math.Clamp((e.GetPosition(this).Y - Padding) / height, 0, 1);
            ScrollTo(scroll, fraction * Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            e.Handled = true;
        }
    }

    private ScrollViewer? FindScrollViewer() => _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void OnTextChanged(object? sender, EventArgs e)
    {
        _searchOffsets = [];
        _currentMatchOffset = null;
        InvalidatePreview();
    }
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y != 0 || e.ViewportDelta != default || e.ExtentDelta != default)
            InvalidateVisual();
    }
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

internal sealed class SearchHighlights : IBackgroundRenderer
{
    private readonly TextEditor _editor;

    public SearchHighlights(TextEditor editor)
    {
        _editor = editor;
        editor.TextArea.SelectionChanged += (_, _) => UpdateCurrentSelection();
    }

    public Regex? Pattern { get; set; }
    public bool OnlyCurrent { get; set; }
    public (int Offset, int Length)? Current
    {
        get;
        set
        {
            field = value;
            UpdateCurrentSelection();
        }
    }
    public KnownLayer Layer => KnownLayer.Selection;

    private void UpdateCurrentSelection()
    {
        _editor.TextArea.Classes.Set("search-current", Current is { Length: > 0 } current
            && _editor.SelectionStart == current.Offset && _editor.SelectionLength == current.Length);
    }

    public void Draw(TextView view, DrawingContext drawingContext)
    {
        if (!view.VisualLinesValid || _editor.Document is not { } document)
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
                foreach (var line in VisibleDocumentLines(view, document))
                {
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

                    if (count >= 5000 || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds > 25)
                    {
                        break;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        if (all.CreateGeometry() is { } matches)
        {
            drawingContext.DrawGeometry(FocusTheme.Match, FocusTheme.MatchOutline, matches);
        }

        if (Current is { Length: > 0 } current && current.Offset + current.Length <= document.TextLength)
        {
            var active = new BackgroundGeometryBuilder();
            active.AddSegment(view, new TextSegment { StartOffset = current.Offset, Length = current.Length });
            if (active.CreateGeometry() is { } selected)
            {
                drawingContext.DrawGeometry(FocusTheme.CurrentMatch, FocusTheme.CurrentMatchOutline, selected);
            }
        }
    }

    private static IEnumerable<DocumentLine> VisibleDocumentLines(TextView view, TextDocument document)
    {
        foreach (var visual in view.VisualLines)
        {
            var first = visual.FirstDocumentLine;
            var line = first;
            yield return line;
            if (first == visual.LastDocumentLine) continue;

            // A folded visual line can span many hidden document lines.
            foreach (var element in visual.Elements)
            {
                var offset = first.Offset + element.RelativeTextOffset;
                if (offset < line.Offset + line.TotalLength) continue;
                line = document.GetLineByOffset(offset);
                yield return line;
            }
        }
    }
}
