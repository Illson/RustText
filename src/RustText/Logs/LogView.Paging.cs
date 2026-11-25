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
    private PointerEventArgs? _selectionDrag;

    private readonly record struct TextScrollAnchor(int First, int Last, double Offset, double ViewportY, double Step);
    private readonly record struct ScrollOverlap(int Index, int Start, int End);
    private sealed record ScrollAnchor(LogPage Page, bool Forward, TextScrollAnchor Text, ScrollOverlap Keep);
    private readonly record struct ScrollSelection(int Caret, int Start, int End)
    {
        public void Restore(TextBox text)
        {
            text.CaretIndex = Caret;
            text.SelectionStart = Start;
            text.SelectionEnd = End;
        }
    }

    private void TrackSelectionPointer(object? sender, PointerEventArgs e)
    {
        if (e.RoutedEvent == PointerPressedEvent && e.Pointer.Type == PointerType.Mouse
            && e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            _selectionDrag = e;
            StopFollowingEnd();
        }
        else if (ReferenceEquals(e.Pointer, _selectionDrag?.Pointer))
        {
            if (e.RoutedEvent == PointerReleasedEvent && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                _selectionDrag = null;
            else if (e.RoutedEvent == PointerMovedEvent)
                _selectionDrag = e;
        }
    }

    private bool IsDraggingSelection(TextBox text) =>
        _selectionDrag?.Pointer.Captured is TextPresenter presenter && text.IsVisualAncestorOf(presenter);

    private void TrackSelectionWheel(TextBox text, PointerWheelEventArgs e, bool loading)
    {
        if (!IsDraggingSelection(text) || !ReferenceEquals(_selectionDrag?.Pointer, e.Pointer)) return;
        _selectionDrag = e;
        if (!loading) QueueSelectionAtPointer(text);
    }

    private void QueueSelectionAtPointer(TextBox text)
    {
        if (!IsDraggingSelection(text)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectionDrag is not { Pointer.Captured: TextPresenter presenter } drag
                || !text.IsVisualAncestorOf(presenter) || VisualRoot is not Visual root) return;
            var point = drag.GetCurrentPoint(root);
            // Let TextBox move the live end of the drag to the content now under the pointer.
            presenter.RaiseEvent(new PointerEventArgs(PointerMovedEvent, presenter, drag.Pointer,
                root, point.Position, drag.Timestamp, point.Properties, drag.KeyModifiers));
        }, DispatcherPriority.Loaded);
    }

    private bool TryKeepScrollSelection(TextBox text, ScrollOverlap keep, int shift, out ScrollSelection? selection,
        bool limitReached = false)
    {
        selection = null;
        var dragging = IsDraggingSelection(text);
        var start = text.SelectionStart;
        var end = text.SelectionEnd;
        limitReached &= dragging;
        if (start == end && !dragging) return true;
        if (!limitReached && Math.Min(start, end) >= keep.Start && Math.Max(start, end) <= keep.End)
        {
            selection = new(Math.Clamp(text.CaretIndex, keep.Start, keep.End) + shift, start + shift, end + shift);
            return true;
        }
        if (dragging)
        {
            SetStatus("Выделение не помещается в загруженный блок. Скопируйте его, прежде чем продолжить прокрутку.");
            return false;
        }
        selection = new(0, 0, 0);
        return true;
    }

    private void OnLogPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || e.KeyModifiers != KeyModifiers.None || Math.Abs(e.Delta.Y) < Math.Abs(e.Delta.X)) return;
        if (_levelFilterViewport?.IsVisible == true)
        {
            if (e.Source is Visual source && (ReferenceEquals(_filteredLogText, source) || _filteredLogText.IsVisualAncestorOf(source)))
            {
                var loading = TryContinueFilteredScroll(e.Delta.Y);
                TrackSelectionWheel(_filteredLogText, e, loading);
                if (loading) e.Handled = true;
            }
            return;
        }
        if (!IsLogTextSource(e.Source as Visual)) return;

        StopFollowingEnd();
        var forward = e.Delta.Y < 0;
        if (PageAtScrollBoundary(forward) is { } page)
        {
            e.Handled = true;
            TrackSelectionWheel(_logText, e, loading: true);
            _ = ContinueScrollingAsync(page, forward, Math.Abs(e.Delta.Y));
        }
        else TrackSelectionWheel(_logText, e, loading: false);
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

        // shortcut: keep an active selection within a bounded window; larger ranges would need file-backed selection.
        var dragging = IsDraggingSelection(_logText);
        var overlapBytes = dragging ? LogDataReader.MaximumPageBytes - LogDataReader.DefaultPageBytes : 2 * LogDataReader.DefaultPageBytes;
        var overlapRows = dragging ? LogDataReader.MaximumPageSize - LogDataReader.DefaultPageSize : 3 * LogDataReader.DefaultPageSize;
        var keep = FindScrollOverlap(_logText, page.Rows, anchor, forward, row => row.Text.Length, row => row.StartsWithinLine,
            (i, _) => forward
                ? page.Rows.Count - i <= overlapRows && page.NextOffset - page.Rows[i].StartOffset <= overlapBytes
                : i < overlapRows && page.Rows[i].EndOffset - page.StartOffset <= overlapBytes);
        return new(page, forward, anchor, keep);
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

    private ScrollOverlap FindScrollOverlap<T>(TextBox text, IReadOnlyList<T> rows, TextScrollAnchor anchor,
        bool forward, Func<T, int> length, Func<T, bool> continuation, Func<int, int, bool> fits)
    {
        if (rows.Count == 0) return default;
        var first = anchor.First;
        var last = anchor.Last;
        if (text.SelectionStart != text.SelectionEnd || IsDraggingSelection(text))
        {
            first = Math.Min(first, Math.Min(text.SelectionStart, text.SelectionEnd));
            last = Math.Max(last, Math.Max(text.SelectionStart, text.SelectionEnd));
        }
        var keep = 0;
        var keepText = 0;
        var textIndex = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var end = textIndex + length(rows[i - 1]);
            textIndex = end + (continuation(rows[i]) ? 0 : Environment.NewLine.Length);
            if (forward ? textIndex > first && fits(i - 1, keepText)
                : textIndex > last || !fits(i, textIndex + length(rows[i]))) break;
            keep = i;
            keepText = textIndex;
        }
        return forward ? new(keep, keepText, text.Text?.Length ?? 0) : new(keep + 1, 0, keepText + length(rows[keep]));
    }

    private static (LogPage Page, int TextIndex) ReadScrolledPage(LogDataReader reader, ScrollAnchor anchor,
        long firstOffset, CancellationToken token)
    {
        var old = anchor.Page;
        var first = anchor.Forward ? anchor.Keep.Index : 0;
        var count = anchor.Forward ? old.Rows.Count - first : anchor.Keep.Index;
        var start = anchor.Forward ? (first == 0 ? old.StartOffset : old.Rows[first].StartOffset)
            : Math.Max(firstOffset, reader.GetPreviousPageOffset(old.StartOffset, token));
        var bytes = (int)Math.Clamp(old.Rows[first + count - 1].EndOffset - start
            + (anchor.Forward ? LogDataReader.DefaultPageBytes : 0), 4, LogDataReader.MaximumPageBytes);
        // Read the overlap and the adjacent rows together: a paused file can be rewritten at the same offsets.
        var page = reader.ReadPage(start, Math.Min(count + LogDataReader.DefaultPageSize, LogDataReader.MaximumPageSize),
            Math.Min(bytes, LogDataReader.DefaultFrameBytes), bytes, token);
        var prefix = 0;
        var i = 0;
        while (i < page.Rows.Count && page.Rows[i].StartOffset < old.Rows[first].StartOffset)
        {
            prefix += page.Rows[i++].Text.Length;
            if (i < page.Rows.Count && !page.Rows[i].StartsWithinLine) prefix += Environment.NewLine.Length;
        }
        if (!page.Rows.Skip(i).Take(count).SequenceEqual(old.Rows.Skip(first).Take(count))) return (page, -1);
        return (page, prefix + Math.Max(0, anchor.Text.First - anchor.Keep.Start));
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
