using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace RustText.Demo.Logs;

internal sealed partial class LogView
{
    private void UpdateMinimapViewport()
    {
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

    private sealed class LogMinimap : Control
    {
        private readonly Pen _viewportBorder = new(FocusTheme.Accent, 1);
        private double _start;
        private double _end;
        private bool _hasPage;

        public IReadOnlyList<string> Samples { get; set; } = [];

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
            _start = Math.Clamp(start, 0, 1);
            _end = Math.Clamp(end, _start, 1);
            _hasPage = true;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(FocusTheme.Hover, new Rect(Bounds.Size));
            context.FillRectangle(FocusTheme.Border, new Rect(0, 0, 1, Bounds.Height));
            var marker = ViewportMarker;
            if (marker.Height > 0) context.FillRectangle(FocusTheme.Selection, marker);

            var rowHeight = Bounds.Height / Math.Max(1, Samples.Count);
            for (var index = 0; index < Samples.Count; index++)
            {
                var width = Math.Clamp(Samples[index].Length, 3, 32) / 32d * Math.Max(0, Bounds.Width - 12);
                context.FillRectangle(FocusTheme.Muted, new Rect(6, (index + .5) * rowHeight, width, 1));
            }
            if (marker.Height > 0) context.DrawRectangle(null, _viewportBorder, marker);
        }

        private void OnThemeChanged() => InvalidateVisual();
    }
}
