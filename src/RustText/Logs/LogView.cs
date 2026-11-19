using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Win32.SafeHandles;
using RustText.Demo;

namespace RustText.Demo.Logs;

internal sealed record LogTabState(
    TextEncodingKind? Encoding = null,
    long Offset = 0,
    long HiddenOffset = 0,
    bool WordWrap = false,
    bool FollowEnd = false,
    bool AutoRefresh = true,
    bool IndexPaused = false,
    bool SearchVisible = false,
    bool Regex = false,
    bool MatchCase = false,
    bool WholeWord = false,
    string SearchText = "",
    LogFileIdentity? Identity = null,
    long Size = 0,
    LogFingerprint? Fingerprint = null,
    bool? IndexOptIn = null);

internal sealed record LogFileIdentity(ulong Volume, int[] Id);
internal sealed record LogSystemTime(long SecsSinceEpoch, uint NanosSinceEpoch);
internal sealed record LogFingerprint(LogSystemTime? Modified, int[] Digest);

internal sealed class LogView : UserControl, IDisposable
{
    private const int HistoryLimit = 100;
    private const int SearchResultsPerPage = 32;
    private const int MaximumSearchLineBytes = 1024 * 1024;
    private const int IndexBatchBytes = 16 * 1024 * 1024;
    private const long LargeLogIndexOptInBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan SearchRegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly TextEncodingKind? _encodingOverride;
    private FileStream _file;
    private LogDataReader _reader;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<long> _previousOffsets = [];
    private readonly ObservableCollection<SearchResult> _searchResults = [];
    private readonly ObservableCollection<MiniSample> _miniSamples = [];
    private readonly TextBlock _fileLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _logText = new()
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
        CaretBrush = FocusTheme.Accent,
        SelectionBrush = FocusTheme.Selection,
        SelectionForegroundBrush = FocusTheme.Text,
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly TextBox _percentBox = new()
    {
        Width = 76,
        PlaceholderText = "%",
        Text = "0",
        Background = FocusTheme.Field,
        Foreground = FocusTheme.Text,
        BorderBrush = FocusTheme.Border,
    };
    private readonly TextBox _searchBox = new() { PlaceholderText = "Текст или регулярное выражение" };
    private readonly CheckBox _regexBox = new() { Content = "Регулярное выражение" };
    private readonly CheckBox _caseBox = new() { Content = "Учитывать регистр" };
    private readonly CheckBox _wholeWordBox = new() { Content = "Целое слово" };
    private readonly Button _pauseButton = new();
    private readonly Button _followButton = new();
    private readonly Button _indexButton = new();
    private readonly Button _searchButton = new() { Content = "Найти" };
    private readonly Button _moreButton = new() { Content = "Ещё" };
    private readonly Button _cancelSearchButton = new() { Content = "Отменить поиск" };
    private readonly Button _cancelIndexButton = new() { Content = "Остановить индекс" };
    private readonly Button _wrapButton = new();
    private readonly Button _hideButton = new() { Content = "Скрыть начало" };
    private readonly Button _revealButton = new() { Content = "Показать весь файл" };
    private readonly Button _trimButton = new() { Content = "Удалить начало…" };
    private readonly Button _cancelTrimButton = new() { Content = "Отменить обрезку", IsVisible = false };
    private readonly Button _searchCloseButton = new() { Content = "Закрыть поиск" };
    private readonly Button _timeButton = new() { Content = "Ко времени" };
    private readonly Button _previousButton = new() { Content = "Назад" };
    private readonly Button _nextButton = new() { Content = "Дальше" };
    private readonly Button _endButton = new() { Content = "Конец" };
    private readonly Button _middleButton = new() { Content = "50%" };
    private readonly Button _startButton = new() { Content = "Начало" };
    private readonly Button _openSearchButton = new() { Content = "Поиск в логе" };
    private readonly TextBlock _positionLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _statusLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _countLabel = new();
    private readonly TextBlock _lineNumberLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _lineGutterText = new()
    {
        FontFamily = new FontFamily("Consolas, monospace"),
        FontSize = FocusTheme.EditorFontSize,
        TextAlignment = TextAlignment.Right,
        TextWrapping = TextWrapping.NoWrap,
        Foreground = FocusTheme.Gutter,
    };
    private readonly ScrollViewer _lineGutterScrollViewer = new()
    {
        Width = 64,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        IsHitTestVisible = false,
    };
    private ScrollViewer? _logScrollViewer;
    private readonly TextBlock _indexLabel = new();
    private readonly ListBox _minimapList = new()
    {
        Width = 52,
        Background = FocusTheme.Hover,
        Foreground = FocusTheme.Muted,
        BorderBrush = FocusTheme.Border,
    };
    private readonly StackPanel _searchPanel = new()
    {
        Spacing = 7,
        Margin = new Thickness(0, 5, 0, 0),
        Background = FocusTheme.Panel,
        IsVisible = false,
    };
    private readonly StackPanel _searchResultButtons = new() { Spacing = 2 };
    private readonly TextBlock _liveLabel = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12,
    };

    private readonly List<IndexEntry> _lineIndex = [];
    private readonly HashSet<long> _skippedSearchLines = [];
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _trimCancellation;
    private LogPage? _page;
    private FileStamp _fileStamp;
    private byte[]? _observedDigest;
    private long _hiddenOffset;
    private long _searchOffset;
    private long _indexedTo;
    private long _indexedLineCount;
    private long _trimGeneration;
    private bool _wordWrap;
    private bool _followEnd;
    private bool _autoRefresh;
    private bool _indexPaused;
    private bool _indexOptIn;
    private bool _busy;
    private bool _closed;
    private bool _firstFrameReported;
    private bool _searchHasMore;
    private bool _indexBuilding;
    private bool _minimapLoading;
    private bool _minimapDragging;
    private double? _pendingMinimapRatio;
    private string _lastSearchText = string.Empty;
    private long _dataGeneration;
    private int[] _displayRowStarts = [];
    private long?[] _displaySourceLineNumbers = [];

    public LogView(string path, TextEncodingKind? encoding = null, LogTabState? state = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _encodingOverride = encoding ?? state?.Encoding;
        _file = LogDataReader.Open(_path);
        var effectiveEncoding = LogDataReader.DetectEncoding(_file, _encodingOverride);
        _reader = new LogDataReader(_file, effectiveEncoding);
        _fileStamp = FileStamp.Read(_path, _file);
        _observedDigest = TryReadObservedDigest(_fileStamp.Length);
        var bookmarkRestorable = CanRestoreBookmark(state);
        var restoreState = bookmarkRestorable ? state : state is null ? null : state with { Offset = 0, HiddenOffset = 0 };
        var savedHiddenOffset = Math.Clamp(restoreState?.HiddenOffset ?? 0, 0, _file.Length);
        _hiddenOffset = savedHiddenOffset == _file.Length
            ? savedHiddenOffset
            : _reader.AlignToNextLine(savedHiddenOffset);
        _wordWrap = state?.WordWrap ?? false;
        _followEnd = restoreState?.FollowEnd ?? false;
        _autoRefresh = restoreState?.AutoRefresh ?? true;
        _indexOptIn = restoreState?.IndexOptIn == true;
        _indexPaused = restoreState?.IndexPaused == true
            || (_file.Length >= LargeLogIndexOptInBytes && !_indexOptIn);
        _searchBox.Text = restoreState?.SearchText ?? string.Empty;
        _regexBox.IsChecked = restoreState?.Regex ?? false;
        _caseBox.IsChecked = restoreState?.MatchCase ?? false;
        _wholeWordBox.IsChecked = restoreState?.WholeWord ?? false;
        _searchPanel.IsVisible = restoreState?.SearchVisible ?? false;
        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += OnRefreshTimerTick;

        Content = BuildLayout();
        ScrollViewer.SetHorizontalScrollBarVisibility(_logText, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_logText, ScrollBarVisibility.Auto);
        _fileLabel.Text = System.IO.Path.GetFileName(_path);
        ToolTip.SetTip(_fileLabel, _path);
        ConfigureInteractions();
        _logText.LayoutUpdated += OnLogTextLayoutUpdated;
        RefreshButtons();

        _ = LoadPageAsync(Math.Max(_hiddenOffset, restoreState?.Offset ?? _hiddenOffset), saveHistory: false, allowFollowEnd: bookmarkRestorable);
        LoadMinimapSamples();
        StartIndexIfNeeded();
        if (_autoRefresh)
        {
            _refreshTimer.Start();
        }
    }

    public event Action<string>? StatusChanged;

    public string Path => _path;
    public bool IsReadOnly => true;
    public string StatusText { get; private set; } = string.Empty;

    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            _wordWrap = value;
            _logText.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _wrapButton.Content = value ? "Перенос: вкл." : "Перенос: выкл.";
        }
    }

    public LogTabState CaptureState()
    {
        var (identity, size, fingerprint) = CaptureBookmarkProof();
        return new LogTabState(
            _encodingOverride,
            _page?.StartOffset ?? _hiddenOffset,
            _hiddenOffset,
            _wordWrap,
            _followEnd,
            _autoRefresh,
            _indexPaused,
            _searchPanel.IsVisible,
            _regexBox.IsChecked == true,
            _caseBox.IsChecked == true,
            _wholeWordBox.IsChecked == true,
            _searchBox.Text ?? string.Empty,
            identity,
            size,
            fingerprint,
            _indexOptIn);
    }

    private bool CanRestoreBookmark(LogTabState? state)
    {
        if (state is null)
        {
            return true;
        }

        var atStart = state.Offset == 0 && state.HiddenOffset == 0;
        if (state.Identity is null)
        {
            return atStart;
        }

        var currentIdentity = ReadFileIdentity(_file.SafeFileHandle);
        if (state.Size < 0 || currentIdentity is null || !IdentityEquals(state.Identity, currentIdentity) || _file.Length < state.Size)
        {
            return false;
        }
        if (state.Fingerprint is null)
        {
            return atStart;
        }

        if (_file.Length == state.Size && state.Fingerprint.Modified is { } modified
            && GetFileModifiedTime(_file.SafeFileHandle) is { } currentModified
            && (modified.SecsSinceEpoch != currentModified.SecsSinceEpoch
                || modified.NanosSinceEpoch != currentModified.NanosSinceEpoch))
        {
            return false;
        }

        try
        {
            var digest = _reader.ComputeBookmarkDigest(state.Size).Select(value => (int)value);
            return digest.SequenceEqual(state.Fingerprint.Digest);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private (LogFileIdentity? Identity, long Size, LogFingerprint? Fingerprint) CaptureBookmarkProof()
    {
        try
        {
            var size = _file.Length;
            var modified = GetFileModifiedTime(_file.SafeFileHandle);
            var digest = _reader.ComputeBookmarkDigest(size).Select(value => (int)value).ToArray();
            return (ReadFileIdentity(_file.SafeFileHandle), size, new LogFingerprint(modified, digest));
        }
        catch (IOException)
        {
            return (ReadFileIdentity(_file.SafeFileHandle), _file.Length, null);
        }
    }

    private static bool IdentityEquals(LogFileIdentity expected, LogFileIdentity actual) =>
        expected.Volume == actual.Volume && expected.Id.AsSpan().SequenceEqual(actual.Id);

    private static LogFileIdentity? ReadFileIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var native = default(NativeFileIdInfo);
        if (!GetFileInformationByHandleEx(handle, 18, ref native, (uint)Marshal.SizeOf<NativeFileIdInfo>()))
        {
            return null;
        }

        var id = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(0, 8), native.Id.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8, 8), native.Id.High);
        return new LogFileIdentity(native.Volume, id.Select(value => (int)value).ToArray());
    }

    private static LogSystemTime? GetFileModifiedTime(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            return null;
        }

        var modifiedUtc = ToDateTime(information.LastWriteTime);
        var ticks = modifiedUtc.Ticks - DateTime.UnixEpoch.Ticks;
        if (ticks < 0)
        {
            return null;
        }
        var seconds = ticks / TimeSpan.TicksPerSecond;
        var nanos = (uint)(ticks % TimeSpan.TicksPerSecond * 100);
        return new LogSystemTime(seconds, nanos);
    }

    public void SetTextSize(double value)
    {
        _logText.FontSize = value;
        _lineGutterText.FontSize = value;
    }

    private int _activeSearchResult = -1;

    public async Task NavigateMatchAsync(bool backward)
    {
        if (string.IsNullOrWhiteSpace(_searchBox.Text))
        {
            FocusSearch();
            return;
        }

        if (_searchResults.Count == 0)
        {
            await SearchAsync(false);
        }
        else if (!backward && _activeSearchResult >= _searchResults.Count - 1 && _searchHasMore)
        {
            await SearchAsync(true);
        }

        if (_searchResults.Count > 0)
        {
            var index = backward
                ? (_activeSearchResult <= 0 ? _searchResults.Count - 1 : _activeSearchResult - 1)
                : (_activeSearchResult + 1) % _searchResults.Count;
            await OpenSearchHitAsync(_searchResults[index]);
        }
    }

    private async Task OpenSearchHitAsync(SearchResult hit)
    {
        _activeSearchResult = _searchResults.IndexOf(hit);
        var end = hit.MatchIndex + hit.MatchLength;
        if (_page?.Rows.FirstOrDefault()?.StartOffset != hit.Offset)
        {
            await LoadPageAsync(hit.Offset, saveHistory: true, allowFollowEnd: false);
        }

        if (_page?.Rows.FirstOrDefault()?.StartOffset == hit.Offset && end > FirstDisplayedLineLength())
        {
            await LoadPageAsync(hit.Offset, saveHistory: false, allowFollowEnd: false,
                maxPageBytes: MaximumSearchLineBytes);
        }

        if (_page?.Rows.FirstOrDefault()?.StartOffset == hit.Offset && hit.MatchLength > 0
            && hit.MatchIndex >= 0 && end <= FirstDisplayedLineLength())
        {
            _logText.CaretIndex = end;
            _logText.SelectionStart = hit.MatchIndex;
            _logText.SelectionEnd = end;
        }
    }

    private int FirstDisplayedLineLength()
    {
        var rows = _page!.Rows;
        var length = rows[0].Text.Length;
        for (var index = 1; index < rows.Count && rows[index].ContinuesLine; index++)
        {
            length += rows[index].Text.Length;
        }
        return length;
    }

    public void FocusSearch()
    {
        _searchPanel.IsVisible = true;
        _searchBox.Focus();
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _closing.Cancel();
        _searchCancellation?.Cancel();
        _indexCancellation?.Cancel();
        _trimCancellation?.Cancel();
        _refreshTimer.Stop();
        _file.Dispose();
        _closing.Dispose();
    }

    public void Dispose() => Close();

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Background = FocusTheme.Editor,
        };

        _fileLabel.FontSize = 12;
        _fileLabel.FontWeight = FontWeight.Medium;
        _fileLabel.MinWidth = 0;
        _fileLabel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _fileLabel.VerticalAlignment = VerticalAlignment.Center;
        _fileLabel.Foreground = FocusTheme.Text;
        var readOnlyLabel = new TextBlock
        {
            Text = "ТОЛЬКО ЧТЕНИЕ",
            FontSize = 11,
            Foreground = FocusTheme.Muted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleGroup = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            MinWidth = 0,
        };
        titleGroup.Children.Add(_fileLabel);
        Grid.SetColumn(readOnlyLabel, 1);
        titleGroup.Children.Add(readOnlyLabel);

        SetButtonStyle(_pauseButton, "focus-quiet");
        SetButtonStyle(_followButton, "focus-quiet");
        var contextActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 5,
        };
        contextActions.Children.Add(_pauseButton);
        contextActions.Children.Add(_followButton);
        contextActions.Children.Add(_liveLabel);

        var contextGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
        };
        contextGrid.Children.Add(titleGroup);
        Grid.SetColumn(contextActions, 1);
        contextGrid.Children.Add(contextActions);
        root.Children.Add(new Border
        {
            Padding = new Thickness(10, 5),
            Background = FocusTheme.Chrome,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = contextGrid,
        });

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 0,
            Background = FocusTheme.Editor,
        };
        Grid.SetRow(content, 1);
        _lineGutterScrollViewer.Content = new Border
        {
            Padding = new Thickness(0, 6),
            Background = FocusTheme.Gutter,
            Child = _lineGutterText,
        };
        AutomationProperties.SetName(_lineGutterScrollViewer, "Номера строк лога");
        AutomationProperties.SetAutomationId(_lineGutterScrollViewer, "LogLineNumbers");
        Grid.SetColumn(_lineGutterScrollViewer, 0);
        content.Children.Add(_lineGutterScrollViewer);
        Grid.SetColumn(_logText, 1);
        content.Children.Add(_logText);
        _minimapList.ItemsSource = _miniSamples;
        AutomationProperties.SetName(_minimapList, "Прокрутка лога");
        AutomationProperties.SetAutomationId(_minimapList, "LogMinimap");
        _minimapList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MiniSample>((sample, _) =>
        {
            if (sample is null)
            {
                return null;
            }

            var button = new Button
            {
                Height = 5,
                MinHeight = 5,
                MaxHeight = 5,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new Border
                {
                    Width = Math.Clamp(sample.Preview.Length * 1.35, 6, 46),
                    Height = 2,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = FocusTheme.Muted,
                },
            };
            SetButtonStyle(button, "focus-quiet");
            button.Margin = new Thickness(0);
            ToolTip.SetTip(button, $"{sample.Offset:N0} байт · {sample.Preview}");
            button.Click += async (_, _) => await NavigateAsync(sample.Offset, true);
            return button;
        });
        Grid.SetColumn(_minimapList, 2);
        content.Children.Add(_minimapList);
        root.Children.Add(content);

        var searchRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 4,
        };
        _searchBox.MinWidth = 180;
        _searchBox.FontSize = 12;
        _searchBox.Background = FocusTheme.Field;
        _searchBox.Foreground = FocusTheme.Text;
        _searchBox.BorderBrush = FocusTheme.Border;
        _percentBox.FontSize = 12;
        _regexBox.FontSize = 12;
        _caseBox.FontSize = 12;
        _wholeWordBox.FontSize = 12;
        AutomationProperties.SetName(_regexBox, "Регулярное выражение");
        ToolTip.SetTip(_regexBox, "Регулярное выражение");
        AutomationProperties.SetName(_caseBox, "Учитывать регистр");
        ToolTip.SetTip(_caseBox, "Учитывать регистр");
        _regexBox.Foreground = FocusTheme.Muted;
        _caseBox.Foreground = FocusTheme.Muted;
        _wholeWordBox.Foreground = FocusTheme.Muted;
        searchRow.Children.Add(_searchBox);
        searchRow.Children.Add(_regexBox);
        searchRow.Children.Add(_caseBox);
        searchRow.Children.Add(_wholeWordBox);
        SetButtonStyle(_searchButton, "focus-primary");
        SetButtonStyle(_moreButton, "focus-quiet");
        SetButtonStyle(_cancelSearchButton, "focus-quiet");
        SetButtonStyle(_searchCloseButton, "focus-icon");
        AutomationProperties.SetName(_searchCloseButton, "Закрыть поиск");
        ToolTip.SetTip(_searchCloseButton, "Закрыть поиск");
        searchRow.Children.Add(_searchButton);
        searchRow.Children.Add(_moreButton);
        searchRow.Children.Add(_cancelSearchButton);
        searchRow.Children.Add(_searchCloseButton);
        _searchPanel.Children.Add(searchRow);
        var searchResults = new ScrollViewer
        {
            MaxHeight = 48,
            Content = _searchResultButtons,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.SizeChanged += (_, e) => searchResults.MaxHeight = Math.Clamp(e.NewSize.Height / 8, 32, 48);
        _searchPanel.Children.Add(searchResults);
        Grid.SetRow(_searchPanel, 2);
        root.Children.Add(_searchPanel);

        SetNavigationIcon(_startButton, "В начало лога");
        SetNavigationIcon(_previousButton, "Предыдущая страница");
        SetNavigationIcon(_nextButton, "Следующая страница");
        SetNavigationIcon(_endButton, "В конец лога");
        SetButtonStyle(_middleButton, "focus-quiet");
        SetButtonStyle(_timeButton, "focus-quiet");
        var percentButton = new Button { Content = "Перейти, %" };
        SetButtonStyle(percentButton, "focus-quiet");
        percentButton.Click += async (_, _) => await JumpByPercentageAsync();

        var navigation = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2),
            ItemSpacing = 4,
        };
        navigation.Children.Add(_startButton);
        navigation.Children.Add(_previousButton);
        navigation.Children.Add(_nextButton);
        navigation.Children.Add(_endButton);
        navigation.Children.Add(_middleButton);
        navigation.Children.Add(_percentBox);
        navigation.Children.Add(percentButton);
        navigation.Children.Add(_timeButton);

        SetButtonStyle(_indexButton, "focus-quiet");
        SetButtonStyle(_cancelIndexButton, "focus-quiet");
        SetButtonStyle(_openSearchButton, "focus-quiet");
        SetButtonStyle(_wrapButton, "focus-quiet");
        SetButtonStyle(_hideButton, "focus-quiet");
        SetButtonStyle(_revealButton, "focus-quiet");
        SetButtonStyle(_trimButton, "focus-danger");
        SetButtonStyle(_cancelTrimButton, "focus-quiet");
        var fileActions = new StackPanel { Spacing = 2 };
        var actionsFlyout = new Flyout { Content = fileActions, Placement = PlacementMode.Top };
        foreach (var button in new[] { _indexButton, _wrapButton, _hideButton, _revealButton, _trimButton })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Click += (_, _) => actionsFlyout.Hide();
            fileActions.Children.Add(button);
        }
        var actions = new Button { Content = "Действия ▾", Flyout = actionsFlyout };
        SetButtonStyle(actions, "focus-quiet");
        navigation.Children.Add(_openSearchButton);
        navigation.Children.Add(actions);
        navigation.Children.Add(_cancelIndexButton);
        navigation.Children.Add(_cancelTrimButton);

        _countLabel.FontSize = 11;
        _countLabel.Foreground = FocusTheme.Muted;
        _lineNumberLabel.FontSize = 11;
        _lineNumberLabel.Foreground = FocusTheme.Muted;
        _lineNumberLabel.MaxWidth = 110;
        _indexLabel.FontSize = 11;
        _indexLabel.Foreground = FocusTheme.Muted;
        _indexLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _indexLabel.MaxWidth = 160;
        _positionLabel.FontSize = 11;
        _positionLabel.Foreground = FocusTheme.Muted;
        _positionLabel.MaxWidth = 190;
        var footerContent = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(8, 3),
            Background = FocusTheme.Chrome,
        };
        footerContent.Children.Add(navigation);
        var statusRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 3, 0, 0),
        };
        statusRow.Children.Add(_countLabel);
        Grid.SetColumn(_lineNumberLabel, 1);
        statusRow.Children.Add(_lineNumberLabel);
        Grid.SetColumn(_indexLabel, 2);
        statusRow.Children.Add(_indexLabel);
        Grid.SetColumn(_positionLabel, 3);
        statusRow.Children.Add(_positionLabel);
        footerContent.Children.Add(statusRow);
        var footer = new Border
        {
            Background = FocusTheme.Chrome,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerContent,
        };
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private static void SetButtonStyle(Button button, string styleClass)
    {
        button.Classes.Add(styleClass);
        button.FontSize = 12;
        button.Margin = new Thickness(0, 0, 4, 3);
    }

    private static void SetNavigationIcon(Button button, string name)
    {
        SetButtonStyle(button, "focus-icon");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
    }

    private void ConfigureInteractions()
    {
        _startButton.Click += async (_, _) => await NavigateAsync(_hiddenOffset, true);
        _previousButton.Click += async (_, _) => await NavigatePreviousAsync();
        _nextButton.Click += async (_, _) => await NavigateAsync(_page?.NextOffset ?? _hiddenOffset, true);
        _endButton.Click += async (_, _) => await NavigateAsync(Math.Max(_hiddenOffset, _reader.GetEndOffset(_closing.Token)), true);
        _middleButton.Click += async (_, _) => await NavigateAsync(Math.Max(_hiddenOffset, _reader.GetMidpointOffset(_closing.Token)), true);
        _pauseButton.Click += (_, _) => SetAutoRefresh(!_autoRefresh);
        _followButton.Click += async (_, _) =>
        {
            _followEnd = !_followEnd;
            if (_followEnd && _autoRefresh)
            {
                await NavigateAsync(Math.Max(_hiddenOffset, _reader.GetEndOffset(_closing.Token)), true);
            }
            RefreshButtons();
        };
        _indexButton.Click += (_, _) => ToggleIndexing();
        _cancelIndexButton.Click += (_, _) => _indexCancellation?.Cancel();
        _openSearchButton.Click += (_, _) => FocusSearch();
        _wrapButton.Click += (_, _) => WordWrap = !WordWrap;
        _hideButton.Click += async (_, _) => await HideBeginningAsync();
        _revealButton.Click += async (_, _) =>
        {
            _hiddenOffset = 0;
            _previousOffsets.Clear();
            await NavigateAsync(0, false);
        };
        _trimButton.Click += async (_, _) => await TrimBeginningAsync();
        _cancelTrimButton.Click += (_, _) => _trimCancellation?.Cancel();
        _searchButton.Click += async (_, _) => await SearchAsync(false);
        _moreButton.Click += async (_, _) => await SearchAsync(true);
        _cancelSearchButton.Click += (_, _) => _searchCancellation?.Cancel();
        _searchCloseButton.Click += (_, _) => _searchPanel.IsVisible = false;
        _timeButton.Click += async (_, _) => await JumpToTimeAsync();
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                await SearchAsync(false);
                e.Handled = true;
            }
        };
        _logText.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Home && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                await NavigateAsync(_hiddenOffset, true);
                e.Handled = true;
            }
            else if (e.Key == Key.End && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                await NavigateAsync(Math.Max(_hiddenOffset, _reader.GetEndOffset(_closing.Token)), true);
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown)
            {
                await NavigateAsync(_page?.NextOffset ?? _hiddenOffset, true);
                e.Handled = true;
            }
            else if (e.Key == Key.PageUp)
            {
                await NavigatePreviousAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                FocusSearch();
                e.Handled = true;
            }
        };
        _minimapList.SelectionChanged += (_, _) => _minimapList.SelectedItem = null;
        _minimapList.AddHandler(InputElement.PointerPressedEvent, OnMinimapPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _minimapList.PointerMoved += OnMinimapPointerMoved;
        _minimapList.PointerReleased += OnMinimapPointerReleased;
        _minimapList.AddHandler(InputElement.PointerWheelChangedEvent, OnMinimapPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        WordWrap = _wordWrap;
    }

    private void OnMinimapPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_minimapList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _minimapDragging = true;
        e.Pointer.Capture(_minimapList);
        _ = NavigateMinimapToRatioAsync(MinimapRatioAt(e.GetPosition(_minimapList).Y));
        e.Handled = true;
    }

    private void OnMinimapPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_minimapDragging)
        {
            return;
        }

        _ = NavigateMinimapToRatioAsync(MinimapRatioAt(e.GetPosition(_minimapList).Y));
        e.Handled = true;
    }

    private void OnMinimapPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_minimapDragging)
        {
            return;
        }

        _minimapDragging = false;
        e.Pointer.Capture(null);
        _ = NavigateMinimapToRatioAsync(MinimapRatioAt(e.GetPosition(_minimapList).Y));
        e.Handled = true;
    }

    private void OnMinimapPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_page is not { } page || e.Delta.Y == 0)
        {
            return;
        }

        var pageBytes = Math.Max(1, page.NextOffset - page.StartOffset);
        var next = Math.Clamp(page.StartOffset - (long)Math.Sign(e.Delta.Y) * pageBytes, _hiddenOffset, _file.Length);
        var range = Math.Max(1, _file.Length - _hiddenOffset);
        _ = NavigateMinimapToRatioAsync((next - _hiddenOffset) / (double)range);
        e.Handled = true;
    }

    private async Task LoadPageAsync(long offset, bool saveHistory = true, bool allowFollowEnd = true,
        int maxPageBytes = LogDataReader.DefaultPageBytes)
    {
        if (_closed || _busy)
        {
            return;
        }

        _busy = true;
        var loadFollowEnd = false;
        RefreshButtons();
        try
        {
            var requested = Math.Clamp(offset, _hiddenOffset, _file.Length);
            var page = await Task.Run(() => _reader.ReadPage(requested, maxPageBytes: maxPageBytes,
                cancellationToken: _closing.Token), _closing.Token);
            if (_closed)
            {
                return;
            }

            if (saveHistory && _page is { } current && current.StartOffset != page.StartOffset)
            {
                if (_previousOffsets.Count == HistoryLimit)
                {
                    _previousOffsets.RemoveAt(0);
                }
                _previousOffsets.Add(current.StartOffset);
            }

            _page = page;
            var displayText = new StringBuilder();
            for (var index = 0; index < page.Rows.Count; index++)
            {
                var row = page.Rows[index];
                if (index > 0 && !row.ContinuesLine)
                {
                    displayText.Append(Environment.NewLine);
                }
                displayText.Append(row.Text);
            }
            _logText.Text = displayText.ToString();
            UpdatePageLineNumbers(page);
            _positionLabel.Text = $"Байты [{page.StartOffset:N0}, {page.NextOffset:N0}) / {page.FileLength:N0}";
            var firstLine = page.Rows.Count > 0 ? FindLineNumber(page.Rows[0].StartOffset) : FindLineNumber(page.StartOffset);
            var lastLine = page.Rows.Count > 0 ? FindLineNumber(page.Rows[^1].StartOffset) : firstLine;
            _lineNumberLabel.Text = firstLine is { } first && lastLine is { } last
                ? $"Строки {first:N0}–{last:N0}"
                : "Строки индексируются";
            _countLabel.Text = $"Записей на странице · {page.Rows.Count} · {_reader.Encoding}";
            SetStatus(page.WasTruncated
                ? "Файл изменился во время чтения. Позиция пересчитана по открытому файлу."
                : string.Empty);
            if (allowFollowEnd && _followEnd && page.HasMore)
            {
                loadFollowEnd = true;
            }
            if (!_firstFrameReported && page.Rows.Count > 0)
            {
                _firstFrameReported = true;
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => Program.MarkFirstLogFrame());
            }
            RefreshButtons();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Не удалось прочитать лог. {error.Message}");
        }
        finally
        {
            _busy = false;
            RefreshButtons();
            if (loadFollowEnd && !_closed)
            {
                _ = NavigateAsync(_reader.GetEndOffset(_closing.Token), saveHistory: false);
            }
            else if (_pendingMinimapRatio is { } ratio && !_closed)
            {
                _pendingMinimapRatio = null;
                _ = NavigateMinimapToRatioAsync(ratio);
            }
        }
    }

    private async Task NavigateMinimapToRatioAsync(double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        if (_busy)
        {
            _pendingMinimapRatio = ratio;
            return;
        }

        var length = _file.Length;
        var available = Math.Max(0, length - _hiddenOffset);
        var target = ratio >= 1
            ? _reader.GetEndOffset(_closing.Token)
            : _hiddenOffset + (long)(available * ratio);
        try
        {
            var aligned = await Task.Run(() => _reader.AlignToNextLine(target, _closing.Token), _closing.Token);
            await LoadPageAsync(Math.Max(_hiddenOffset, aligned), saveHistory: true, allowFollowEnd: false);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (IOException error)
        {
            SetStatus($"Переход по миникарте остановлен. {error.Message}");
        }
    }

    private double MinimapRatioAt(double y)
    {
        var height = _minimapList.Bounds.Height;
        return height <= 0 ? 0 : Math.Clamp(y / height, 0, 1);
    }

    private async Task NavigateAsync(long offset, bool saveHistory)
    {
        if (_page is { } current && offset == current.StartOffset)
        {
            return;
        }
        await LoadPageAsync(offset, saveHistory);
    }

    private async Task NavigatePreviousAsync()
    {
        if (_previousOffsets.Count == 0 || _busy)
        {
            return;
        }
        var offset = _previousOffsets[^1];
        _previousOffsets.RemoveAt(_previousOffsets.Count - 1);
        await LoadPageAsync(offset, false);
    }

    private async Task JumpByPercentageAsync()
    {
        if (!double.TryParse(_percentBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || percent is < 0 or > 100)
        {
            SetStatus("Введите процент от 0 до 100.");
            return;
        }
        var target = (long)Math.Clamp(_file.Length * percent / 100d, _hiddenOffset, _file.Length);
        try
        {
            var aligned = await Task.Run(() => _reader.AlignToNextLine(target, _closing.Token), _closing.Token);
            await NavigateAsync(Math.Max(_hiddenOffset, aligned), true);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (IOException error)
        {
            SetStatus($"Переход по проценту остановлен. {error.Message}");
        }
    }

    private async Task SearchAsync(bool continueSearch)
    {
        var query = _searchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            SetStatus("Введите текст для поиска.");
            return;
        }

        if (_busy)
        {
            return;
        }
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var token = _searchCancellation.Token;
        var generation = _dataGeneration;
        if (!continueSearch || !string.Equals(query, _lastSearchText, StringComparison.Ordinal))
        {
            _searchOffset = Math.Max(_hiddenOffset, _page?.StartOffset ?? _hiddenOffset);
            _searchResults.Clear();
            _searchResultButtons.Children.Clear();
            _activeSearchResult = -1;
        }
        _lastSearchText = query;
        var startOffset = _searchOffset;
        var regex = _regexBox.IsChecked == true;
        var caseSensitive = _caseBox.IsChecked == true;
        var wholeWord = _wholeWordBox.IsChecked == true;
        _searchButton.IsEnabled = false;
        SetStatus("Поиск в логе…");
        try
        {
            var result = await Task.Run(() => SearchFile(query, regex, caseSensitive, wholeWord, startOffset, token), token);
            if (_closed || token.IsCancellationRequested || generation != _dataGeneration)
            {
                return;
            }
            foreach (var hit in result.Results)
            {
                _searchResults.Add(hit);
                var button = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = FocusTheme.Panel,
                    Content = new TextBlock
                    {
                        Text = $"{hit.LineNumber?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} · {hit.Preview}",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = FocusTheme.Text,
                    }
                };
                SetButtonStyle(button, "focus-quiet");
                ToolTip.SetTip(button, $"Байт {hit.Offset:N0}");
                button.Click += async (_, _) => await OpenSearchHitAsync(hit);
                _searchResultButtons.Children.Add(button);
            }
            _searchOffset = result.NextOffset;
            _searchHasMore = result.HasMore;
            _moreButton.IsEnabled = _searchHasMore;
            SetStatus(result.Message ?? (result.Results.Count == 0 ? "Совпадений не найдено." : $"Найдено {result.Results.Count} совпадений. Нажмите «Ещё» для продолжения."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_closed)
            {
                SetStatus("Поиск отменён.");
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or RegexMatchTimeoutException)
        {
            SetStatus($"Поиск остановлен. {error.Message}");
        }
        finally
        {
            _searchButton.IsEnabled = true;
            _moreButton.IsEnabled = _searchHasMore;
        }
    }

    private SearchPage SearchFile(string query, bool useRegex, bool matchCase, bool wholeWord, long offset, CancellationToken token)
    {
        Regex? expression = null;
        if (useRegex)
        {
            var pattern = wholeWord ? $"\\b(?:{query})\\b" : query;
            expression = new Regex(pattern, RegexOptions.Compiled | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), SearchRegexTimeout);
        }

        var results = new List<SearchResult>(SearchResultsPerPage);
        var cursor = Math.Max(offset, _hiddenOffset);
        var pageEnd = _file.Length;
        var pendingLine = new StringBuilder();
        var pendingOffset = cursor;
        long? lineNumber = FindLineNumber(cursor);
        string? notice = null;
        var skippingOversizedLine = false;
        var nextOffset = cursor;

        while (cursor < pageEnd && results.Count < SearchResultsPerPage)
        {
            token.ThrowIfCancellationRequested();
            var page = _reader.ReadPage(cursor, pageSize: 80, maxFrameBytes: 16 * 1024, maxPageBytes: 128 * 1024, cancellationToken: token);
            if (page.Rows.Count == 0 || page.NextOffset <= cursor)
            {
                break;
            }

            foreach (var row in page.Rows)
            {
                token.ThrowIfCancellationRequested();
                var startsWithinLine = row.ContinuesLine && pendingLine.Length > 0 || IsContinuationAt(row.StartOffset);
                if (!startsWithinLine)
                {
                    var pendingText = pendingLine.ToString();
                    var match = !skippingOversizedLine && pendingLine.Length > 0
                        ? FindMatch(pendingText, query, expression, matchCase, wholeWord)
                        : null;
                    if (match is { } previousMatch)
                    {
                        results.Add(new SearchResult(pendingOffset, lineNumber, ClipText(pendingText), previousMatch.Index, previousMatch.Length));
                        if (results.Count >= SearchResultsPerPage)
                        {
                            nextOffset = row.StartOffset;
                            break;
                        }
                    }
                    pendingLine.Clear();
                    skippingOversizedLine = false;
                    pendingOffset = row.StartOffset;
                    lineNumber++;
                }

                if (skippingOversizedLine || row.EndOffset - pendingOffset > MaximumSearchLineBytes)
                {
                    pendingLine.Clear();
                    skippingOversizedLine = row.ContinuesLine;
                    notice = $"Пропущена строка длиннее лимита поиска в {MaximumSearchLineBytes:N0} символов.";
                    continue;
                }
                pendingLine.Append(row.Text);
                if (!row.ContinuesLine && !IsContinuationAt(row.EndOffset)
                    && FindMatch(pendingLine.ToString(), query, expression, matchCase, wholeWord) is { } currentMatch)
                {
                    results.Add(new SearchResult(pendingOffset, lineNumber, ClipText(pendingLine.ToString()), currentMatch.Index, currentMatch.Length));
                    pendingLine.Clear();
                    if (results.Count >= SearchResultsPerPage)
                    {
                        nextOffset = row.EndOffset;
                        break;
                    }
                }
                else if (!row.ContinuesLine && !IsContinuationAt(row.EndOffset))
                {
                    pendingLine.Clear();
                }
                nextOffset = row.EndOffset;
            }

            cursor = page.NextOffset;
            if (results.Count >= SearchResultsPerPage || !page.HasMore)
            {
                break;
            }
        }

        if (cursor >= pageEnd && !skippingOversizedLine && pendingLine.Length > 0
            && FindMatch(pendingLine.ToString(), query, expression, matchCase, wholeWord) is { } finalMatch)
        {
            results.Add(new SearchResult(pendingOffset, lineNumber, ClipText(pendingLine.ToString()), finalMatch.Index, finalMatch.Length));
        }

        var resultNextOffset = results.Count == SearchResultsPerPage ? nextOffset : cursor;
        return new SearchPage(results, resultNextOffset, resultNextOffset < _file.Length, notice);
    }

    private static MatchSpan? FindMatch(string text, string query, Regex? expression, bool matchCase, bool wholeWord)
    {
        if (expression is not null)
        {
            var match = expression.Match(text);
            return match.Success ? new MatchSpan(match.Index, match.Length) : null;
        }
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var found = text.IndexOf(query, comparison);
        while (found >= 0)
        {
            if (!wholeWord || IsWordBoundary(text, found - 1) && IsWordBoundary(text, found + query.Length))
            {
                return new MatchSpan(found, query.Length);
            }
            found = text.IndexOf(query, found + 1, comparison);
        }
        return null;
    }

    private bool IsContinuationAt(long offset)
    {
        if (offset <= _reader.PreambleLength)
        {
            return false;
        }
        return !_reader.IsNewlineBefore(offset);
    }

    private static bool IsWordBoundary(string text, int index) => index < 0 || index >= text.Length
        || !(char.IsLetterOrDigit(text[index]) || text[index] == '_');

    private async Task JumpToTimeAsync()
    {
        var input = _searchBox.Text?.Trim() ?? string.Empty;
        if (!TryParseRequestedTime(input, out var requested))
        {
            SetStatus("Введите дату и время либо время в формате ЧЧ:ММ:СС[.доли].");
            return;
        }
        if (_busy)
        {
            return;
        }

        var token = _closing.Token;
        SetStatus("Поиск записи по времени…");
        try
        {
            var offset = await Task.Run(() => FindTimeOffset(requested, token), token);
            if (offset is null)
            {
                SetStatus("Подходящая запись не найдена.");
                return;
            }
            await NavigateAsync(offset.Value, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            SetStatus($"Переход по времени остановлен. {error.Message}");
        }
    }

    private long? FindTimeOffset(DateTime requested, CancellationToken token)
    {
        var cursor = _hiddenOffset;
        long line = 0;
        while (cursor < _file.Length)
        {
            token.ThrowIfCancellationRequested();
            var page = _reader.ReadPage(cursor, cancellationToken: token);
            if (page.Rows.Count == 0 || page.NextOffset <= cursor)
            {
                break;
            }
            foreach (var row in page.Rows)
            {
                if (IsContinuationAt(row.StartOffset))
                {
                    continue;
                }
                line++;
                if (TryReadTimestamp(row.Text, out var timestamp) && timestamp >= requested)
                {
                    return row.StartOffset;
                }
            }
            cursor = page.NextOffset;
        }
        return null;
    }

    private static readonly Regex TimestampPattern = new(
        @"(?<date>\d{4}-\d{2}-\d{2})[ T](?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)|(?<clock>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(20));

    private static bool TryReadTimestamp(string line, out DateTime timestamp)
    {
        var match = TimestampPattern.Match(line);
        if (match.Success)
        {
            var value = match.Groups["date"].Success
                ? $"{match.Groups["date"].Value} {match.Groups["time"].Value}"
                : match.Groups["clock"].Value;
            var formats = match.Groups["date"].Success
                ? new[] { "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss" }
                : new[] { "HH:mm:ss.FFFFFFF", "HH:mm:ss" };
            if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
            {
                return true;
            }
        }
        timestamp = default;
        return false;
    }

    private async Task HideBeginningAsync()
    {
        if (_page is not { Rows.Count: > 0 } page)
        {
            return;
        }
        var firstLine = page.Rows.FirstOrDefault(row => !IsContinuationAt(row.StartOffset));
        if (firstLine is null)
        {
            SetStatus("Нельзя скрыть начало посреди длинной строки. Перейдите к следующей строке.");
            return;
        }
        var boundary = firstLine.StartOffset;
        _hiddenOffset = Math.Max(_hiddenOffset, boundary);
        _previousOffsets.Clear();
        if (_page.StartOffset < _hiddenOffset)
        {
            await NavigateAsync(_hiddenOffset, false);
        }
        RefreshButtons();
        SetStatus($"Просмотр и поиск начинаются с байта {_hiddenOffset:N0}. Начало файла не изменено.");
    }

    private async Task TrimBeginningAsync()
    {
        if (_hiddenOffset <= 0 || _busy)
        {
            return;
        }

        var boundary = _hiddenOffset;
        var expected = _fileStamp;
        var preambleLength = _reader.PreambleLength;
        var confirm = await ShowConfirmationAsync($"Файл будет обрезан физически до байта {boundary:N0}. Рядом будет создана резервная копия. Продолжить?");
        if (!confirm || _closed || _busy)
        {
            return;
        }

        _busy = true;
        RefreshButtons();
        _trimCancellation?.Dispose();
        _trimCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var token = _trimCancellation.Token;
        var generation = ++_trimGeneration;
        try
        {
            var backup = await Task.Run(() => TrimFile(_path, boundary, preambleLength, expected, token), token);
            if (_closed || generation != _trimGeneration)
            {
                return;
            }

            ResetFileHandle();
            SetStatus($"Начало удалено. Резервная копия: {backup}");
            _busy = false;
            await LoadPageAsync(0, saveHistory: false);
            StartIndexIfNeeded();
            LoadMinimapSamples();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_closed)
            {
                SetStatus("Обрезка отменена. Исходный файл не изменён.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Не удалось обрезать файл. {error.Message}");
        }
        finally
        {
            _trimCancellation?.Dispose();
            _trimCancellation = null;
            _busy = false;
            RefreshButtons();
        }
    }

    private async Task<bool> ShowConfirmationAsync(string message)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null)
        {
            return false;
        }

        var dialog = new Window
        {
            Title = "Подтверждение обрезки",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = FocusTheme.Panel,
            Foreground = FocusTheme.Text,
        };
        var result = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var accept = new Button { Content = "Удалить начало", MinWidth = 120 };
        var cancel = new Button { Content = "Отмена", MinWidth = 90 };
        SetButtonStyle(accept, "focus-danger");
        SetButtonStyle(cancel, "focus-quiet");
        accept.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        dialog.Content = new Border
        {
            Padding = new Thickness(18),
            Background = FocusTheme.Panel,
            BorderBrush = FocusTheme.Border,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 16,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = FocusTheme.Text }, buttons }
            }
        };
        await dialog.ShowDialog(window);
        return result;
    }

    private static string TrimFile(
        string path,
        long boundary,
        long preambleLength,
        FileStamp expected,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var directory = System.IO.Path.GetDirectoryName(path) ?? throw new IOException("Не удалось определить папку файла.");
        var fileName = System.IO.Path.GetFileName(path);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var backup = System.IO.Path.Combine(directory, $"{fileName}.backup-{stamp}");
        var temp = System.IO.Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.trim");
        var backupComplete = false;
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (boundary <= 0 || boundary < preambleLength || boundary > source.Length
                || source.Length != expected.Length || ReadFileStamp(path, source) != expected)
            {
                throw new IOException("Файл изменился до обрезки.");
            }

            using (var backupStream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
            {
                source.Position = 0;
                CopyWithCancellation(source, backupStream, token);
                backupStream.Flush(flushToDisk: true);
                backupComplete = true;
            }

            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
            {
                source.Position = boundary;
                if (preambleLength > 0)
                {
                    source.Position = 0;
                    var preamble = new byte[checked((int)preambleLength)];
                    source.ReadExactly(preamble);
                    target.Write(preamble);
                }
                source.Position = boundary;
                CopyWithCancellation(source, target, token);
                target.Flush(flushToDisk: true);
            }

            CommitTrim(path, temp, source, expected, token);
            return backup;
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
            if (!backupComplete && File.Exists(backup))
            {
                File.Delete(backup);
            }
            throw;
        }
    }

    private static void CommitTrim(string path, string staged, FileStream source, FileStamp expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ReadFileStamp(path, source) != expected)
        {
            throw new IOException("Файл изменился во время обрезки. Исходник оставлен без изменений, резервная копия сохранена.");
        }
        File.Replace(staged, path, destinationBackupFileName: null);
    }

    private static void CopyWithCancellation(Stream source, Stream target, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            target.Write(buffer, 0, read);
        }
    }

    private void SetAutoRefresh(bool enabled)
    {
        _autoRefresh = enabled;
        if (enabled)
        {
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
        RefreshButtons();
    }

    private void ResetFileHandle()
    {
        var replacement = LogDataReader.Open(_path);
        var encoding = LogDataReader.DetectEncoding(replacement, _encodingOverride);
        var reader = new LogDataReader(replacement, encoding);
        _dataGeneration++;
        _indexCancellation?.Cancel();
        _searchCancellation?.Cancel();
        var previous = _file;
        _file = replacement;
        _reader = reader;
        _fileStamp = FileStamp.Read(_path, replacement);
        _observedDigest = TryReadObservedDigest(_fileStamp.Length);
        previous.Dispose();

        _previousOffsets.Clear();
        _hiddenOffset = 0;
        _lineIndex.Clear();
        _indexedTo = 0;
        _indexedLineCount = 0;
        _indexLabel.Text = "";
        _skippedSearchLines.Clear();
        _searchResults.Clear();
        _searchResultButtons.Children.Clear();
        _searchOffset = 0;
        _searchHasMore = false;
        _activeSearchResult = -1;
        _lastSearchText = "";
        _logText.SelectionStart = _logText.SelectionEnd = 0;
    }

    private byte[]? TryReadObservedDigest(long size)
    {
        try
        {
            return _reader.ComputeBookmarkDigest(size);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_closed || _busy || !_autoRefresh)
        {
            return;
        }

        try
        {
            var now = FileStamp.Read(_path, _file);
            if (now == _fileStamp)
            {
                return;
            }

            var grew = now.Length > _fileStamp.Length;
            var previousContent = grew && now.Identity == _fileStamp.Identity
                ? TryReadObservedDigest(_fileStamp.Length) : null;
            var appended = _observedDigest is { } expected && previousContent is not null
                && expected.AsSpan().SequenceEqual(previousContent);
            if (appended)
            {
                _fileStamp = now;
                _observedDigest = TryReadObservedDigest(now.Length);
                var offset = _followEnd ? _reader.GetEndOffset(_closing.Token) : _page?.StartOffset ?? _hiddenOffset;
                await LoadPageAsync(offset, saveHistory: false);
            }
            else
            {
                ResetFileHandle();
                SetStatus("Файл был заменён, очищен или перезаписан. Позиция и индекс сброшены.");
                await LoadPageAsync(0, saveHistory: false);
            }
            ApplyLargeLogIndexOptInPolicy();
            StartIndexIfNeeded();
            LoadMinimapSamples();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Не удалось проверить лог. {error.Message}");
        }
    }

    private void ApplyLargeLogIndexOptInPolicy()
    {
        if (_file.Length < LargeLogIndexOptInBytes || _indexOptIn)
        {
            return;
        }

        _indexPaused = true;
        if (_indexBuilding)
        {
            _indexCancellation?.Cancel();
        }
        RefreshButtons();
    }

    private void ToggleIndexing()
    {
        if (_indexBuilding)
        {
            _indexPaused = true;
            _indexCancellation?.Cancel();
            RefreshButtons();
            return;
        }
        _indexPaused = !_indexPaused;
        if (!_indexPaused)
        {
            _indexOptIn = true;
            StartIndexIfNeeded();
        }
        RefreshButtons();
    }

    private void StartIndexIfNeeded()
    {
        if (_closed || _indexPaused || _indexBuilding || _indexedTo >= _file.Length)
        {
            return;
        }
        _indexCancellation?.Dispose();
        _indexCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var token = _indexCancellation.Token;
        _indexBuilding = true;
        var startOffset = _indexedTo;
        var startLine = _indexedLineCount;
        var generation = _dataGeneration;
        _ = Task.Run(() => BuildIndex(startOffset, startLine, token), token).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            _indexBuilding = false;
            if (_closed)
            {
                return;
            }
            if (generation != _dataGeneration)
            {
                if (!_indexPaused)
                {
                    StartIndexIfNeeded();
                }
                RefreshButtons();
                return;
            }
            if (task.Status == TaskStatus.RanToCompletion)
            {
                _indexedTo = task.Result.EndOffset;
                _indexedLineCount = task.Result.LineCount;
                _lineIndex.AddRange(task.Result.Entries);
                _indexLabel.Text = $"Индекс: {_indexedLineCount:N0} строк до {_indexedTo:N0} байт";
                if (_page is { } page)
                {
                    UpdatePageLineNumbers(page);
                }
                if (_indexedTo < _file.Length && !_indexPaused)
                {
                    StartIndexIfNeeded();
                }
            }
            else if (task.IsFaulted && task.Exception is { } error)
            {
                SetStatus($"Индексирование остановлено. {error.GetBaseException().Message}");
            }
            RefreshButtons();
        }), TaskScheduler.Default);
        RefreshButtons();
    }

    private IndexBuildResult BuildIndex(long startOffset, long startLineCount, CancellationToken token)
    {
        var entries = new List<IndexEntry>();
        var cursor = startOffset;
        var lineCount = startLineCount;
        var batchEnd = startOffset + Math.Min(IndexBatchBytes, _file.Length - startOffset);
        while (cursor < batchEnd)
        {
            token.ThrowIfCancellationRequested();
            var page = _reader.ReadPage(cursor, cancellationToken: token);
            if (page.Rows.Count == 0 || page.NextOffset <= cursor)
            {
                break;
            }
            foreach (var row in page.Rows)
            {
                if (IsContinuationAt(row.StartOffset))
                {
                    continue;
                }
                lineCount++;
                if (lineCount % 4096 == 0)
                {
                    entries.Add(new IndexEntry(row.StartOffset, lineCount));
                }
            }
            cursor = page.NextOffset;
        }
        return new IndexBuildResult(cursor, lineCount, entries);
    }

    private long? FindLineNumber(long offset)
    {
        if (offset < 0 || (offset > 128 * 1024 && offset > _indexedTo))
        {
            return null;
        }

        var entry = _lineIndex.LastOrDefault(value => value.Offset <= offset);
        if (entry is null && _hiddenOffset == 0 && offset <= 128 * 1024)
        {
            entry = new IndexEntry(_reader.PreambleLength, 1);
        }
        if (entry is null)
        {
            return null;
        }

        var lineNumber = entry.LineNumber;
        if (offset == entry.Offset)
        {
            return lineNumber;
        }
        var cursor = entry.Offset;
        // A sparse checkpoint may be far away in bytes when a log contains very long rows.
        // Keep this UI lookup bounded even when the background index has already passed offset.
        var scanLimit = Math.Min(offset, entry.Offset + 16L * 1024 * 1024);
        while (cursor < offset)
        {
            if (cursor >= scanLimit && offset > scanLimit)
            {
                return null;
            }
            var page = _reader.ReadPage(cursor, cancellationToken: _closing.Token);
            if (page.Rows.Count == 0 || page.NextOffset <= cursor)
            {
                break;
            }
            foreach (var row in page.Rows)
            {
                if (row.StartOffset > entry.Offset && row.StartOffset <= offset && !IsContinuationAt(row.StartOffset))
                {
                    lineNumber++;
                }
            }
            cursor = page.NextOffset;
        }
        if (cursor == offset && offset < _file.Length && !IsContinuationAt(offset))
        {
            lineNumber++;
        }
        return cursor >= offset ? lineNumber : null;
    }

    private void UpdatePageLineNumbers(LogPage page)
    {
        _displayRowStarts = new int[page.Rows.Count];
        _displaySourceLineNumbers = new long?[page.Rows.Count];
        var textOffset = 0;
        for (var index = 0; index < page.Rows.Count; index++)
        {
            _displayRowStarts[index] = textOffset;
            textOffset += page.Rows[index].Text.Length;
            if (index + 1 < page.Rows.Count && !page.Rows[index + 1].ContinuesLine)
            {
                textOffset += Environment.NewLine.Length;
            }
        }

        if (page.Rows.Count > 0)
        {
            var lineNumber = FindLineNumber(page.Rows[0].StartOffset);
            for (var index = 0; index < page.Rows.Count; index++)
            {
                var isContinuation = index > 0 && IsContinuationAt(page.Rows[index].StartOffset);
                if (index > 0 && !isContinuation && lineNumber.HasValue)
                {
                    lineNumber++;
                }
                _displaySourceLineNumbers[index] = isContinuation ? null : lineNumber;
            }
        }

        var firstLine = _displaySourceLineNumbers.FirstOrDefault(value => value.HasValue);
        var lastLine = _displaySourceLineNumbers.LastOrDefault(value => value.HasValue);
        _lineNumberLabel.Text = firstLine is { } first && lastLine is { } last
            ? $"Строки {first:N0}–{last:N0}"
            : "Строки индексируются";
        UpdateLineGutterText();
    }

    private void OnLogTextLayoutUpdated(object? sender, EventArgs e)
    {
        AttachLogScrollViewer();
        UpdateLineGutterText();
    }

    private void AttachLogScrollViewer()
    {
        var scrollViewer = _logText.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (ReferenceEquals(scrollViewer, _logScrollViewer))
        {
            return;
        }
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.ScrollChanged -= OnLogScrollChanged;
        }
        _logScrollViewer = scrollViewer;
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.ScrollChanged += OnLogScrollChanged;
            _lineGutterScrollViewer.Offset = new Vector(0, _logScrollViewer.Offset.Y);
        }
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_logScrollViewer is { } scrollViewer)
        {
            _lineGutterScrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y);
        }
    }

    private void UpdateLineGutterText()
    {
        if (_displaySourceLineNumbers.Length == 0)
        {
            _lineGutterText.Text = string.Empty;
            return;
        }

        var values = new List<string>();
        var layout = _logText.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()?.TextLayout;
        if (layout is not null && layout.TextLines.Count > 0)
        {
            var text = _logText.Text ?? string.Empty;
            var rowIndex = 0;
            foreach (var visualLine in layout.TextLines)
            {
                var start = Math.Clamp(visualLine.FirstTextSourceIndex, 0, text.Length);
                while (rowIndex + 1 < _displayRowStarts.Length && _displayRowStarts[rowIndex + 1] <= start)
                {
                    rowIndex++;
                }

                var startsLogicalRow = start == _displayRowStarts[rowIndex];
                values.Add(startsLogicalRow
                    ? _displaySourceLineNumbers[rowIndex]?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"
                    : string.Empty);
            }
        }
        else
        {
            values.AddRange(_displaySourceLineNumbers.Select(value => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"));
        }

        _lineGutterText.Text = string.Join(Environment.NewLine, values);
        if (_logScrollViewer is { } scrollViewer)
        {
            _lineGutterScrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y);
        }
    }

    private async void LoadMinimapSamples()
    {
        if (_closed || _file.Length == 0 || _minimapLoading)
        {
            return;
        }
        _minimapLoading = true;
        var length = _file.Length;
        var generation = _dataGeneration;
        try
        {
            var samples = await Task.Run(() => BuildMinimapSamples(length, _closing.Token), _closing.Token);
            if (_closed || generation != _dataGeneration)
            {
                return;
            }
            _miniSamples.Clear();
            foreach (var sample in samples)
            {
                _miniSamples.Add(sample);
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            if (!_closed)
            {
                SetStatus($"Миникарта не обновлена. {error.Message}");
            }
        }
        finally
        {
            _minimapLoading = false;
            if (!_closed && generation != _dataGeneration)
            {
                Dispatcher.UIThread.Post(LoadMinimapSamples);
            }
        }
    }

    private IReadOnlyList<MiniSample> BuildMinimapSamples(long fileLength, CancellationToken token)
    {
        var samples = new List<MiniSample>(64);
        for (var index = 0; index < 64; index++)
        {
            token.ThrowIfCancellationRequested();
            var offset = (long)((fileLength - 1) * (index / 63d));
            var pageOffset = offset > _hiddenOffset ? offset : _hiddenOffset;
            var page = _reader.ReadPage(pageOffset, pageSize: 1, maxFrameBytes: 256, maxPageBytes: 256, cancellationToken: token);
            var text = page.Rows.FirstOrDefault()?.Text ?? string.Empty;
            samples.Add(new MiniSample(page.Rows.FirstOrDefault()?.StartOffset ?? pageOffset, ClipText(text, 32)));
        }
        return samples;
    }

    private void RefreshButtons()
    {
        var enabled = !_busy && !_closed;
        _pauseButton.Content = _autoRefresh ? "Пауза" : "Продолжить";
        _followButton.Content = _followEnd ? "К концу" : "Следовать";
        _liveLabel.Text = _autoRefresh ? "● Обновляется" : "● На паузе";
        _liveLabel.Foreground = _autoRefresh ? FocusTheme.Success : FocusTheme.Muted;
        _followButton.Background = _followEnd ? FocusTheme.Hover : Brushes.Transparent;
        _followButton.Foreground = _followEnd ? FocusTheme.Accent : FocusTheme.Text;
        _indexButton.Background = _indexPaused ? FocusTheme.Hover : Brushes.Transparent;
        _indexButton.Foreground = _indexPaused ? FocusTheme.Accent : FocusTheme.Text;
        var largeIndexOptInRequired = _file.Length >= LargeLogIndexOptInBytes && !_indexOptIn;
        _indexButton.Content = _indexPaused
            ? largeIndexOptInRequired ? "Индексировать весь файл" : "Продолжить индекс"
            : "Индексирование";
        ToolTip.SetTip(_indexButton, _indexPaused && largeIndexOptInRequired
            ? "Большой файл не индексируется автоматически. Нажмите, чтобы начать полный проход."
            : "Построить индекс строк для навигации по файлу.");
        _wrapButton.Content = _wordWrap ? "Перенос: вкл." : "Перенос: выкл.";
        _wrapButton.Background = _wordWrap ? FocusTheme.Hover : Brushes.Transparent;
        _wrapButton.Foreground = _wordWrap ? FocusTheme.Accent : FocusTheme.Text;
        _previousButton.IsEnabled = enabled && _previousOffsets.Count > 0;
        _nextButton.IsEnabled = enabled && _page?.HasMore == true;
        _startButton.IsEnabled = enabled && _page is not null;
        _endButton.IsEnabled = enabled && _page is not null;
        _middleButton.IsEnabled = enabled && _file.Length > 0;
        _pauseButton.IsEnabled = !_closed;
        _followButton.IsEnabled = !_closed;
        _indexButton.IsEnabled = !_closed && (!_indexBuilding || _indexCancellation is not null);
        _cancelIndexButton.IsVisible = _indexBuilding;
        _cancelTrimButton.IsVisible = _trimCancellation is not null;
        _hideButton.IsEnabled = enabled && _page?.Rows.Count > 0;
        _revealButton.IsEnabled = enabled && _hiddenOffset > 0;
        _trimButton.IsEnabled = enabled && _hiddenOffset > 0;
        _searchButton.IsEnabled = !_closed;
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        _statusLabel.Text = text;
        _statusLabel.IsVisible = text.Length > 0;
        StatusChanged?.Invoke(text);
    }

    private static string ClipText(string value, int maxLength = 160) => value.Length <= maxLength
        ? value
        : string.Concat(value.AsSpan(0, maxLength - 1), "…");

    private bool TryParseRequestedTime(string text, out DateTime requested)
    {
        if (DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out requested))
        {
            return true;
        }
        if (DateTime.TryParseExact(text, ["HH:mm:ss.FFFFFFF", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var current = _page?.Rows.FirstOrDefault()?.Text;
            if (current is not null && TryReadTimestamp(current, out var currentTimestamp))
            {
                requested = currentTimestamp.Date + time.TimeOfDay;
            }
            else
            {
                requested = DateTime.Today + time.TimeOfDay;
            }
            return true;
        }
        requested = default;
        return false;
    }

    private static FileStamp ReadFileStamp(string path, FileStream file)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Open the path anew: the held reader can still refer to the old file after rotation.
                using var pathHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (GetFileInformationByHandle(pathHandle, out var information))
                {
                    var index = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
                    var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
                    var creation = ToDateTime(information.CreationTime);
                    var lastWrite = ToDateTime(information.LastWriteTime);
                    return new FileStamp(length, lastWrite, creation, true,
                        $"{information.VolumeSerialNumber:X8}:{index:X16}");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var info = new FileInfo(path);
        return new FileStamp(info.Exists ? info.Length : file.Length, info.LastWriteTimeUtc, info.CreationTimeUtc,
            info.Exists, $"fallback:{info.CreationTimeUtc.Ticks}:{info.Exists}");
    }

    private readonly record struct FileStamp(long Length, DateTime LastWriteUtc, DateTime CreationUtc, bool Exists, string Identity)
    {
        public static FileStamp Read(string path, FileStream file) => ReadFileStamp(path, file);
    }

    private static DateTime ToDateTime(Win32FileTime value) => DateTime.FromFileTimeUtc(((long)value.High << 32) | value.Low);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, ref NativeFileIdInfo information, uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileIdInfo
    {
        public ulong Volume;
        public FileId128 Id;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public Win32FileTime CreationTime;
        public Win32FileTime LastAccessTime;
        public Win32FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private readonly record struct MatchSpan(int Index, int Length);
    private sealed record SearchResult(long Offset, long? LineNumber, string Preview, int MatchIndex, int MatchLength);
    private sealed record SearchPage(IReadOnlyList<SearchResult> Results, long NextOffset, bool HasMore, string? Message);
    private sealed record IndexEntry(long Offset, long LineNumber);
    private sealed record IndexBuildResult(long EndOffset, long LineCount, IReadOnlyList<IndexEntry> Entries);
    private sealed record MiniSample(long Offset, string Preview);
}
