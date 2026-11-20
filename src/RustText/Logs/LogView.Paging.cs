using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RustText.Demo.Logs;

internal sealed partial class LogView
{
    private LogPage? _draggedPage;

    private void OnLogPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || e.KeyModifiers != KeyModifiers.None || Math.Abs(e.Delta.Y) < Math.Abs(e.Delta.X)
            || !IsLogTextSource(e.Source as Avalonia.Visual)) return;

        var forward = e.Delta.Y < 0;
        if (PageAtScrollBoundary(forward) is { } page)
        {
            e.Handled = true;
            _ = ContinueScrollingAsync(page, forward);
        }
    }

    private void OnLogScrollbarScrolled(object? sender, ScrollEventArgs e)
    {
        if (_closed || _busy || PageNavigationBlocked || _page is not { } page) return;
        if (e.ScrollEventType == ScrollEventType.ThumbTrack)
        {
            _draggedPage = page;
            return;
        }
        var endedDrag = e.ScrollEventType == ScrollEventType.EndScroll;
        if (endedDrag && !ReferenceEquals(_draggedPage, page)) return;
        _draggedPage = null;
        var forward = e.ScrollEventType is ScrollEventType.SmallIncrement or ScrollEventType.LargeIncrement;
        var backward = e.ScrollEventType is ScrollEventType.SmallDecrement or ScrollEventType.LargeDecrement;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_page, page)) return;
            if ((forward || endedDrag) && PageAtScrollBoundary(forward: true) is not null)
                _ = ContinueScrollingAsync(page, forward: true);
            else if ((backward || endedDrag) && PageAtScrollBoundary(forward: false) is not null)
                _ = ContinueScrollingAsync(page, forward: false);
        }, DispatcherPriority.Background);
    }

    private LogPage? PageAtScrollBoundary(bool forward)
    {
        if (_closed || _busy || PageNavigationBlocked || _page is not { } page || _logScrollViewer is not { } scroll)
            return null;

        var edge = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var canContinue = forward ? page.HasMore && scroll.Offset.Y >= edge - 1
            : page.StartOffset > _hiddenOffset && scroll.Offset.Y <= 1;
        return canContinue ? page : null;
    }

    private bool IsLogTextSource(Avalonia.Visual? visual)
    {
        for (; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, _logText)) return true;
        }
        return false;
    }

    private async Task ContinueScrollingAsync(LogPage page, bool forward)
    {
        try
        {
            if (forward) await NavigateAsync(page.NextOffset, saveHistory: false);
            else await NavigatePreviousAsync(scrollToEnd: true, adjacent: true);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (ObjectDisposedException) when (_closed) { }
        catch (IOException error) { if (!_closed) SetStatus($"Не удалось продолжить прокрутку. {error.Message}"); }
    }
}
