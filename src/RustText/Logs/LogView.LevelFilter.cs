using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    private int _sourceSelectionStart;
    private int _sourceSelectionEnd;
    private int _sourceCaretIndex;

    private Grid BuildLevelFilterViewport()
    {
        AutomationProperties.SetAutomationId(_filteredLogText, "LogLevelFilterResults");
        AutomationProperties.SetName(_filteredLogText, "Строки лога с выбранными уровнями");
        AutomationProperties.SetAutomationId(_filterStatus, "LogLevelFilterStatus");
        var textLayer = new Grid { Background = FocusTheme.Editor };
        textLayer.Children.Add(_filteredLogText);
        _filteredLevelBands = new LogLevelBands(_filteredLogText) { IsHitTestVisible = false, ClipToBounds = true };
        textLayer.Children.Add(_filteredLevelBands);
        _filteredLogText.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => _filteredLevelBands.InvalidateVisual());
        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 4 };
        _levelFilter = new LogLevelFilterPanel();
        _levelFilter.LevelsChanged += OnFilterLevelsChanged;
        _levelFilter.CloseRequested += CloseInlineLevelFilter;
        body.Children.Add(textLayer);
        Grid.SetRow(_filterStatus, 1);
        body.Children.Add(_filterStatus);
        return body;
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

    private async Task LoadFilteredPageAsync(FilterCursor cursor)
    {
        if (_closed || _levelFilter is null) return;
        _filterReading?.Cancel();
        _filterReading?.Dispose();
        var reading = new CancellationTokenSource();
        _filterReading = reading;
        var generation = ++_filterGeneration;
        _filterCurrent = cursor;
        _filterNext = null;
        _filteredLogText.Text = string.Empty;
        _filterStatus.Text = _levelFilter.SelectedLevels == 0 ? "Выберите уровень" : "Поиск по логу…";
        if (_levelFilter.SelectedLevels == 0)
        {
            _filterReading = null;
            reading.Dispose();
            RefreshButtons();
            return;
        }
        var stamp = _fileStamp;
        var originalDigest = _observedDigest;
        var encoding = _reader.Encoding;
        var levels = _levelFilter.SelectedLevels;
        try
        {
            var page = await Task.Run(() => LogLevelFilter.Read(ReadRows, cursor, levels, reading.Token), reading.Token);
            if (reading.IsCancellationRequested || generation != _filterGeneration || _closed
                || !_levelFilterOpen || _levelFilterViewport?.IsVisible != true) return;
            _filteredLogText.Text = _filteredLevelBands?.SetFilteredPage(page) ?? string.Empty;
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

        IEnumerable<LogRow> ReadRows(long offset, CancellationToken token)
        {
            using var file = LogDataReader.Open(_path);
            var reader = new LogDataReader(file, encoding);
            var now = FileStamp.Read(_path, file);
            if (now != stamp && !(now.Identity == stamp.Identity && now.Length > stamp.Length && originalDigest is not null
                && originalDigest.AsSpan().SequenceEqual(reader.ComputeBookmarkDigest(stamp.Length))))
                throw new IOException("Файл изменился. Закройте фильтр и повторите попытку.");
            foreach (var row in LogLevelFilter.FileRows(reader, offset, stamp.Length, token)) yield return row;
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

    private void ShowFilteredViewport()
    {
        if (_levelFilterViewport?.IsVisible == true) return;
        _sourceSelectionStart = _logText.SelectionStart;
        _sourceSelectionEnd = _logText.SelectionEnd;
        _sourceCaretIndex = _logText.CaretIndex;
        _contentViewport!.Children[0].IsVisible = false;
        _contentViewport.Children.Add(_levelFilterViewport!);
        _levelFilterViewport!.IsVisible = true;
    }

    private void ShowSourceViewport()
    {
        if (_levelFilterViewport is null || _contentViewport is null) return;
        var wasFiltered = _levelFilterViewport.IsVisible;
        _levelFilterViewport.IsVisible = false;
        _contentViewport.Children[0].IsVisible = true;
        if (!wasFiltered) return;
        _contentViewport.Children.Remove(_levelFilterViewport);
        _logText.CaretIndex = Math.Clamp(_sourceCaretIndex, 0, _logText.Text?.Length ?? 0);
        _logText.SelectionStart = Math.Clamp(_sourceSelectionStart, 0, _logText.Text?.Length ?? 0);
        _logText.SelectionEnd = Math.Clamp(_sourceSelectionEnd, 0, _logText.Text?.Length ?? 0);
    }
}
