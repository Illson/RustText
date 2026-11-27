using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RustText.Demo.Editor;

namespace RustText.Demo.Logs;

internal sealed partial class LogView
{
    private void UpdateMinimapViewport()
    {
        _minimap.SetSearchRange(_hiddenOffset, _fileStamp.Length);
        if (_levelFilterViewport?.IsVisible == true) return;
        if (_page is not { } page) return;
        var range = Math.Max(1, page.FileLength - _hiddenOffset);
        var pageHeight = _logScrollViewer is { Extent.Height: > 0 } scroll ? scroll.Extent.Height : 1;
        var scrollY = _logScrollViewer?.Offset.Y ?? 0;
        var viewportHeight = _logScrollViewer?.Viewport.Height ?? pageHeight;
        var pageBytes = Math.Max(0, page.NextOffset - page.StartOffset);
        var start = page.StartOffset - _hiddenOffset + pageBytes * Math.Clamp(scrollY / pageHeight, 0, 1);
        var end = page.StartOffset - _hiddenOffset + pageBytes * Math.Clamp((scrollY + viewportHeight) / pageHeight, 0, 1);
        _minimap.UpdateViewport(start / range, end / range);
    }

    private void UpdateMinimapSearch() =>
        _minimap.SetSearchMarkers(_searchResults, _selectedSearchHit, _searchPanel.IsVisible);

    internal sealed class LogMinimap : Control
    {
        private readonly Pen _viewportBorder = new(FocusTheme.Accent, 1);
        private double _start;
        private double _end;
        private bool _hasPage;
        private IReadOnlyList<LogSearchHit> _searchHits = [];
        private LogSearchHit? _selectedHit;
        private bool _showSearch;
        private long _searchStart;
        private long _searchLength;

        public void SetSearchMarkers(IReadOnlyList<LogSearchHit> hits, LogSearchHit? selected, bool visible)
        {
            _searchHits = hits;
            _selectedHit = selected;
            _showSearch = visible;
            InvalidateVisual();
        }

        public void SetSearchRange(long start, long end)
        {
            var length = end - start;
            if (_searchStart == start && _searchLength == length) return;
            _searchStart = start;
            _searchLength = length;
            if (_showSearch) InvalidateVisual();
        }

        public byte[] Widths
        {
            get;
            set
            {
                field = value;
                InvalidateVisual();
            }
        } = [];

        public LogMinimap()
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, "Щёлкните или перетащите для перехода по файлу");
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            FocusTheme.ThemeChanged += OnThemeChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            FocusTheme.ThemeChanged -= OnThemeChanged;
            base.OnDetachedFromVisualTree(e);
        }

        internal Rect ViewportMarker
        {
            get
            {
                var height = Bounds.Height;
                if (!_hasPage || height <= 0 || Bounds.Width < 4) return default;
                var markerHeight = Math.Clamp((_end - _start) * height, Math.Min(16, height), height);
                var y = Math.Clamp((_start + _end) / 2 * height - markerHeight / 2, 0, height - markerHeight);
                return new Rect(1.5, y + 0.5, Bounds.Width - 3, Math.Max(0, markerHeight - 1));
            }
        }

        public void UpdateViewport(double start, double end)
        {
            start = Math.Clamp(start, 0, 1);
            end = Math.Clamp(end, start, 1);
            if (_hasPage && _start == start && _end == end) return;
            _start = start;
            _end = end;
            _hasPage = true;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(FocusTheme.MinimapBackground, new Rect(Bounds.Size));
            context.FillRectangle(FocusTheme.Border, new Rect(0, 0, 1, Bounds.Height));
            var marker = ViewportMarker;
            if (marker.Height > 0) context.FillRectangle(FocusTheme.MinimapViewport, marker);

            var rowHeight = Bounds.Height / Math.Max(1, Widths.Length);
            for (var index = 0; index < Widths.Length; index++)
            {
                var width = Math.Max(3, (int)Widths[index]) / (double)LogDataReader.MinimapColumns * Math.Max(0, Bounds.Width - 12);
                context.FillRectangle(FocusTheme.LogMinimapInk, new Rect(6, (index + .5) * rowHeight, width, 1));
            }
            if (marker.Height > 0) context.DrawRectangle(null, _viewportBorder, marker);

            if (!_showSearch || _searchLength <= 0) return;
            var lastRow = -1d;
            foreach (var hit in _searchHits)
            {
                if (!IsInTrack(hit)) continue;
                var fraction = (hit.Offset - _searchStart) / (double)_searchLength;
                var row = Math.Floor(Bounds.Height * fraction);
                if (row == lastRow) continue;
                MinimapSearchMarkers.Draw(context, Bounds.Width, 0, Bounds.Height, fraction, current: false);
                lastRow = row;
            }
            if (_selectedHit is { } current && IsInTrack(current))
            {
                var fraction = (current.Offset - _searchStart) / (double)_searchLength;
                MinimapSearchMarkers.Draw(context, Bounds.Width, 0, Bounds.Height, fraction, current: true);
            }
        }

        private bool IsInTrack(LogSearchHit hit) => hit.MatchLength > 0
            && hit.Offset >= _searchStart && hit.Offset - _searchStart < _searchLength;

        private void OnThemeChanged() => InvalidateVisual();
    }
}
