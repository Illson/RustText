using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using RustText.Demo.Platform;

namespace RustText.Demo;

internal sealed class ProxyCheckerView : UserControl, IDisposable
{
    private static readonly FontFamily AddressFont = new("Cascadia Mono, Consolas, monospace");
    private static readonly ProxyProtocol[] Protocols = [ProxyProtocol.Http, ProxyProtocol.Https, ProxyProtocol.Socks5];

    private readonly TextEditor _input = new()
    {
        Name = "ProxyCheckerInput",
        Background = FocusTheme.Editor,
        Foreground = FocusTheme.Text,
        Document = new TextDocument(),
        FontFamily = AddressFont,
        FontSize = FocusTheme.EditorFontSize,
        ShowLineNumbers = true,
        LineNumbersForeground = FocusTheme.Gutter,
        LineNumbersMargin = new Thickness(6, 0, 8, 0),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private readonly ComboBox _protocol = new()
    {
        Name = "ProxyCheckerDefaultProtocol",
        ItemsSource = Protocols.Select(ProtocolName).ToArray(),
        SelectedIndex = 0,
        MinWidth = 88
    };

    private readonly NumericUpDown _timeout = Number(5, 1, 120);
    private readonly NumericUpDown _parallelism = Number(8, 1, 32);
    private readonly Button _start = new() { Content = "Проверить", Classes = { "focus-primary" } };
    private readonly Button _stop = new() { Content = "Остановить" };
    private readonly Button _copy = new() { Content = "Копировать рабочие" };
    private readonly Button _delay = new() { Content = "Задержка ↕", Classes = { "focus-quiet" } };
    private readonly Button[] _filters = [new(), new(), new()];
    private readonly TextBlock _progress = Label("");
    private readonly TextBlock _empty = Label("");
    private readonly List<ProxyCheckResult> _results = [];
    private readonly ObservableCollection<ProxyCheckResult> _visibleResults = [];
    private readonly ConcurrentQueue<ProxyCheckResult> _pending = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private CancellationTokenSource? _run;
    private int _filter;
    private bool? _ascending;
    private int _working;
    private int _total;
    private bool _disposed;

    public ProxyCheckerView()
    {
        Name = nameof(ProxyCheckerView);
        _input.Options.EnableHyperlinks = false;
        _input.Options.EnableEmailHyperlinks = false;
        AutomationProperties.SetName(_input, "Список прокси");
        ToolTip.SetTip(_protocol, "Протокол для строк без http://, https:// или socks5://");
        ToolTip.SetTip(_copy, "Копирует адреса со схемой и введёнными данными авторизации");

        var commands = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 6,
            Children =
            {
                _start, _stop, LabeledField("Без схемы", _protocol),
                LabeledField("Тайм-аут, с", _timeout), LabeledField("Параллельно", _parallelism)
            }
        };
        var toolbar = new Border
        {
            Child = commands,
            Background = FocusTheme.Panel,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 8)
        };

        var inputHint = Label("По одному адресу на строку");
        inputHint.TextTrimming = TextTrimming.CharacterEllipsis;
        var inputHeader = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(10, 7),
            Children = { new TextBlock { Text = "Список прокси", FontWeight = FontWeight.SemiBold }, inputHint }
        };
        var left = new DockPanel { Children = { inputHeader, _input } };
        DockPanel.SetDock(inputHeader, Dock.Top);

        var filters = new WrapPanel { ItemSpacing = 6, LineSpacing = 4, Margin = new Thickness(8, 7) };
        for (var i = 0; i < _filters.Length; i++)
        {
            var filter = i;
            _filters[i].Click += (_, _) =>
            {
                _filter = filter;
                UpdateFilter();
            };
            filters.Children.Add(_filters[i]);
        }

        var tableHeader = new Border
        {
            Background = FocusTheme.Panel,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Child = Columns(Cell("Прокси", FocusTheme.Text), Cell("Статус", FocusTheme.Text),
                _delay, Cell("Детали", FocusTheme.Text))
        };
        var list = new ListBox
        {
            Background = FocusTheme.Editor,
            ItemsSource = _visibleResults,
            ItemTemplate = new FuncDataTemplate<ProxyCheckResult>((result, _) => result is null ? null : CreateRow(result), supportsRecycling: false)
        };
        list.Styles.Add(new Style(s => s.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(MinHeightProperty, 27d)
            }
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.IsHitTestVisible = false;
        var results = new Panel { Children = { list, _empty } };
        var right = new DockPanel { Children = { filters, tableHeader, results } };
        DockPanel.SetDock(filters, Dock.Top);
        DockPanel.SetDock(tableHeader, Dock.Top);

        var splitter = new GridSplitter
        {
            Background = FocusTheme.Border,
            ResizeDirection = GridResizeDirection.Columns
        };
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,4,2*"),
            Children = { left, splitter, right }
        };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);

        var footer = new DockPanel
        {
            Margin = new Thickness(10, 6),
            Children = { _copy, _progress }
        };
        DockPanel.SetDock(_copy, Dock.Right);
        _progress.Margin = new Thickness(0, 0, 8, 0);
        _progress.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(_progress, $"Адрес проверки: {ProxyChecker.ProbeUrl}");
        var status = new Border
        {
            Child = footer,
            Background = FocusTheme.Panel,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };

        Content = new DockPanel
        {
            Background = FocusTheme.Editor,
            Children = { toolbar, status, body }
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);

        Loaded += (_, _) => _input.TextArea.Focus();
        _start.Click += async (_, _) => await CheckAsync();
        _stop.Click += (_, _) =>
        {
            _stop.IsEnabled = false;
            _run?.Cancel();
        };
        _copy.Click += async (_, _) => await CopyWorkingAsync();
        _delay.Click += (_, _) =>
        {
            _ascending = _ascending is null ? true : !_ascending.Value;
            _delay.Content = _ascending == true ? "Задержка ↑" : "Задержка ↓";
            UpdateFilter();
        };
        _input.TextChanged += (_, _) => _start.IsEnabled = _run is null && _input.Document.TextLength > 0;
        _refresh.Tick += (_, _) => ReceiveResults();
        UpdateControls();
        UpdateResults();
        _progress.Text = "Проверка через connectivitycheck.gstatic.com";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Stop();
        _run?.Cancel();
    }

    private async Task CheckAsync()
    {
        if (_disposed || _run is not null) return;
        var input = _input.Text;
        using var reader = new StringReader(input);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line)) count++;
        }
        if (count == 0)
        {
            _progress.Text = "Добавьте хотя бы один адрес";
            return;
        }

        var protocol = Protocols[Math.Max(0, _protocol.SelectedIndex)];
        var timeout = TimeSpan.FromSeconds((double)(_timeout.Value ?? 5));
        var parallelism = (int)(_parallelism.Value ?? 8);
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        _total = count;
        _working = 0;
        _results.Clear();
        _visibleResults.Clear();
        UpdateControls();
        UpdateResults();
        _refresh.Start();

        try
        {
            await Task.Run(() => ProxyChecker.CheckAsync(
                input, protocol, timeout, parallelism, _pending.Enqueue, cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                ReceiveResults();
                _progress.Text = $"Проверка прервана · {_results.Count} из {_total}";
            }
        }
        finally
        {
            _refresh.Stop();
            _run = null;
            if (!_disposed)
            {
                ReceiveResults();
                if (cancellation.IsCancellationRequested)
                {
                    _progress.Text = $"Остановлено · {_results.Count} из {_total}";
                }
                UpdateControls();
            }
        }
    }

    private async Task CopyWorkingAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var working = _results.Where(result => result.CopyValue is not null).OrderBy(result => result.LineNumber);
        try
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, working.Select(result => result.CopyValue)));
        }
        catch (Exception)
        {
            if (!_disposed) _progress.Text = "Не удалось записать в буфер обмена";
        }
    }

    private void ReceiveResults()
    {
        var changed = false;
        while (_pending.TryDequeue(out var result))
        {
            changed = true;
            _results.Add(result);
            if (result.Status == ProxyCheckStatus.Working) _working++;
            if (MatchesFilter(result)) AddVisible(result);
        }
        if (changed) UpdateResults();
    }

    private void AddVisible(ProxyCheckResult result)
    {
        if (_ascending is null)
        {
            _visibleResults.Add(result);
            return;
        }

        var first = 0;
        var last = _visibleResults.Count;
        while (first < last)
        {
            var middle = first + (last - first) / 2;
            if (CompareDelay(_visibleResults[middle], result) <= 0) first = middle + 1;
            else last = middle;
        }
        _visibleResults.Insert(first, result);
    }

    private int CompareDelay(ProxyCheckResult left, ProxyCheckResult right)
    {
        if (left.ElapsedMilliseconds is not { } first) return right.ElapsedMilliseconds is null ? 0 : 1;
        if (right.ElapsedMilliseconds is not { } second) return -1;
        return _ascending == true ? first.CompareTo(second) : second.CompareTo(first);
    }

    private bool MatchesFilter(ProxyCheckResult result) => _filter switch
    {
        1 => result.Status == ProxyCheckStatus.Working,
        2 => result.Status != ProxyCheckStatus.Working,
        _ => true
    };

    private void UpdateFilter()
    {
        var visible = _results.Where(MatchesFilter).ToList();
        if (_ascending is not null) visible.Sort(CompareDelay);
        _visibleResults.Clear();
        foreach (var result in visible) _visibleResults.Add(result);
        UpdateFiltersAndEmptyState();
    }

    private void UpdateResults()
    {
        UpdateFiltersAndEmptyState();
        _copy.IsEnabled = _working > 0;
        _progress.Text = $"Проверено {_results.Count} из {_total}";
    }

    private void UpdateFiltersAndEmptyState()
    {
        _filters[0].Content = $"Все {_results.Count}";
        _filters[1].Content = $"Работают {_working}";
        _filters[2].Content = $"Ошибки {_results.Count - _working}";
        for (var i = 0; i < _filters.Length; i++)
        {
            _filters[i].BorderBrush = i == _filter ? FocusTheme.Accent : FocusTheme.Border;
        }

        _empty.IsVisible = _visibleResults.Count == 0;
        _empty.Text = _total == 0 ? "Здесь появятся результаты" : "Нет результатов";
    }

    private void UpdateControls()
    {
        var running = _run is not null;
        _start.IsEnabled = !running && _input.Document.TextLength > 0;
        _stop.IsEnabled = running;
        _input.IsReadOnly = running;
        _protocol.IsEnabled = _timeout.IsEnabled = _parallelism.IsEnabled = !running;
    }

    private static Control CreateRow(ProxyCheckResult result)
    {
        var (status, brush) = result.Status switch
        {
            ProxyCheckStatus.Working => ("Работает", FocusTheme.Success),
            ProxyCheckStatus.Timeout => ("Тайм-аут", FocusTheme.Warning),
            ProxyCheckStatus.Invalid => ("Ошибка ввода", FocusTheme.Danger),
            _ => ("Ошибка", FocusTheme.Danger)
        };

        var address = Cell(result.Address, FocusTheme.Text);
        address.FontFamily = AddressFont;
        var detail = result.Protocol is { } protocol ? ProtocolName(protocol) : "";
        if (result.Detail.Length != 0)
        {
            detail = detail.Length == 0 ? result.Detail : $"{detail} · {result.Detail}";
        }
        var elapsed = result.ElapsedMilliseconds is { } delay ? $"{delay} мс" : "—";
        return new Border
        {
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Columns(address, Cell(status, brush), Cell(elapsed, FocusTheme.Text), Cell(detail, FocusTheme.Muted))
        };
    }

    private static Grid Columns(params Control[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2.55*,1.35*,1.2*,1.5*") };
        for (var col = 0; col < cells.Length; col++)
        {
            Grid.SetColumn(cells[col], col);
            grid.Children.Add(cells[col]);
        }
        return grid;
    }

    private static TextBlock Cell(string text, IBrush brush)
    {
        var cell = Label(text);
        cell.Margin = new Thickness(8, 2);
        cell.Foreground = brush;
        cell.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(cell, text);
        return cell;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = FocusTheme.Muted, VerticalAlignment = VerticalAlignment.Center
    };

    private static StackPanel LabeledField(string text, Control field)
    {
        AutomationProperties.SetName(field, text);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Label(text), field } };
    }

    private static NumericUpDown Number(int value, int minimum, int maximum) => new()
    {
        Minimum = minimum, Maximum = maximum, Value = value, Increment = 1, FormatString = "0", ShowButtonSpinner = false, Width = 48
    };

    private static string ProtocolName(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Https => "HTTPS",
        ProxyProtocol.Socks5 => "SOCKS5",
        _ => "HTTP"
    };
}
