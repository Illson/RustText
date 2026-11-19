using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RustText.Demo.Editor;

namespace RustText.Demo.Logs;

internal sealed class LogLevelFilterWindow : Window
{
    private readonly Func<long, CancellationToken, IEnumerable<LogRow>> _read;
    private readonly FilterCursor _first;
    private readonly Stack<FilterCursor> _history = new();
    private readonly Dictionary<LogLevel, ToggleButton> _options = [];
    private readonly SelectableTextBlock _lines = new() { FontFamily = new FontFamily("Consolas, monospace") };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = SearchBar.Status();
    private readonly Button _previous;
    private readonly Button _next;
    private CancellationTokenSource? _reading;
    private FilterCursor _current;
    private FilterCursor? _nextPage;

    internal LogLevelFilterWindow(string name, double fontSize, long start,
        Func<long, CancellationToken, IEnumerable<LogRow>> read)
    {
        _read = read;
        _first = new(start);
        Title = $"Уровни лога — {name}";
        Width = 860;
        Height = 560;
        MinWidth = 420;
        MinHeight = 220;
        Background = FocusTheme.Panel;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var levels = new WrapPanel { ItemSpacing = 4, LineSpacing = 3 };
        foreach (var (level, label) in new[] { (LogLevel.Fatal, "Fatal"), (LogLevel.Error, "Error"), (LogLevel.Warning, "Warn"),
            (LogLevel.Info, "Info"), (LogLevel.Debug, "Debug"), (LogLevel.Trace, "Trace"), (LogLevel.Other, "Без уровня") })
        {
            var option = SearchBar.Option(label, $"Показывать строки · {label}");
            option.IsChecked = level is LogLevel.Fatal or LogLevel.Error or LogLevel.Warning;
            option.IsCheckedChanged += (_, _) => { _history.Clear(); _ = LoadAsync(_first); };
            levels.Children.Add(option);
            _options.Add(level, option);
        }
        _previous = SearchBar.Command("Назад", () => { if (_history.TryPop(out var back)) _ = LoadAsync(back); });
        _next = SearchBar.Command("Дальше", () =>
        {
            if (_nextPage is not { } next) return;
            _history.Push(_current);
            _ = LoadAsync(next);
        });
        _lines.FontSize = fontSize;
        _lines.Margin = new Thickness(8, 5);
        _scroll.Content = _lines;
        _scroll.Background = FocusTheme.Editor;
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(8), RowSpacing = 6 };
        layout.Children.Add(levels);
        Grid.SetRow(_scroll, 1);
        layout.Children.Add(_scroll);
        var footer = SearchBar.Footer(_status, _previous, _next, SearchBar.Command("Закрыть", Close));
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Content = layout;
        Opened += (_, _) => _ = LoadAsync(_first);
        Closed += (_, _) => { _reading?.Cancel(); _reading = null; };
    }

    private async Task LoadAsync(FilterCursor cursor)
    {
        _reading?.Cancel();
        using var reading = new CancellationTokenSource();
        _reading = reading;
        _current = cursor;
        _nextPage = null;
        _next.IsEnabled = _previous.IsEnabled = false;
        _lines.Inlines!.Clear();
        _scroll.Offset = default;
        var levels = _options.Where(entry => entry.Value.IsChecked == true)
            .Aggregate((LogLevel)0, (mask, entry) => mask | entry.Key);
        try
        {
            if (levels == 0) { _status.Text = "Выберите уровень"; return; }
            _status.Text = "Поиск по логу…";
            var page = await Task.Run(() => LogLevelFilter.Read(_read, cursor, levels, reading.Token), reading.Token);
            if (reading.IsCancellationRequested) return;
            for (var i = 0; i < page.Rows.Count; i++)
            {
                var row = page.Rows[i];
                if (i > 0 && !row.Continuation) _lines.Inlines.Add(new LineBreak());
                _lines.Inlines.Add(new Run(row.Text) { Foreground = LogLevels.Brush(row.Level) });
            }
            _status.Text = page.Rows.Count == 0 ? "Совпадений нет" : $"Страница {LineNumbers.Format(_history.Count + 1)}";
            _nextPage = page.Next;
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (!reading.IsCancellationRequested) _status.Text = $"Не удалось прочитать лог. {error.Message}";
        }
        finally
        {
            if (ReferenceEquals(_reading, reading))
            {
                _reading = null;
                _previous.IsEnabled = _history.Count > 0;
                _next.IsEnabled = _nextPage.HasValue;
            }
        }
    }
}
