using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RustText.Demo.Logs;

internal sealed partial class LogView
{
    private LogPage? _draggedPage;

    private readonly record struct TextScrollAnchor(int First, int Last, double Offset, double ViewportY, double Step);
    private sealed record ScrollAnchor(LogPage Page, bool Forward, TextScrollAnchor Text, int KeepIndex, int KeepTextIndex);

    private void OnLogPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || e.KeyModifiers != KeyModifiers.None || Math.Abs(e.Delta.Y) < Math.Abs(e.Delta.X)) return;
        if (_levelFilterViewport?.IsVisible == true)
        {
            if (e.Source is Visual source && (ReferenceEquals(_filteredLogText, source) || _filteredLogText.IsVisualAncestorOf(source))
                && TryContinueFilteredScroll(e.Delta.Y)) e.Handled = true;
            return;
        }
        if (!IsLogTextSource(e.Source as Visual)) return;

        StopFollowingEnd();
        var forward = e.Delta.Y < 0;
        if (PageAtScrollBoundary(forward) is { } page)
        {
            e.Handled = true;
            _ = ContinueScrollingAsync(page, forward, Math.Abs(e.Delta.Y));
        }
    }

    private void OnLogScrollbarScrolled(object? sender, ScrollEventArgs e)
    {
        if (e.ScrollEventType != ScrollEventType.EndScroll) StopFollowingEnd();
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

    private bool IsLogTextSource(Visual? visual)
    {
        for (; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, _logText) || ReferenceEquals(visual, _pageEditor)) return true;
        }
        return false;
    }

    private async Task ContinueScrollingAsync(LogPage page, bool forward, double steps = 1)
    {
        try
        {
            if (_pageEdit is null && GetScrollAnchor(page, forward, steps) is { } anchor)
                await LoadPageAsync(page.StartOffset, saveHistory: false, scrollAnchor: anchor);
            else if (forward) await NavigateAsync(page.NextOffset, saveHistory: false);
            else await NavigatePreviousAsync(scrollToEnd: true, adjacent: true);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (ObjectDisposedException) when (_closed) { }
        catch (IOException error) { if (!_closed) SetStatus($"Не удалось продолжить прокрутку. {error.Message}"); }
    }

    private ScrollAnchor? GetScrollAnchor(LogPage page, bool forward, double steps)
    {
        if (page.Rows.Count == 0 || GetTextScrollAnchor(_logText, _logScrollViewer, forward ? steps : -steps) is not { } anchor)
            return null;

        // shortcut: bound the overlap even when a viewport is filled by a very long line.
        const int overlapBytes = 2 * LogDataReader.DefaultPageBytes;
        const int overlapRows = LogDataReader.MaximumPageSize - LogDataReader.DefaultPageSize;
        var (index, textIndex) = FindScrollOverlap(page.Rows, anchor, forward, row => row.Text.Length, row => row.StartsWithinLine,
            (i, _) => forward
                ? page.Rows.Count - i <= overlapRows && page.NextOffset - page.Rows[i].StartOffset <= overlapBytes
                : i < overlapRows && page.Rows[i].EndOffset - page.StartOffset <= overlapBytes);
        return new(page, forward, anchor, index, textIndex);
    }

    private static TextScrollAnchor? GetTextScrollAnchor(TextBox text, ScrollViewer? scroll, double steps)
    {
        if (scroll is not { Presenter: { } viewport, Viewport.Height: > 0 }
            || text.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter
            || presenter.TranslatePoint(default, viewport) is not { } origin) return null;
        var y = origin.Y;
        var first = -1;
        var last = 0;
        var firstY = 0d;
        foreach (var line in presenter.TextLayout.TextLines)
        {
            if (y >= scroll.Viewport.Height) break;
            if (y + line.Height > 0)
            {
                last = line.FirstTextSourceIndex;
                if (first < 0)
                {
                    first = last;
                    firstY = y;
                }
            }
            y += line.Height;
        }
        return first < 0 ? null : new(first, last, scroll.Offset.Y, firstY,
            Math.Max(1, scroll.SmallChange.Height) * Math.Clamp(steps, -1, 1));
    }

    private static (int Index, int TextIndex) FindScrollOverlap<T>(IReadOnlyList<T> rows, TextScrollAnchor anchor,
        bool forward, Func<T, int> length, Func<T, bool> continuation, Func<int, int, bool> fits)
    {
        var keep = 0;
        var keepText = 0;
        var textIndex = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var end = textIndex + length(rows[i - 1]);
            textIndex = end + (continuation(rows[i]) ? 0 : Environment.NewLine.Length);
            if (forward ? textIndex > anchor.First && fits(i - 1, keepText)
                : textIndex > anchor.Last || !fits(i, textIndex + length(rows[i]))) break;
            keep = i;
            keepText = textIndex;
        }
        return (forward || rows.Count == 0 ? keep : keep + 1, keepText);
    }

    private static (LogPage Page, int TextIndex) ReadScrolledPage(LogDataReader reader, ScrollAnchor anchor,
        long firstOffset, CancellationToken token)
    {
        var old = anchor.Page;
        if (anchor.Forward)
        {
            var next = reader.ReadPage(old.NextOffset, cancellationToken: token);
            if (next.Rows.Count == 0 || next.StartOffset != old.NextOffset) return (old, anchor.Text.First);
            var kept = old.Rows.Skip(anchor.KeepIndex);
            var start = anchor.KeepIndex == 0 ? old.StartOffset : old.Rows[anchor.KeepIndex].StartOffset;
            return (next with { Rows = [.. kept, .. next.Rows], StartOffset = start },
                Math.Max(0, anchor.Text.First - anchor.KeepTextIndex));
        }

        var startBefore = Math.Max(firstOffset, reader.GetPreviousPageOffset(old.StartOffset, token));
        var previous = reader.ReadPage(startBefore, cancellationToken: token, endOffset: old.StartOffset);
        if (previous.Rows.Count == 0) return (old, anchor.Text.First);
        if (previous.NextOffset != old.StartOffset)
            return (reader.ReadPage(startBefore, cancellationToken: token), -1);
        var keptRows = old.Rows.Take(anchor.KeepIndex).ToArray();
        var prefixLength = previous.Rows.Sum(row => row.Text.Length)
            + (previous.Rows.Skip(1).Count(row => !row.StartsWithinLine) + (keptRows[0].StartsWithinLine ? 0 : 1)) * Environment.NewLine.Length;
        var nextOffset = keptRows.Length == old.Rows.Count ? old.NextOffset : keptRows[^1].EndOffset;
        return (old with { Rows = [.. previous.Rows, .. keptRows], StartOffset = previous.StartOffset, NextOffset = nextOffset,
            HasMore = nextOffset < old.FileLength }, prefixLength + anchor.Text.First);
    }

    private static double ResolveScrollAnchor(TextBox text, ScrollViewer scroll, TextScrollAnchor anchor, int textIndex)
    {
        if (textIndex < 0) return scroll.ScrollBarMaximum.Y;
        if (scroll.Presenter is not { } viewport
            || text.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter
            || presenter.TranslatePoint(default, viewport) is not { } origin) return scroll.Offset.Y;
        var y = presenter.TextLayout.HitTestTextPosition(textIndex).Y;
        return scroll.Offset.Y + origin.Y + y - anchor.ViewportY + anchor.Step;
    }
}
