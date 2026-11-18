using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace RustText.Demo;

public partial class MainWindow : Window
{
    private const int HistoryLimit = 128;

    private static readonly string[] SampleLines =
    [
        "09:41:00  INFO   Запуск службы импорта",
        "09:41:01  INFO   Найдено файлов: 12",
        "09:41:02  DEBUG  Очередь импорта создана",
        "09:41:04  INFO   Обработан файл input-01.csv",
        "09:41:05  WARN   Пропущена пустая строка в input-02.csv",
        "09:41:07  INFO   Обработан файл input-02.csv",
        "09:41:09  ERROR  Не удалось открыть input-03.csv",
        "09:41:10  DEBUG  Запланирована повторная попытка",
        "09:41:12  INFO   Обработан файл input-03.csv",
        "09:41:13  WARN   Ответ хранилища занял 820 мс",
        "09:41:15  INFO   Индекс обновлён",
        "09:41:16  INFO   Импорт завершён"
    ];

    private readonly string? _startupPath;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<long> _previousOffsets = [];
    private FileStream? _file;
    private LogPage? _page;
    private string[] _lines = SampleLines;
    private bool _busy;
    private bool _closed;
    private bool _firstLogFrameScheduled;

    public MainWindow() : this(null)
    {
    }

    internal MainWindow(string? startupPath)
    {
        InitializeComponent();
        _startupPath = startupPath;
        var executionMode = RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT";
        RuntimeLabel.Text = $".NET {Environment.Version.ToString(2)} · {executionMode}";
        SearchBox.TextChanged += (_, _) => RefreshLog();
        if (_startupPath is not null)
        {
            _lines = [];
            FileLabel.Text = Path.GetFileName(_startupPath);
        }

        RefreshLog();
        UpdateNavigation();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_startupPath is { } path)
        {
            await RunNavigationAsync(token => OpenPathAsync(path, token));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _closing.Cancel();
        if (!_busy)
        {
            DisposeReader();
        }

        base.OnClosed(e);
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e) =>
        await RunNavigationAsync(async token =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Открыть лог",
                FileTypeFilter =
                [
                    new FilePickerFileType("Логи и текст") { Patterns = ["*.log", "*.txt"] },
                    FilePickerFileTypes.All
                ]
            });
            if (files.Count > 0)
            {
                using var selected = files[0];
                var path = selected.TryGetLocalPath()
                    ?? throw new IOException("Выберите файл с диска.");
                await OpenPathAsync(path, token);
            }
        });

    private async void OnStartClicked(object? sender, RoutedEventArgs e) =>
        await NavigateAsync(PageMove.Start);

    private async void OnPreviousClicked(object? sender, RoutedEventArgs e) =>
        await NavigateAsync(PageMove.Previous);

    private async void OnNextClicked(object? sender, RoutedEventArgs e) =>
        await NavigateAsync(PageMove.Next);

    private async void OnMiddleClicked(object? sender, RoutedEventArgs e) =>
        await NavigateAsync(PageMove.Middle);

    private async Task OpenPathAsync(string path, CancellationToken token)
    {
        SetNotice("Чтение первой страницы…");
        var opened = await Task.Run(() =>
        {
            var file = LogPageReader.Open(path);
            try
            {
                return (File: file, Page: LogPageReader.ReadPage(file, cancellationToken: token));
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }, token);

        if (_closed)
        {
            opened.File.Dispose();
            return;
        }

        _file?.Dispose();
        _file = opened.File;
        _page = null;
        _previousOffsets.Clear();
        FileLabel.Text = Path.GetFileName(opened.File.Name);
        ToolTip.SetTip(FileLabel, opened.File.Name);
        SearchBox.Clear();
        ShowPage(opened.Page);
    }

    private Task NavigateAsync(PageMove move) => RunNavigationAsync(async token =>
    {
        if (_file is not { } file || _page is not { } current)
        {
            return;
        }

        var previousOffset = _previousOffsets.Count > 0 ? _previousOffsets[^1] : current.StartOffset;
        SetNotice("Чтение страницы…");
        var page = await Task.Run(() =>
        {
            var offset = move switch
            {
                PageMove.Start => 0,
                PageMove.Previous => previousOffset,
                PageMove.Next => current.NextOffset,
                _ => LogPageReader.GetMidpointOffset(file, token)
            };
            return LogPageReader.ReadPage(file, offset, cancellationToken: token);
        }, token);

        if (_closed)
        {
            return;
        }

        if (move == PageMove.Start || page.WasTruncated || page.FileLength < current.FileLength)
        {
            _previousOffsets.Clear();
        }
        else if (move == PageMove.Previous && _previousOffsets.Count > 0)
        {
            _previousOffsets.RemoveAt(_previousOffsets.Count - 1);
        }
        else if (page.StartOffset != current.StartOffset)
        {
            if (_previousOffsets.Count == HistoryLimit)
            {
                _previousOffsets.RemoveAt(0);
            }

            _previousOffsets.Add(current.StartOffset);
        }

        ShowPage(page);
    });

    private async Task RunNavigationAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closed)
        {
            return;
        }

        _busy = true;
        UpdateNavigation();
        try
        {
            await action(_closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            if (!_closed)
            {
                SetNotice($"Не удалось прочитать лог. {error.Message}");
            }
        }
        finally
        {
            _busy = false;
            if (_closed)
            {
                DisposeReader();
            }
            else
            {
                UpdateNavigation();
            }
        }
    }

    private void ShowPage(LogPage page)
    {
        var shrank = page.WasTruncated || (_page is { } previous && page.FileLength < previous.FileLength);
        _page = page;
        _lines = page.Rows.Select(row => row.Text).ToArray();
        PositionLabel.Text = $"Байты [{page.StartOffset:N0}, {page.NextOffset:N0}) из {page.FileLength:N0}";
        SetNotice(shrank ? "Файл был укорочен. Кнопка «В начало» перечитает его с нуля." : null);
        RefreshLog();

        if (!_firstLogFrameScheduled && page.Rows.Count > 0 && Program.MeasurementStart is { } started)
        {
            _firstLogFrameScheduled = true;
            RequestAnimationFrame(_ => Console.WriteLine(
                $"RUSTTEXT first_log_frame_ms={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}"));
        }
    }

    private void RefreshLog()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var matches = query.Length == 0
            ? _lines
            : Array.FindAll(_lines, line => line.Contains(query, StringComparison.OrdinalIgnoreCase));

        LogBox.PlaceholderText = matches.Length > 0 ? null
            : _page is { Rows.Count: 0 } ? "В этом фрагменте нет строк" : "Совпадений нет";
        LogBox.Text = string.Join(Environment.NewLine, matches);
        LogBox.CaretIndex = 0;
        CountLabel.Text = $"Строки · {matches.Length} из {_lines.Length}";
    }

    private void UpdateNavigation()
    {
        OpenButton.IsEnabled = !_busy;
        StartButton.IsEnabled = !_busy && _page is not null;
        PreviousButton.IsEnabled = !_busy && _previousOffsets.Count > 0;
        NextButton.IsEnabled = !_busy && _page is { HasMore: true };
        MiddleButton.IsEnabled = !_busy && _page is { FileLength: > 0 };
    }

    private void SetNotice(string? text)
    {
        NoticeLabel.Text = text;
        NoticeLabel.IsVisible = text is not null;
    }

    private void DisposeReader()
    {
        _file?.Dispose();
        _closing.Dispose();
    }

    private enum PageMove
    {
        Start,
        Previous,
        Next,
        Middle
    }
}
