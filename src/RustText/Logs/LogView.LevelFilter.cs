using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using RustText.Demo.Editor;

namespace RustText.Demo.Logs;

internal sealed partial class LogView
{
    private readonly TextBox _filteredLogText = new()
    {
        Classes = { "focus-log-editor" },
        IsReadOnly = true,
        AcceptsReturn = true,
        FontFamily = new FontFamily("Consolas, monospace"),
        FontSize = FocusTheme.EditorFontSize,
        IsInactiveSelectionHighlightEnabled = true,
        Padding = new Thickness(8, 6),
        Background = FocusTheme.Editor,
        Foreground = FocusTheme.Text,
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly TextBlock _filterStatus = SearchBar.Status();
    private LogLevelFilterPanel? _levelFilter;
    private LogLevelBands? _filteredLevelBands;
    private CancellationTokenSource? _filterReading;
    private readonly Stack<FilterCursor> _filterHistory = new();
    private FilterCursor _filterCurrent;
    private FilterCursor? _filterNext;
    private bool _levelFilterOpen;
    private long _filterGeneration;
    private long _filterSnapshotLength;
    private int _sourceSelectionStart;
    private int _sourceSelectionEnd;
    private int _sourceCaretIndex;
    private long?[] _filteredSourceLineNumbers = [];
    private FilterPage? _filteredPage;
    private ScrollViewer? _filteredLogScrollViewer;

    private Grid BuildLevelFilterViewport()
    {
        AutomationProperties.SetAutomationId(_filteredLogText, "LogLevelFilterResults");
        AutomationProperties.SetName(_filteredLogText, "Строки лога с выбранными уровнями");
        AutomationProperties.SetAutomationId(_filterStatus, "LogLevelFilterStatus");
        var textLayer = new Grid { Background = FocusTheme.Editor };
        textLayer.Children.Add(_filteredLogText);
        _filteredLevelBands = new LogLevelBands(_filteredLogText) { IsHitTestVisible = false, ClipToBounds = true };
        textLayer.Children.Add(_filteredLevelBands);
        _filteredLogText.LayoutUpdated += OnFilteredLogTextLayoutUpdated;
        _levelFilter = new LogLevelFilterPanel();
        _levelFilter.LevelsChanged += OnFilterLevelsChanged;
        _levelFilter.CloseRequested += CloseInlineLevelFilter;
        _filterStatus.IsVisible = false;
        return textLayer;
    }

    private void OpenInlineLevelFilter()
    {
        if (_closed || !CanFilterLevels || _levelFilterViewport is null || _levelFilter is null) return;
        _levelFilterOpen = true;
        _filterHistory.Clear();
        _filterNext = null;
        _filteredLogText.FontSize = _logText.FontSize;
        _levelFilter!.IsVisible = true;
        _levelFilterButton.Content = "Закрыть уровни";
        RefreshButtons();
        OnFilterLevelsChanged();
    }

    private void CloseInlineLevelFilter()
    {
        _filterReading?.Cancel();
        _filterReading?.Dispose();
        _filterReading = null;
        if (!_levelFilterOpen) return;
        _filterGeneration++;
        _levelFilterOpen = false;
        if (_levelFilter is not null) _levelFilter.IsVisible = false;
        ShowSourceViewport();
        if (_levelFilter is not null) _levelFilter.SetLevels(LogLevel.All);
        _filteredLogText.Text = string.Empty;
        _filteredLevelBands?.SetEditedText(string.Empty);
        _filterHistory.Clear();
        _filterNext = null;
        _levelFilterButton.Content = "Уровни…";
        RefreshButtons();
    }

    private void OnFilterLevelsChanged()
    {
        if (!_levelFilterOpen || _levelFilter is null) return;
        _filterGeneration++;
        var filtered = _levelFilter.SelectedLevels != LogLevel.All;
        if (filtered) ShowFilteredViewport();
        else ShowSourceViewport();
        RefreshButtons();
        _filterReading?.Cancel();
        if (!filtered)
        {
            _filterNext = null;
            _filterHistory.Clear();
            _filterStatus.Text = string.Empty;
            RefreshButtons();
            return;
        }
        _filterHistory.Clear();
        _ = LoadFilteredPageAsync(new FilterCursor(_hiddenOffset));
    }

    private Task RefreshAppendedFilterAsync()
    {
        if (_levelFilterViewport?.IsVisible != true || _filterReading is not null
            || _filteredPage is not { } page || _filterSnapshotLength >= _fileStamp.Length)
            return Task.CompletedTask;

        if (page.Next is null) return LoadFilteredPageAsync(page.Tail, page);
        _filterSnapshotLength = _fileStamp.Length;
        UpdateFilteredMinimap(page);
        return Task.CompletedTask;
    }

    private async Task LoadFilteredPageAsync(FilterCursor cursor, FilterPage? previous = null)
    {
        if (_closed || _levelFilter is null) return;
        _filterReading?.Cancel();
        _filterReading?.Dispose();
        var reading = new CancellationTokenSource();
        _filterReading = reading;
        var token = reading.Token;
        var generation = ++_filterGeneration;
        if (previous is null) _filterCurrent = cursor;
        _filterNext = null;
        // A re-entrant click must not see enabled navigation while the old text is being cleared.
        RefreshButtons();
        if (previous is null)
        {
            _filteredLogText.Text = string.Empty;
            _filteredSourceLineNumbers = [];
            _filteredPage = null;
            _lineGutterText.Text = string.Empty;
            _filterStatus.Text = _levelFilter.SelectedLevels == 0 ? "Выберите уровень" : "Поиск по логу…";
        }
        if (_levelFilter.SelectedLevels == 0)
        {
            _filterReading = null;
            reading.Dispose();
            RefreshButtons();
            return;
        }
        var stamp = _fileStamp;
        var dataGeneration = _dataGeneration;
        var originalDigest = _observedDigest;
        var encoding = _reader.Encoding;
        var levels = _levelFilter.SelectedLevels;
        try
        {
            var page = await Task.Run(() =>
            {
                using var file = LogDataReader.Open(_path);
                var reader = new LogDataReader(file, encoding);
                var now = FileStamp.Read(_path, file);
                if (now != stamp && !(now.Identity == stamp.Identity && now.Length > stamp.Length && originalDigest is not null
                    && originalDigest.AsSpan().SequenceEqual(reader.ComputeBookmarkDigest(stamp.Length))))
                    throw new IOException("Файл изменился. Закройте фильтр и повторите попытку.");
                var sourceLine = cursor.SourceLine ?? (cursor.Offset < reader.PreambleLength
                    ? 1 : FindLineNumber(reader, cursor.Offset, token));
                var start = cursor with { SourceLine = sourceLine };
                // The last frame may not be a complete line yet. Keep earlier results and reread that frame.
                var prefix = previous?.Rows.Where(row => row.StartOffset < cursor.Offset).ToList();
                IEnumerable<LogRow> ReadRows(long offset, CancellationToken token) =>
                    LogLevelFilter.FileRows(reader, offset, stamp.Length, token);
                return LogLevelFilter.Read(ReadRows, start, levels, token, prefix);
            }, token);
            if (reading.IsCancellationRequested || generation != _filterGeneration || dataGeneration != _dataGeneration || _closed
                || !_levelFilterOpen || _levelFilterViewport?.IsVisible != true) return;
            _filterSnapshotLength = stamp.Length;
            if (previous is null || !page.Rows.SequenceEqual(previous.Rows))
                _filteredLogText.Text = _filteredLevelBands?.SetFilteredPage(page) ?? string.Empty;
            _filteredPage = page;
            UpdateFilteredLineGutter(page);
            UpdateFilteredMinimap(page);
            if (page.Rows.Count > 0 && page.Rows[0].SourceLine is null
                && page.Rows[0].StartOffset <= _indexedTo)
                _ = RefreshFilteredLineNumbersAsync(_reader, page, generation);
            _filterNext = page.Next;
            _filterStatus.Text = page.Rows.Count == 0 ? "Совпадений нет" : $"Страница {LineNumbers.Format(_filterHistory.Count + 1)}";
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (!reading.IsCancellationRequested && generation == _filterGeneration)
                _filterStatus.Text = $"Не удалось прочитать лог. {error.Message}";
        }
        finally
        {
            if (ReferenceEquals(_filterReading, reading))
            {
                _filterReading = null;
                reading.Dispose();
                RefreshButtons();
            }
        }
    }

    private async Task NavigateFilteredAsync(bool forward)
    {
        if (_filterReading is not null || !_levelFilterOpen || _levelFilterViewport?.IsVisible != true) return;
        if (forward)
        {
            if (_filterNext is not { } next) return;
            _filterHistory.Push(_filterCurrent);
            await LoadFilteredPageAsync(next);
        }
        else if (_filterHistory.TryPop(out var previous)) await LoadFilteredPageAsync(previous);
    }

    private void UpdateFilteredLineGutter(FilterPage page)
    {
        var lineNumbers = new List<long?>(page.Rows.Count);
        foreach (var row in page.Rows)
        {
            if (lineNumbers.Count == 0 || !row.Continuation)
                lineNumbers.Add(row.SourceLine);
        }
        _filteredSourceLineNumbers = [.. lineNumbers];
        UpdateLineGutterText();
        RefreshLineCount();
    }

    private async Task RefreshFilteredLineNumbersAsync(LogDataReader reader, FilterPage page, long generation)
    {
        if (page.Rows.Count == 0) return;
        try
        {
            var first = page.Rows[0];
            var firstLine = await Task.Run(() => FindLineNumber(reader, first.StartOffset, _closing.Token), _closing.Token);
            if (firstLine is null || _closed || generation != _filterGeneration
                || !ReferenceEquals(reader, _reader) || !ReferenceEquals(page, _filteredPage)
                || _levelFilterViewport?.IsVisible != true) return;
            var rows = page.Rows.Select(row => row with
            {
                SourceLine = firstLine + row.RelativeLine - first.RelativeLine
            }).ToList();
            _filteredPage = page with { Rows = rows };
            UpdateFilteredLineGutter(_filteredPage);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_closed || !ReferenceEquals(reader, _reader)) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!_closed && generation == _filterGeneration && ReferenceEquals(reader, _reader))
                _filterStatus.Text = $"Не удалось уточнить номера строк. {error.Message}";
        }
    }

    private void OnFilteredLogTextLayoutUpdated(object? sender, EventArgs e)
    {
        if (_levelFilterViewport?.IsVisible != true) return;
        var scrollViewer = _filteredLogText.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        AttachFilteredScrollViewer(scrollViewer);
        SyncFilteredViewport();
    }

    private void AttachFilteredScrollViewer(ScrollViewer? scrollViewer)
    {
        if (ReferenceEquals(scrollViewer, _filteredLogScrollViewer)) return;
        if (_filteredLogScrollViewer is not null) _filteredLogScrollViewer.ScrollChanged -= OnFilteredScrollChanged;
        _filteredLogScrollViewer = scrollViewer;
        if (scrollViewer is not null) scrollViewer.ScrollChanged += OnFilteredScrollChanged;
    }

    private void OnFilteredScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _filteredLevelBands?.InvalidateVisual();
        SyncFilteredViewport();
    }

    private void SyncFilteredViewport()
    {
        if (_levelFilterViewport?.IsVisible == true && _filteredLogScrollViewer is { Viewport.Height: > 0 } scrollViewer)
        {
            _lineGutterScrollViewer.Height = scrollViewer.Viewport.Height;
            _lineGutterText.MinHeight = scrollViewer.Extent.Height;
            _lineGutterScrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y);
            if (_filteredPage is { Rows.Count: > 0 } page) UpdateFilteredMinimap(page);
        }
    }

    private void UpdateFilteredMinimap(FilterPage page)
    {
        if (_filterSnapshotLength <= _hiddenOffset) return;
        var range = Math.Max(1, _filterSnapshotLength - _hiddenOffset);
        if (page.Rows.Count == 0)
        {
            var offset = Math.Clamp(_filterCurrent.Offset - _hiddenOffset, 0, range);
            var markerStart = offset / (double)range;
            _minimap.UpdateViewport(markerStart, Math.Min(1, markerStart + 1d / range));
            return;
        }
        var visible = _filteredLogScrollViewer is { } scroll ? _filteredLevelBands?.VisibleFilteredOffsets(scroll) : null;
        var (start, end) = visible ?? (page.Rows[0].StartOffset, page.Rows[^1].EndOffset);
        _minimap.UpdateViewport((start - _hiddenOffset) / (double)range, (end - _hiddenOffset) / (double)range);
    }

    private async Task NavigateFilteredMinimapAsync(double ratio, LogDataReader reader, long request, CancellationToken token)
    {
        if (_filterSnapshotLength <= _hiddenOffset) return;
        var length = _filterSnapshotLength;
        var available = length - _hiddenOffset;
        var generation = _filterGeneration;
        try
        {
            var aligned = await Task.Run(() =>
            {
                var target = ratio >= 1
                    ? reader.GetPreviousPageOffset(length, token)
                    : _hiddenOffset + (long)(available * Math.Clamp(ratio, 0, 1));
                return reader.AlignToNextLine(target, length, token);
            }, token);
            if (_closed || request != _minimapRequest || generation != _filterGeneration
                || _levelFilterViewport?.IsVisible != true || !ReferenceEquals(reader, _reader)) return;
            if (_filterCurrent.Offset != aligned) _filterHistory.Push(_filterCurrent);
            await LoadFilteredPageAsync(new FilterCursor(Math.Clamp(aligned, _hiddenOffset, length)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_closed || !ReferenceEquals(reader, _reader)) { }
        catch (IOException error)
        {
            if (!_closed && request == _minimapRequest) SetStatus($"Переход по миникарте остановлен. {error.Message}");
        }
    }

    private void ShowFilteredViewport()
    {
        if (_levelFilterViewport is null || _levelFilterViewport.IsVisible) return;
        _sourceSelectionStart = _logText.SelectionStart;
        _sourceSelectionEnd = _logText.SelectionEnd;
        _sourceCaretIndex = _logText.CaretIndex;
        _lineGutterScrollViewer.IsVisible = true;
        _logText.IsVisible = false;
        if (_pageEditor is not null) _pageEditor.IsVisible = false;
        _levelBands.IsVisible = false;
        _verticalLogScrollbar.IsVisible = false;
        _filterStatus.IsVisible = true;
        _lineGutterScrollViewer.VerticalAlignment = VerticalAlignment.Top;
        ((Grid)_contentViewport!.Children[0]).Children.Add(_levelFilterViewport);
        _levelFilterViewport.IsVisible = true;
    }

    private void ShowSourceViewport()
    {
        if (_levelFilterViewport is null || _contentViewport is null) return;
        var wasFiltered = _levelFilterViewport.IsVisible;
        if (!wasFiltered) return;
        _levelFilterViewport.IsVisible = false;
        ((Grid)_contentViewport.Children[0]).Children.Remove(_levelFilterViewport);
        _filterStatus.IsVisible = false;
        _filteredPage = null;
        _filteredSourceLineNumbers = [];
        AttachFilteredScrollViewer(null);
        _lineGutterScrollViewer.Height = double.NaN;
        _lineGutterScrollViewer.VerticalAlignment = VerticalAlignment.Stretch;
        _lineGutterText.MinHeight = 0;
        _logText.IsVisible = _pageEdit is null;
        if (_pageEditor is not null) _pageEditor.IsVisible = _pageEdit is not null;
        _lineGutterScrollViewer.IsVisible = _pageEdit is null;
        _levelBands.IsVisible = true;
        _verticalLogScrollbar.IsVisible = true;
        UpdateLineGutterText();
        RefreshLineCount();
        UpdateMinimapViewport();
        if (_pageEdit is not null) return;
        _logText.CaretIndex = Math.Clamp(_sourceCaretIndex, 0, _logText.Text?.Length ?? 0);
        _logText.SelectionStart = Math.Clamp(_sourceSelectionStart, 0, _logText.Text?.Length ?? 0);
        _logText.SelectionEnd = Math.Clamp(_sourceSelectionEnd, 0, _logText.Text?.Length ?? 0);
    }
}
