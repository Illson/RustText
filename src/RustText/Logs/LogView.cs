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
using RustText.Demo.Editor;
using RustText.Demo.Platform;

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
    bool? IndexOptIn = null,
    bool EditPages = false);

internal sealed record LogFileIdentity(ulong Volume, int[] Id);
internal sealed record LogSystemTime(long SecsSinceEpoch, uint NanosSinceEpoch);
internal sealed record LogFingerprint(LogSystemTime? Modified, int[] Digest);

internal sealed partial class LogView : UserControl, IDisposable
{
    private const int HistoryLimit = 100;
    private const int SearchResultsPerPage = 32;
    private const int MaximumSearchLineBytes = 1024 * 1024;
    private const int IndexBatchBytes = 16 * 1024 * 1024;
    private const long LargeLogIndexOptInBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan SearchRegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly bool _isDraft;
    private readonly long? _savedLineCount;
    private readonly TextEncodingKind? _encodingOverride;
    private FileStream _file;
    private LogDataReader _reader;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<long> _previousOffsets = [];
    private readonly ObservableCollection<SearchResult> _searchResults = [];
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
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly LogLevelBands _levelBands;
    private readonly Button _levelFilterButton = new() { Content = "Уровни…" };
    private readonly TextBox _percentBox = new()
    {
        Width = 76,
        FontSize = 12,
        PlaceholderText = "%",
        Text = "0",
        Background = FocusTheme.Field,
        Foreground = FocusTheme.Text,
        BorderBrush = FocusTheme.Border,
    };
    private readonly TextBox _searchBox = new() { PlaceholderText = "Текст или регулярное выражение" };
    private readonly ToggleButton _regexBox = SearchBar.Option(".*", "Регулярное выражение");
    private readonly ToggleButton _caseBox = SearchBar.Option("Aa", "Учитывать регистр");
    private readonly ToggleButton _wholeWordBox = SearchBar.Option("«»", "Целое слово");
    private readonly Button _pauseButton = new();
    private readonly Button _indexButton = new();
    private readonly Button _searchButton = new() { Content = "Найти" };
    private readonly Button _moreButton = new() { Content = "Ещё" };
    private Button _cancelSearchButton = null!;
    private readonly TextBlock _searchStatusText = SearchBar.Status();
    private readonly Button _cancelIndexButton = new() { Content = "Остановить индекс" };
    private readonly Button _wrapButton = new();
    private readonly Button _hideButton = new() { Content = "Скрыть начало" };
    private readonly Button _revealButton = new() { Content = "Показать весь файл" };
    private readonly Button _trimButton = new() { Content = "Удалить начало…" };
    private readonly Button _cancelTrimButton = new() { Content = "Отменить обрезку", IsVisible = false };
    private readonly Button _timeButton = new() { Content = "Ко времени" };
    private readonly Button _previousButton = new() { Content = "Назад" };
    private readonly Button _nextButton = new() { Content = "Дальше" };
    private readonly ToggleButton _endButton = new() { Content = "Конец" };
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
    private readonly LogMinimap _minimap = new() { Width = 54 };
    private readonly ViewportScrollBar _verticalLogScrollbar = new();
    private Grid? _contentViewport;
    private Grid? _levelFilterViewport;
    private readonly StackPanel _searchPanel = new()
    {
        Spacing = 2,
        Background = FocusTheme.Panel,
        IsVisible = false,
    };
    private readonly StackPanel _searchResultButtons = new() { Spacing = 2 };
    private readonly TextBlock _liveLabel = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12,
    };

    private readonly List<LogLineCheckpoint> _lineIndex = [];
    private readonly object _lineIndexGate = new();
    private readonly HashSet<long> _skippedSearchLines = [];
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _trimCancellation;
    private LogPage? _page;
    private FileStamp _fileStamp;
    private byte[]? _observedDigest;
    private LogFileIdentity? _bookmarkIdentity;
    private LogFingerprint? _bookmarkFingerprint;
    private long _hiddenOffset;
    private long _searchOffset;
    private long _indexedTo;
    private long _indexedLineCount;
    private long? _totalLineCount;
    private long _trimGeneration;
    private bool _wordWrap;
    private bool FollowEnd => _endButton.IsChecked == true;
    private enum PagePosition { Start, End, Preserve, FollowEnd }
    private bool _autoRefresh;
    private string? _consoleDisconnectMessage;
    private bool _indexPaused;
    private bool _indexOptIn;
    private bool _busy;
    private bool _closed;
    private bool _firstFrameReported;
    private bool _searchHasMore;
    private bool _indexBuilding;
    private bool _minimapLoading;
    private bool _minimapRefreshPending;
    private bool _minimapDragging;
    private double? _pendingMinimapRatio;
    private long _minimapRequest;
    private string _lastSearchText = string.Empty;
    private long _dataGeneration;
    private int[] _displayRowStarts = [];
    private long?[] _displaySourceLineNumbers = [];

    private readonly ConsoleCapture? _console;
    internal bool IsConsole => _console is not null;

    public LogView(string path, TextEncodingKind? encoding = null, LogTabState? state = null,
        string? draftName = null, long? draftLineCount = null, ConsoleCapture? console = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _console = console;
        _isDraft = draftName is not null;
        EditsPagesByDefault = state?.EditPages ?? _isDraft;
        _savedLineCount = draftLineCount;
        _encodingOverride = encoding ?? state?.Encoding;
        _file = LogDataReader.Open(_path);
        var effectiveEncoding = LogDataReader.DetectEncoding(_file, _encodingOverride);
        _reader = new LogDataReader(_file, effectiveEncoding);
        _fileStamp = FileStamp.Read(_path, _file);
        RefreshBookmarkProof();
        var bookmarkRestorable = _isDraft || CanRestoreBookmark(state);
        var restoreState = bookmarkRestorable ? state : state is null ? null : state with { Offset = 0, HiddenOffset = 0 };
        var savedHiddenOffset = Math.Clamp(restoreState?.HiddenOffset ?? 0, 0, _file.Length);
        _hiddenOffset = savedHiddenOffset == _file.Length
            ? savedHiddenOffset
            : _reader.AlignToNextLine(savedHiddenOffset);
        _wordWrap = state?.WordWrap ?? false;
        _endButton.IsChecked = !EditsPagesByDefault && restoreState?.FollowEnd == true;
        _autoRefresh = !_isDraft && restoreState?.AutoRefresh != false;
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

        _levelBands = new LogLevelBands(_logText) { IsHitTestVisible = false, ClipToBounds = true };
        Content = BuildLayout();
        ScrollViewer.SetVerticalScrollBarVisibility(_logText, ScrollBarVisibility.Hidden);
        _fileLabel.Text = console?.Title ?? draftName ?? System.IO.Path.GetFileName(_path);
        if (!_isDraft && !IsConsole) ToolTip.SetTip(_fileLabel, _path);
        ConfigureInteractions();
        _logText.LayoutUpdated += OnLogTextLayoutUpdated;
        RefreshButtons();

        InitialPageLoad = LoadPageAsync(Math.Max(_hiddenOffset, restoreState?.Offset ?? _hiddenOffset), saveHistory: false,
            position: FollowEnd ? PagePosition.FollowEnd : PagePosition.Start);
        LoadMinimapSamples();
        StartIndexIfNeeded();
        if (_autoRefresh)
        {
            _refreshTimer.Start();
        }
    }

    public event Action<string>? StatusChanged;
    internal event Action? TextPositionChanged;

    public string Path => _path;
    public Task InitialPageLoad { get; }
    public bool EditsPagesByDefault { get; }
    public bool IsReadOnly => _pageEdit is null;
    public bool AutoRefresh => _autoRefresh;
    public long? CurrentOffset => _page?.StartOffset;
    internal long FileLength => _fileStamp.Length;
    public long? KnownLineCount => _totalLineCount ?? _savedLineCount;
    public string StatusText { get; private set; } = string.Empty;

    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            _wordWrap = value;
            if (_pageEditor is { } editor) editor.WordWrap = value;
            _logText.TextWrapping = _filteredLogText.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            var horizontalScroll = value ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            ScrollViewer.SetHorizontalScrollBarVisibility(_logText, horizontalScroll);
            ScrollViewer.SetHorizontalScrollBarVisibility(_filteredLogText, horizontalScroll);
            _wrapButton.Content = value ? "Перенос: вкл." : "Перенос: выкл.";
        }
    }

    public LogTabState CaptureState()
    {
        return new LogTabState(
            _encodingOverride,
            _page?.StartOffset ?? _hiddenOffset,
            _hiddenOffset,
            _wordWrap,
            FollowEnd,
            _autoRefresh,
            _indexPaused,
            _searchPanel.IsVisible,
            _regexBox.IsChecked == true,
            _caseBox.IsChecked == true,
            _wholeWordBox.IsChecked == true,
            _searchBox.Text ?? string.Empty,
            _bookmarkIdentity,
            _fileStamp.Length,
            _bookmarkFingerprint,
            _indexOptIn,
            EditsPagesByDefault);
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

    private void RefreshBookmarkProof()
    {
        _observedDigest = TryReadObservedDigest(_fileStamp.Length);
        _bookmarkIdentity = ReadFileIdentity(_file.SafeFileHandle);
        _bookmarkFingerprint = _observedDigest is { } digest
            ? new LogFingerprint(GetFileModifiedTime(_file.SafeFileHandle), Array.ConvertAll(digest, value => (int)value))
            : null;
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
        if (_pageEditor is { } editor) editor.FontSize = value;
        _pageLineNumbers?.SetValue(TextBlock.FontSizeProperty, value);
        _logText.FontSize = value;
        _filteredLogText.FontSize = value;
        _lineGutterText.FontSize = value;
        RefreshLineCount();
    }

    private int _activeSearchResult = -1;

    public async Task NavigateMatchAsync(bool backward)
    {
        if (_levelFilterViewport?.IsVisible == true) CloseInlineLevelFilter();
        if (string.IsNullOrEmpty(_searchBox.Text))
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
        StopFollowingEnd();
        _activeSearchResult = _searchResults.IndexOf(hit);
        var end = hit.MatchIndex + hit.MatchLength;
        if (_page?.Rows.FirstOrDefault()?.StartOffset != hit.Offset)
        {
            await LoadPageAsync(hit.Offset, saveHistory: true);
        }

        if (_page?.Rows.FirstOrDefault()?.StartOffset == hit.Offset && end > FirstDisplayedLineLength())
        {
            await LoadPageAsync(hit.Offset, saveHistory: false,
                maxPageBytes: MaximumSearchLineBytes);
        }

        if (_page?.Rows.FirstOrDefault()?.StartOffset == hit.Offset && hit.MatchLength > 0
            && hit.MatchIndex >= 0 && end <= FirstDisplayedLineLength())
        {
            if (_pageEdit is not null)
            {
                _pageEditor!.Select(hit.MatchIndex, hit.MatchLength);
                return;
            }
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
        if (PageNavigationBlocked) return;
        if (_levelFilterViewport?.IsVisible == true) CloseInlineLevelFilter();
        if (!_searchBox.IsKeyboardFocusWithin && (_pageEdit is null ? _logText.SelectedText : _pageEditor!.SelectedText) is { Length: > 0 } selected)
        {
            _regexBox.IsChecked = false;
            _searchBox.Text = selected;
        }
        _searchPanel.IsVisible = true;
        _searchBox.Focus();
        _searchBox.SelectAll();
        if (_searchResults.Count == 0 && _searchCancellation is null) ScheduleSearch();
    }

    public bool CloseSearch()
    {
        if (!_searchPanel.IsVisible) return false;
        _searchPanel.IsVisible = false;
        StopSearch();
        FocusPageEditor();
        return true;
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _closing.Cancel();
        _filterReading?.Cancel();
        _filterReading?.Dispose();
        _filterReading = null;
        StopSearch();
        _indexCancellation?.Cancel();
        _trimCancellation?.Cancel();
        _refreshTimer.Stop();
        if (_pageEditor is { } editor)
        {
            _unbindPagePaste?.Invoke();
            editor.LayoutUpdated -= OnLogTextLayoutUpdated;
            ClearPageEdit();
        }
        _logText.LayoutUpdated -= OnLogTextLayoutUpdated;
        _verticalLogScrollbar.Scroll -= OnLogScrollbarScrolled;
        _verticalLogScrollbar.SetOwner(null);
        _draggedPage = null;
        _file.Dispose();
        _console?.Dispose();
        _logText.Text = string.Empty;
        _lineGutterText.Text = string.Empty;
        _fileLabel.Text = string.Empty;
        _indexLabel.Text = string.Empty;
        _statusLabel.Text = string.Empty;
        _countLabel.Text = string.Empty;
        _lineNumberLabel.Text = string.Empty;
        _positionLabel.Text = string.Empty;
        _searchStatusText.Text = string.Empty;
        _searchResults.Clear();
        _searchResultButtons.Children.Clear();
        _minimap.Widths = [];
        _previousOffsets.Clear();
        _skippedSearchLines.Clear();
        _displayRowStarts = [];
        _displaySourceLineNumbers = [];
        _page = null;
        _pageEdit = null;
        lock (_lineIndexGate) _lineIndex.Clear();
        _closing.Dispose();
    }

    internal bool CanFilterLevels => !_isDraft && (!EditsPagesByDefault || LogLevels.IsLogPath(_path)) && !HasPendingEdit;

    internal Task ShowLevelFilterAsync()
    {
        if (_closed || _levelFilterViewport is null || (!_levelFilterOpen && !CanFilterLevels)) return Task.CompletedTask;
        if (_levelFilterOpen) CloseInlineLevelFilter();
        else OpenInlineLevelFilter();
        return Task.CompletedTask;
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
        var modeControls = BuildPageModeControls();
        var titleGroup = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            MinWidth = 0,
        };
        titleGroup.Children.Add(_fileLabel);
        Grid.SetColumn(modeControls, 1);
        titleGroup.Children.Add(modeControls);

        SetButtonStyle(_pauseButton, "focus-quiet");
        var contextActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 5,
        };
        contextActions.Children.Add(_pauseButton);
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
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 0,
            Background = FocusTheme.Editor,
        };
        Grid.SetRow(content, 1);
        _lineGutterScrollViewer.Content = new Border
        {
            Padding = new Thickness(5, 6, 11, 6),
            Background = FocusTheme.Chrome,
            Child = _lineGutterText,
        };
        AutomationProperties.SetName(_lineGutterScrollViewer, "Номера строк лога");
        AutomationProperties.SetAutomationId(_lineGutterScrollViewer, "LogLineNumbers");
        Grid.SetColumn(_lineGutterScrollViewer, 0);
        content.Children.Add(_lineGutterScrollViewer);
        Grid.SetColumn(_logText, 1);
        AutomationProperties.SetAutomationId(_logText, "LogText");
        content.Children.Add(_logText);
        Grid.SetColumn(_levelBands, 1);
        content.Children.Add(_levelBands);
        AutomationProperties.SetName(_minimap, "Прокрутка лога");
        AutomationProperties.SetAutomationId(_minimap, "LogMinimap");
        Grid.SetColumn(_minimap, 2);
        content.Children.Add(_minimap);
        AutomationProperties.SetName(_verticalLogScrollbar, "Прокрутка страницы лога");
        Grid.SetColumn(_verticalLogScrollbar, 3);
        content.Children.Add(_verticalLogScrollbar);
        _contentViewport = new Grid();
        _contentViewport.Children.Add(content);
        _levelFilterViewport = BuildLevelFilterViewport();
        _levelFilterViewport.IsVisible = false;
        _levelFilter!.IsVisible = false;
        var logViewport = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        logViewport.Children.Add(_levelFilter!);
        Grid.SetRow(_contentViewport, 1);
        logViewport.Children.Add(_contentViewport);
        Grid.SetRow(logViewport, 1);
        root.Children.Add(logViewport);

        var searchRow = SearchBar.MakeRows();
        SearchBar.StyleInput(_searchBox);
        SearchBar.StyleCommand(_searchButton, primary: true);
        SearchBar.StyleCommand(_moreButton);
        SearchBar.AddRow(searchRow, 0, SearchBar.Options(_regexBox, _caseBox, _wholeWordBox),
            _searchBox, _searchButton, _moreButton, SearchBar.Icon("×", "Закрыть поиск (Esc)", () => CloseSearch()));
        _searchPanel.Children.Add(searchRow);
        var searchResults = new ScrollViewer
        {
            MaxHeight = 48,
            Content = _searchResultButtons,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.SizeChanged += (_, e) => searchResults.MaxHeight = Math.Clamp(e.NewSize.Height / 8, 32, 48);
        _cancelSearchButton = SearchBar.Icon("■", "Остановить поиск", StopSearch);
        _searchPanel.Children.Add(SearchBar.Footer(_searchStatusText, _cancelSearchButton, SearchBar.ResultsToggle(searchResults)));
        _searchPanel.Children.Add(searchResults);
        var searchFrame = new Border
        {
            Padding = new Thickness(6, 3),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = FocusTheme.Border,
            Background = FocusTheme.Panel,
            Child = _searchPanel,
            [!IsVisibleProperty] = _searchPanel[!IsVisibleProperty],
        };
        Grid.SetRow(searchFrame, 2);
        root.Children.Add(searchFrame);

        SetNavigationIcon(_startButton, "В начало лога");
        SetNavigationIcon(_previousButton, "Предыдущая страница");
        SetNavigationIcon(_nextButton, "Следующая страница");
        SetNavigationIcon(_endButton, EditsPagesByDefault ? "В конец файла" : "Конец — следить за новыми строками");
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
        SetButtonStyle(_levelFilterButton, "focus-quiet");
        ToolTip.SetTip(_levelFilterButton, "Фильтр строк по уровням во всём логе");
        _levelFilterButton.Click += async (_, _) => await ShowLevelFilterAsync();
        navigation.Children.Add(_levelFilterButton);
        navigation.Children.Add(actions);
        navigation.Children.Add(_cancelIndexButton);
        navigation.Children.Add(_cancelTrimButton);

        _countLabel.FontSize = 11;
        _countLabel.Foreground = FocusTheme.Muted;
        _lineNumberLabel.FontSize = 11;
        _lineNumberLabel.Foreground = FocusTheme.Muted;
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
        _verticalLogScrollbar.Scroll += OnLogScrollbarScrolled;
        _startButton.Click += async (_, _) => await NavigateAsync(_hiddenOffset, true);
        _previousButton.Click += async (_, _) =>
        {
            if (_levelFilterViewport?.IsVisible == true) await NavigateFilteredAsync(forward: false);
            else await NavigatePreviousAsync();
        };
        _nextButton.Click += async (_, _) =>
        {
            if (_levelFilterViewport?.IsVisible == true) await NavigateFilteredAsync(forward: true);
            else await NavigateAsync(_page?.NextOffset ?? _hiddenOffset, true);
        };
        _endButton.Click += async (_, _) =>
        {
            if (FollowEnd) await GoToEndAsync();
        };
        _middleButton.Click += async (_, _) => await NavigateAsync(Math.Max(_hiddenOffset, _reader.GetMidpointOffset(_closing.Token)), true);
        _pauseButton.Click += (_, _) => SetAutoRefresh(!_autoRefresh);
        _indexButton.Click += (_, _) => ToggleIndexing();
        _cancelIndexButton.Click += (_, _) => _indexCancellation?.Cancel();
        _openSearchButton.Click += (_, _) => FocusSearch();
        _wrapButton.Click += (_, _) => WordWrap = !WordWrap;
        _hideButton.Click += async (_, _) => await HideBeginningAsync();
        _revealButton.Click += async (_, _) =>
        {
            _hiddenOffset = 0;
            LoadMinimapSamples();
            _previousOffsets.Clear();
            await NavigateAsync(0, false);
        };
        _trimButton.Click += async (_, _) => await TrimBeginningAsync();
        _cancelTrimButton.Click += (_, _) => _trimCancellation?.Cancel();
        _searchButton.Click += async (_, _) => await SearchAsync(false);
        _moreButton.Click += async (_, _) => await SearchAsync(true);
        _searchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) ScheduleSearch();
        };
        _regexBox.IsCheckedChanged += (_, _) => ScheduleSearch();
        _caseBox.IsCheckedChanged += (_, _) => ScheduleSearch();
        _wholeWordBox.IsCheckedChanged += (_, _) => ScheduleSearch();
        _searchDebounce.Tick += async (_, _) =>
        {
            if (_busy) return;
            _searchDebounce.Stop();
            if (!_searchPanel.IsVisible || PageNavigationBlocked) return;
            if (_file.Length < TextOperations.AutomaticSearchMaxBytes) await SearchAsync(false);
            else ScheduleSearch();
        };
        _timeButton.Click += async (_, _) => await JumpToTimeAsync();
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                await SearchAsync(false);
                e.Handled = true;
            }
        };
        _logText.AddHandler(KeyDownEvent, OnLogKeyDown, RoutingStrategies.Tunnel);
        _logText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.SelectionStartProperty
                || e.Property == TextBox.SelectionEndProperty || e.Property == TextBox.TextProperty)
            {
                TextPositionChanged?.Invoke();
            }
        };
        AddHandler(PointerWheelChangedEvent, OnLogPointerWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(ScrollGestureEvent, (_, e) =>
        {
            if (e.Delta.Y != 0 && IsLogTextSource(e.Source as Visual)) StopFollowingEnd();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _minimap.PointerPressed += OnMinimapPointerPressed;
        _minimap.PointerMoved += OnMinimapPointerMoved;
        _minimap.PointerReleased += OnMinimapPointerReleased;
        _minimap.PointerCaptureLost += (_, _) => _minimapDragging = false;
        _minimap.PointerWheelChanged += OnMinimapPointerWheelChanged;
        WordWrap = _wordWrap;
    }

    private async void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.X && e.KeyModifiers == KeyModifiers.Control && _pageEdit is not null
            && _pageEditor is { IsReadOnly: false, SelectionLength: 0 })
        {
            e.Handled = true;
            RunPageClipboard(() => EditorClipboard.CutLineAsync(_pageEditor, () => !_closed && _pageEdit is not null && !IsWritingPage));
            return;
        }

        if (_closed || PageNavigationBlocked) return;

        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home
            || e.Key == Key.End && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            StopFollowingEnd();

        if (e.Key == Key.Home && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await NavigateAsync(_hiddenOffset, true);
        }
        else if (e.Key == Key.End && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await GoToEndAsync();
        }
        else if (e.Key == Key.PageDown)
        {
            e.Handled = true;
            if (_page is { HasMore: true } page) await NavigateAsync(page.NextOffset, true);
        }
        else if (e.Key == Key.PageUp)
        {
            e.Handled = true;
            await NavigatePreviousAsync(adjacent: true);
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            if (_levelFilterViewport?.IsVisible != true) FocusSearch();
        }
    }

    private void OnMinimapPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_minimap).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _minimapDragging = true;
        e.Pointer.Capture(_minimap);
        _ = NavigateMinimapToRatioAsync(MinimapRatioAt(e.GetPosition(_minimap).Y));
        e.Handled = true;
    }

    private void OnMinimapPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_minimapDragging)
        {
            return;
        }

        _ = NavigateMinimapToRatioAsync(MinimapRatioAt(e.GetPosition(_minimap).Y));
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
        _ = NavigateMinimapToRatioAsync(MinimapRatioAt(e.GetPosition(_minimap).Y));
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

    private async Task LoadPageAsync(long offset, bool saveHistory = true, PagePosition position = PagePosition.Start,
        int maxPageBytes = LogDataReader.DefaultPageBytes)
    {
        if (_closed || _busy || PageNavigationBlocked)
        {
            return;
        }

        _busy = true;
        RefreshButtons();
        try
        {
            var firstOffset = _hiddenOffset;
            var reader = _reader;
            var token = _closing.Token;
            var stamp = _fileStamp;
            var (page, firstLine, edit) = await Task.Run(() =>
            {
                var pageBytes = maxPageBytes;
                var requested = Math.Max(firstOffset, position == PagePosition.FollowEnd ? reader.GetEndOffset(token) : offset);
                var result = reader.ReadPage(requested, maxPageBytes: pageBytes, cancellationToken: token);
                var first = result.Rows.Count > 0 ? FindLineNumber(reader, result.Rows[0].StartOffset, token) : null;
                var pageEdit = EditsPagesByDefault ? ReadPageEdit(result, reader, stamp, token) : null;
                return (result, first, pageEdit);
            }, token);
            if (_closed || (position == PagePosition.FollowEnd && !FollowEnd && _page is not null))
            {
                return;
            }
            // The user can still type in the current page while the next one is being read.
            if (EditsPagesByDefault && HasPendingEdit)
            {
                SetStatus(PageEditHint);
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

            var previousY = _logScrollViewer?.Offset.Y ?? 0;
            _page = page;
            UpdateMinimapViewport();
            if (edit is not null)
            {
                BeginPageEdit(edit);
            }
            else
            {
                var displayText = new StringBuilder();
                for (var index = 0; index < page.Rows.Count; index++)
                {
                    var row = page.Rows[index];
                    if (index > 0 && !row.StartsWithinLine)
                    {
                        displayText.Append(Environment.NewLine);
                    }
                    displayText.Append(row.Text);
                }
                _logText.Text = displayText.ToString();
                _levelBands.SetPage(page);
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_page, page) || _logScrollViewer is not { } scroll) return;
                if (position == PagePosition.FollowEnd && !FollowEnd) return;
                var y = position switch
                {
                    PagePosition.End or PagePosition.FollowEnd => scroll.ScrollBarMaximum.Y,
                    PagePosition.Preserve => previousY,
                    _ => 0
                };
                scroll.Offset = new Vector(scroll.Offset.X, y);
            }, DispatcherPriority.Loaded);
            var rows = page.Rows.Count == 0 ? 0 : 1 + page.Rows.Skip(1).Count(row => !row.StartsWithinLine);
            _countLabel.Text = $"Строк на странице · {LineNumbers.Format(rows)}";
            if (_isDraft && !_indexOptIn && (KnownLineCount is null || (firstLine is null && rows > 0)))
            {
                _indexOptIn = true;
                _indexPaused = false;
                StartIndexIfNeeded();
            }
            UpdatePageLineNumbers(page, firstLine);
            SetStatus(page.WasTruncated
                ? "Файл изменился во время чтения. Позиция пересчитана по открытому файлу."
                : EditsPagesByDefault ? PageEditHint : string.Empty);
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
        catch (ObjectDisposedException) when (_closed)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Не удалось загрузить страницу. {error.Message}");
        }
        finally
        {
            _busy = false;
            RefreshButtons();
            if (!_closed && FollowEnd && position != PagePosition.FollowEnd)
            {
                _ = LoadPageAsync(_hiddenOffset, saveHistory: false, position: PagePosition.FollowEnd);
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
        if (_closed || PageNavigationBlocked) return;
        StopFollowingEnd();
        var request = ++_minimapRequest;
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
        var reader = _reader;
        var token = _closing.Token;
        try
        {
            var aligned = await Task.Run(() => reader.AlignToNextLine(target, token), token);
            if (_closed || request != _minimapRequest || !ReferenceEquals(reader, _reader)) return;
            if (_busy)
            {
                _pendingMinimapRatio = ratio;
                return;
            }
            await LoadPageAsync(Math.Max(_hiddenOffset, aligned), saveHistory: true);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_closed || !ReferenceEquals(reader, _reader)) { }
        catch (IOException error)
        {
            if (!_closed && request == _minimapRequest) SetStatus($"Переход по миникарте остановлен. {error.Message}");
        }
    }

    private double MinimapRatioAt(double y)
    {
        var height = _minimap.Bounds.Height;
        return height <= 0 ? 0 : Math.Clamp(y / height, 0, 1);
    }

    private void StopFollowingEnd()
    {
        if (FollowEnd) _endButton.IsChecked = false;
    }

    private Task GoToEndAsync()
    {
        if (EditsPagesByDefault)
        {
            StopFollowingEnd();
            return NavigateToPageAsync(Math.Max(_hiddenOffset, _reader.GetEndOffset(_closing.Token)), true, scrollToEnd: true);
        }
        _endButton.IsChecked = true;
        _pendingMinimapRatio = null;
        _minimapRequest++;
        return LoadPageAsync(_hiddenOffset, position: PagePosition.FollowEnd);
    }

    private Task NavigateAsync(long offset, bool saveHistory) => NavigateToPageAsync(offset, saveHistory, scrollToEnd: false);

    private async Task NavigateToPageAsync(long offset, bool saveHistory, bool scrollToEnd)
    {
        StopFollowingEnd();
        if (_page is { } current && offset == current.StartOffset)
        {
            if (_logScrollViewer is { } scroll)
            {
                scroll.Offset = new Vector(scroll.Offset.X,
                    scrollToEnd ? Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height) : 0);
            }
            return;
        }
        await LoadPageAsync(offset, saveHistory, position: scrollToEnd ? PagePosition.End : PagePosition.Start);
    }

    private async Task NavigatePreviousAsync(bool scrollToEnd = false, bool adjacent = false)
    {
        if (_page is not { } page || _busy || _closed || PageNavigationBlocked)
        {
            return;
        }
        StopFollowingEnd();
        long offset;
        if (!adjacent && _previousOffsets.Count > 0)
        {
            offset = _previousOffsets[^1];
            _previousOffsets.RemoveAt(_previousOffsets.Count - 1);
        }
        else
        {
            if (page.StartOffset <= _hiddenOffset) return;
            offset = _reader.GetPreviousPageOffset(page.StartOffset, _closing.Token);
        }
        offset = Math.Max(_hiddenOffset, offset);
        await LoadPageAsync(offset, saveHistory: false, position: scrollToEnd ? PagePosition.End : PagePosition.Start);
    }

    private async Task JumpByPercentageAsync()
    {
        if (!double.TryParse(_percentBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || percent is < 0 or > 100)
        {
            SetStatus("Введите процент от 0 до 100.");
            return;
        }
        if (percent == 100)
        {
            await NavigateToPageAsync(Math.Max(_hiddenOffset, _reader.GetEndOffset(_closing.Token)), true, scrollToEnd: true);
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

    private void ScheduleSearch()
    {
        StopSearch();
        _searchCancellation = null;
        ClearSearchResults();
        _searchStatusText.Text = "";
        ToolTip.SetTip(_searchStatusText, null);
        RefreshButtons();
        if (!_searchPanel.IsVisible || string.IsNullOrEmpty(_searchBox.Text) || _closed) return;
        if (_file.Length < TextOperations.AutomaticSearchMaxBytes)
            _searchDebounce.Start();
        else
            _searchStatusText.Text = "Нажмите «Найти» для поиска по большому файлу";
    }

    private void StopSearch()
    {
        _searchDebounce.Stop();
        _searchCancellation?.Cancel();
    }

    private void ClearSearchResults()
    {
        _searchOffset = _hiddenOffset;
        _searchResults.Clear();
        _searchResultButtons.Children.Clear();
        _searchHasMore = false;
        _activeSearchResult = -1;
        _lastSearchText = "";
    }

    private void SetSearchStatus(string message)
    {
        _searchStatusText.Text = message;
        ToolTip.SetTip(_searchStatusText, message);
        SetStatus(message);
    }

    private async Task SearchAsync(bool continueSearch)
    {
        _searchDebounce.Stop();
        var query = _searchBox.Text ?? string.Empty;
        if (query.Length == 0)
        {
            SetSearchStatus("Введите текст для поиска.");
            return;
        }

        if (_closed || _busy || PageNavigationBlocked) return;
        _searchCancellation?.Cancel();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _searchCancellation = source;
        var token = source.Token;
        var generation = _dataGeneration;
        if (!continueSearch || !string.Equals(query, _lastSearchText, StringComparison.Ordinal))
            ClearSearchResults();
        _lastSearchText = query;
        var startOffset = _searchOffset;
        var regex = _regexBox.IsChecked == true;
        var caseSensitive = _caseBox.IsChecked == true;
        var wholeWord = _wholeWordBox.IsChecked == true;
        RefreshButtons();
        SetSearchStatus("Поиск в логе…");
        try
        {
            var result = await Task.Run(() => SearchFile(query, regex, caseSensitive, wholeWord, startOffset, token), token);
            if (_closed || token.IsCancellationRequested || generation != _dataGeneration) return;
            foreach (var hit in result.Results)
            {
                _searchResults.Add(hit);
                var button = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = FocusTheme.Panel,
                    Content = new TextBlock
                    {
                        Text = hit.LineNumber is { } line ? $"{LineNumbers.Format(line)} · {hit.Preview}" : hit.Preview,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = FocusTheme.Text,
                    }
                };
                SetButtonStyle(button, "focus-quiet");
                button.Click += async (_, _) => await OpenSearchHitAsync(hit);
                _searchResultButtons.Children.Add(button);
            }
            _searchOffset = result.NextOffset;
            _searchHasMore = result.HasMore;
            var count = _searchResults.Count;
            var status = count == 0 ? "Совпадений не найдено." : $"Найдено {count:N0}";
            if (_searchHasMore) status += " · нажмите «Ещё»";
            if (result.Message is not null) status += $" · {result.Message}";
            SetSearchStatus(status);
        }
        catch (Exception error) when (token.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException)
        {
            if (!_closed && _searchPanel.IsVisible && ReferenceEquals(_searchCancellation, source))
                SetSearchStatus("Поиск остановлен");
        }
        catch (Exception error) when (error is IOException or ArgumentException or RegexMatchTimeoutException)
        {
            if (ReferenceEquals(_searchCancellation, source)) SetSearchStatus($"Поиск остановлен. {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, source))
            {
                _searchCancellation = null;
                RefreshButtons();
            }
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
        var reader = _reader;
        using var scan = reader.BeginSequentialScan();
        var cursor = Math.Max(Math.Max(offset, _hiddenOffset), reader.PreambleLength);
        var pageEnd = _file.Length;
        var pendingLine = new StringBuilder();
        var pendingOffset = cursor;
        var lineNumber = FindLineNumber(reader, cursor, token);
        string? notice = null;
        var skippingOversizedLine = false;
        var nextOffset = cursor;

        while (cursor < pageEnd && results.Count < SearchResultsPerPage)
        {
            token.ThrowIfCancellationRequested();
            var page = scan.ReadPage(cursor, token);
            if (page.NextOffset <= cursor) break;
            foreach (var row in page.Rows)
            {
                token.ThrowIfCancellationRequested();
                nextOffset = row.EndOffset;
                if (!row.StartsWithinLine) pendingOffset = row.StartOffset;
                if (row.EndOffset - pendingOffset > MaximumSearchLineBytes)
                {
                    skippingOversizedLine = true;
                    notice = $"Пропущена строка длиннее лимита поиска в {MaximumSearchLineBytes:N0} байт.";
                }

                if (row.IsTruncated)
                {
                    if (!skippingOversizedLine) pendingLine.Append(row.Text);
                    continue;
                }

                if (!skippingOversizedLine)
                {
                    var text = pendingLine.Length == 0 ? row.Text : pendingLine.Append(row.Text).ToString();
                    if (FindMatch(text, query, expression, matchCase, wholeWord) is { } match)
                    {
                        results.Add(new SearchResult(pendingOffset, lineNumber, ClipText(text), match.Index, match.Length));
                    }
                }
                pendingLine.Clear();
                skippingOversizedLine = false;
                lineNumber++;
                if (results.Count == SearchResultsPerPage) break;
            }
            cursor = page.NextOffset;
            if (!page.HasMore) break;
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
        using var scan = _reader.BeginSequentialScan();
        while (cursor < _file.Length)
        {
            token.ThrowIfCancellationRequested();
            var page = scan.ReadPage(cursor, token);
            if (page.Rows.Count == 0 || page.NextOffset <= cursor)
            {
                break;
            }
            foreach (var row in page.Rows)
            {
                if (row.StartsWithinLine)
                {
                    continue;
                }
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

    private static readonly string[] TimestampDateFormats = ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss"];
    private static readonly string[] TimestampClockFormats = ["HH:mm:ss.FFFFFFF", "HH:mm:ss"];

    private static bool TryReadTimestamp(string line, out DateTime timestamp)
    {
        var match = TimestampPattern.Match(line);
        if (match.Success)
        {
            var date = match.Groups["date"];
            var time = match.Groups["time"];
            var clock = match.Groups["clock"];
            Span<char> value = stackalloc char[32];
            int length;
            string[] formats;
            if (date.Success)
            {
                var dateSpan = date.ValueSpan;
                var timeSpan = time.ValueSpan;
                dateSpan.CopyTo(value);
                value[dateSpan.Length] = ' ';
                timeSpan.CopyTo(value[(dateSpan.Length + 1)..]);
                length = dateSpan.Length + 1 + timeSpan.Length;
                formats = TimestampDateFormats;
            }
            else
            {
                var clockSpan = clock.ValueSpan;
                clockSpan.CopyTo(value);
                length = clockSpan.Length;
                formats = TimestampClockFormats;
            }
            if (DateTime.TryParseExact(value[..length], formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
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
        UpdateMinimapViewport();
        LoadMinimapSamples();
        _previousOffsets.Clear();
        if (_page.StartOffset < _hiddenOffset)
        {
            await NavigateAsync(_hiddenOffset, false);
        }
        RefreshButtons();
        SetStatus("Просмотр и поиск начинаются с выбранной строки.");
    }

    private async Task TrimBeginningAsync()
    {
        if (IsConsole || _hiddenOffset <= 0 || _busy)
        {
            return;
        }

        var boundary = _hiddenOffset;
        var expected = _fileStamp;
        var preambleLength = _reader.PreambleLength;
        var confirm = await ShowConfirmationAsync("Строки перед скрытой границей будут удалены из файла. Рядом будет создана резервная копия. Продолжить?");
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
        DialogWindowChrome.Apply(dialog);
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

    public void SetAutoRefresh(bool enabled)
    {
        if (_isDraft || _closed) return;
        _autoRefresh = enabled;
        if (enabled)
        {
            _refreshTimer.Start();
            OnRefreshTimerTick(null, EventArgs.Empty);
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
        RefreshBookmarkProof();
        previous.Dispose();

        _previousOffsets.Clear();
        _hiddenOffset = 0;
        lock (_lineIndexGate)
        {
            _lineIndex.Clear();
            _indexedTo = 0;
            _indexedLineCount = 0;
        }
        _totalLineCount = null;
        RefreshLineCount();
        _skippedSearchLines.Clear();
        ClearSearchResults();
        _searchStatusText.Text = "";
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
        if (_closed || _busy || _pageWrite is not null || !_autoRefresh
            || (_pageEdit is not null && PreserveExternalPageEdit is null))
        {
            return;
        }

        try
        {
            var now = FileStamp.Read(_path, _file);
            if (now == _fileStamp)
            {
                CompleteConsoleRefresh();
                return;
            }

            var grew = now.Length > _fileStamp.Length;
            var previousContent = grew && now.Identity == _fileStamp.Identity
                ? TryReadObservedDigest(_fileStamp.Length) : null;
            var appended = _observedDigest is { } expected && previousContent is not null
                && expected.AsSpan().SequenceEqual(previousContent);
            if (!appended) ResetFileHandle();
            var preservedDraft = ExitPageEditForExternalRefresh();
            if (appended)
            {
                _fileStamp = now;
                _totalLineCount = null;
                RefreshBookmarkProof();
                if (FollowEnd || _page?.HasMore != true || EditsPagesByDefault || preservedDraft)
                {
                    await LoadPageAsync(_page?.StartOffset ?? _hiddenOffset, saveHistory: false,
                        position: FollowEnd ? PagePosition.FollowEnd : PagePosition.Preserve);
                }
            }
            else
            {
                SetStatus("Файл был заменён, очищен или перезаписан. Позиция и индекс сброшены.");
                await LoadPageAsync(0, saveHistory: false, position: FollowEnd ? PagePosition.FollowEnd : PagePosition.Start);
            }
            ApplyLargeLogIndexOptInPolicy();
            StartIndexIfNeeded();
            LoadMinimapSamples();
            if (_levelFilterOpen && _levelFilter?.SelectedLevels != LogLevel.All)
            {
                if (!appended)
                {
                    _filterHistory.Clear();
                    _filterCurrent = new(_hiddenOffset);
                }
                await LoadFilteredPageAsync(appended ? _filterCurrent : new FilterCursor(_hiddenOffset));
            }
            if (preservedDraft) ReportPreservedExternalEdit();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Не удалось проверить лог. {error.Message}");
        }
    }

    internal void ReportPreservedExternalEdit() => SetStatus(MainWindow.ExternalDraftStatus);

    internal void ReportConsoleDisconnected(string message)
    {
        if (_closed) return;
        _consoleDisconnectMessage = message;
        RefreshButtons();
        if (!_autoRefresh) SetStatus(message);
    }

    private void CompleteConsoleRefresh()
    {
        if (_consoleDisconnectMessage is null || _closed) return;
        SetAutoRefresh(false);
        SetStatus(_consoleDisconnectMessage);
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
        var reader = _reader;
        var start = new LogLineCheckpoint(_indexedTo, _indexedLineCount);
        var length = _file.Length;
        var batchEnd = start.Offset + Math.Min(IndexBatchBytes, length - start.Offset);
        var generation = _dataGeneration;
        _ = Task.Run(() =>
        {
            var points = new List<LogLineCheckpoint>();
            var end = reader.ScanLineBreaks(start, batchEnd, points.Add, token);
            long? total = end.Offset == length
                ? end.Lines + (length > reader.PreambleLength && !reader.IsNewlineBefore(length) ? 1 : 0)
                : null;
            return (End: end, Points: points, Total: total);
        }, token).ContinueWith(task => Dispatcher.UIThread.Post(() =>
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
                var result = task.Result;
                lock (_lineIndexGate)
                {
                    _indexedTo = result.End.Offset;
                    _indexedLineCount = result.End.Lines;
                    _lineIndex.AddRange(result.Points);
                }
                _totalLineCount = length == _file.Length ? result.Total : null;
                RefreshLineCount();
                if (_page is { Rows.Count: > 0 } page && !_displaySourceLineNumbers.Any(line => line.HasValue)
                    && page.Rows[0].StartOffset <= _indexedTo)
                {
                    _ = RefreshCurrentPageNumbersAsync(reader, page, generation);
                }
                if (_indexedTo > start.Offset && _indexedTo < _file.Length && !_indexPaused)
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

    private long? FindLineNumber(LogDataReader reader, long offset, CancellationToken token)
    {
        if (offset < reader.PreambleLength) return null;

        LogLineCheckpoint closest;
        lock (_lineIndexGate)
        {
            if (offset - reader.PreambleLength > 128 * 1024 && offset > _indexedTo)
            {
                return null;
            }

            closest = new(reader.PreambleLength, 0);
            var low = 0;
            var high = _lineIndex.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_lineIndex[middle].Offset <= offset) low = middle + 1;
                else high = middle;
            }
            if (low > 0) closest = _lineIndex[low - 1];
        }

        var result = reader.ScanLineBreaks(closest, offset, null, token);
        return result.Offset == offset ? result.Lines + 1 : null;
    }

    private async Task RefreshCurrentPageNumbersAsync(LogDataReader reader, LogPage page, long generation)
    {
        var token = _closing.Token;
        try
        {
            var line = await Task.Run(() => FindLineNumber(reader, page.Rows[0].StartOffset, token), token);
            if (!_closed && _dataGeneration == generation && ReferenceEquals(_page, page))
            {
                UpdatePageLineNumbers(page, line);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_closed || generation != _dataGeneration) { }
        catch (IOException error)
        {
            if (!_closed && generation == _dataGeneration) SetStatus($"Не удалось уточнить номера строк. {error.Message}");
        }
    }

    private void UpdatePageLineNumbers(LogPage page, long? lineNumber)
    {
        var rowStarts = new List<int>(page.Rows.Count);
        var lineNumbers = new List<long?>(page.Rows.Count);
        var textOffset = 0;
        for (var index = 0; index < page.Rows.Count; index++)
        {
            var row = page.Rows[index];
            if (index == 0 || !row.StartsWithinLine)
            {
                if (index > 0)
                {
                    textOffset += Environment.NewLine.Length;
                    lineNumber++;
                }
                rowStarts.Add(textOffset);
                lineNumbers.Add(lineNumber);
            }
            textOffset += row.Text.Length;
        }
        _displayRowStarts = [.. rowStarts];
        _displaySourceLineNumbers = [.. lineNumbers];

        var first = _displaySourceLineNumbers.FirstOrDefault(value => value.HasValue);
        var last = _displaySourceLineNumbers.LastOrDefault(value => value.HasValue);
        _lineNumberLabel.Text = first is { } firstLine && last is { } lastLine
            ? $"Строки {LineNumbers.Format(firstLine)}–{LineNumbers.Format(lastLine)}"
            : page.Rows.Count == 0 ? "" : "Позиция · " + (_indexPaused ? "нет индекса строк" : "считаются строки");
        RefreshLineCount();
        if (_pageEdit is null) UpdateLineGutterText();
        else _pageLineNumbers!.FirstLineNumber = first;
        TextPositionChanged?.Invoke();
    }

    internal (long Line, int Selected, bool IsPageLine)? TextPosition
    {
        get
        {
            if (_page is null) return null;
            if (_pageEdit is not null && _pageEditor is { } editor)
            {
                var line = editor.TextArea.Caret.Line;
                var first = _displaySourceLineNumbers.FirstOrDefault();
                return (first + line - 1 ?? line, editor.SelectionLength, first is null);
            }
            var text = _logText.Text.AsSpan();
            var caret = Math.Clamp(_logText.CaretIndex, 0, text.Length);
            var start = Math.Clamp(_logText.SelectionStart, 0, text.Length);
            var end = Math.Clamp(_logText.SelectionEnd, 0, text.Length);
            var selected = Math.Abs(end - start);
            var index = Array.BinarySearch(_displayRowStarts, caret);
            var pageLine = index < 0 ? Math.Max(0, ~index - 1) : index;
            var fileLine = _displaySourceLineNumbers.ElementAtOrDefault(pageLine);
            return (fileLine ?? pageLine + 1, selected, fileLine is null);
        }
    }

    private void UpdateDisplayedLineCount() =>
        _positionLabel.Text = DisplayLineCount is { } count ? $"Всего строк · {LineNumbers.Format(count)}" : "";
    private void RefreshLineCount()
    {
        UpdateDisplayedLineCount();
        _indexLabel.Text = KnownLineCount is null && _indexedLineCount > 0
            ? $"Посчитано · {LineNumbers.Format(_indexedLineCount)}"
            : "";
        var last = _displaySourceLineNumbers.LastOrDefault(value => value.HasValue);
        var max = Math.Max(KnownLineCount ?? 0, last ?? 0);
        var text = new FormattedText(LineNumbers.Format(max), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(_lineGutterText.FontFamily), _lineGutterText.FontSize, _lineGutterText.Foreground);
        _lineGutterScrollViewer.Width = Math.Max(32, Math.Ceiling(text.Width) + 16);
    }

    private void OnLogTextLayoutUpdated(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _pageEdit is null ? (Control)_logText : _pageEditor)) return;
        AttachLogScrollViewer();
        if (FollowEnd && _page is { HasMore: false } && _logScrollViewer is { } scroll && scroll.Offset.Y < scroll.ScrollBarMaximum.Y)
            scroll.Offset = new Vector(scroll.Offset.X, scroll.ScrollBarMaximum.Y);
        if (_pageEdit is null)
        {
            UpdateLineGutterText();
            _levelBands.InvalidateVisual();
        }
    }

    private void AttachLogScrollViewer()
    {
        Control surface = _pageEdit is null ? _logText : _pageEditor!;
        if (_logScrollViewer is { } current && surface.IsVisualAncestorOf(current)) return;
        var scrollViewer = surface.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (ReferenceEquals(scrollViewer, _logScrollViewer))
        {
            return;
        }
        _verticalLogScrollbar.SetOwner(scrollViewer);
        _draggedPage = null;
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.ScrollChanged -= OnLogScrollChanged;
        }
        _logScrollViewer = scrollViewer;
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.AllowAutoHide = false;
            _logScrollViewer.ScrollChanged += OnLogScrollChanged;
            _lineGutterScrollViewer.Offset = new Vector(0, _logScrollViewer.Offset.Y);
        }
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_logScrollViewer is { } scrollViewer)
        {
            _lineGutterScrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y);
            UpdateMinimapViewport();
            _levelBands.InvalidateVisual();
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
                    ? _displaySourceLineNumbers[rowIndex] is { } line ? LineNumbers.Format(line) : "?"
                    : string.Empty);
            }
        }
        else
        {
            values.AddRange(_displaySourceLineNumbers.Select(value => value is { } line ? LineNumbers.Format(line) : "?"));
        }

        _lineGutterText.Text = string.Join(Environment.NewLine, values);
        if (_logScrollViewer is { } scrollViewer)
        {
            _lineGutterScrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y);
        }
    }

    private async void LoadMinimapSamples()
    {
        if (_closed) return;
        if (_minimapLoading)
        {
            _minimapRefreshPending = true;
            return;
        }
        _minimapRefreshPending = false;
        var length = _file.Length;
        if (length == 0)
        {
            _minimap.Widths = [];
            return;
        }
        _minimapLoading = true;
        var generation = _dataGeneration;
        var reader = _reader;
        var start = _hiddenOffset;
        try
        {
            var token = _closing.Token;
            var widths = await Task.Run(() => reader.ReadMinimapWidths(start, length, token), token);
            if (_closed || generation != _dataGeneration || start != _hiddenOffset)
            {
                return;
            }
            _minimap.Widths = widths;
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
            if (!_closed && (_minimapRefreshPending || generation != _dataGeneration || start != _hiddenOffset))
            {
                Dispatcher.UIThread.Post(LoadMinimapSamples);
            }
        }
    }

    private void RefreshButtons()
    {
        if (_closed) return;
        var enabled = !_busy && !PageNavigationBlocked;
        UpdatePageModeControls();
        _levelFilterButton.IsVisible = !_isDraft && (!EditsPagesByDefault || LogLevels.IsLogPath(_path));
        _levelBands.IsVisible = _levelFilterButton.IsVisible && _pageEdit is null;
        _levelFilterButton.IsEnabled = CanFilterLevels || _levelFilterOpen;
        _pauseButton.Content = _autoRefresh ? "Пауза" : "Продолжить";
        _liveLabel.Text = _isDraft ? "" : _consoleDisconnectMessage is not null ? "● Источник отключён"
            : _autoRefresh ? "● Обновляется" : "● На паузе";
        _liveLabel.Foreground = _autoRefresh && _consoleDisconnectMessage is null ? FocusTheme.Success : FocusTheme.Muted;
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
        _previousButton.IsEnabled = enabled && (_previousOffsets.Count > 0 || _page?.StartOffset > _hiddenOffset);
        _nextButton.IsEnabled = enabled && _page?.HasMore == true;
        _startButton.IsEnabled = enabled && _page is not null;
        _endButton.IsEnabled = _page is not null && !PageNavigationBlocked && (!_busy || FollowEnd);
        _middleButton.IsEnabled = enabled && _file.Length > 0;
        _pauseButton.IsVisible = !_isDraft;
        _liveLabel.IsVisible = !_isDraft && (_pageEdit is null || EditsPagesByDefault);
        _pauseButton.IsEnabled = !_closed && _consoleDisconnectMessage is null;
        _indexButton.IsEnabled = !_closed && (!_indexBuilding || _indexCancellation is not null);
        _cancelIndexButton.IsVisible = _indexBuilding;
        _cancelTrimButton.IsVisible = _trimCancellation is not null;
        _hideButton.IsVisible = !_isDraft && !EditsPagesByDefault;
        _revealButton.IsVisible = !_isDraft && !EditsPagesByDefault;
        _trimButton.IsVisible = !_isDraft && !EditsPagesByDefault && !IsConsole;
        _hideButton.IsEnabled = enabled && _page?.Rows.Count > 0;
        _revealButton.IsEnabled = enabled && _hiddenOffset > 0;
        _trimButton.IsEnabled = enabled && _hiddenOffset > 0;
        _searchButton.IsEnabled = enabled && _searchCancellation is null;
        _moreButton.IsEnabled = _searchButton.IsEnabled && _searchHasMore;
        _cancelSearchButton.IsVisible = _searchCancellation is not null;
        _openSearchButton.IsEnabled = enabled;
        _timeButton.IsEnabled = enabled;
        if (_levelFilterViewport?.IsVisible == true)
        {
            _previousButton.IsEnabled = !_busy && _filterReading is null && _filterHistory.Count > 0;
            _nextButton.IsEnabled = !_busy && _filterReading is null && _filterNext.HasValue;
            _startButton.IsEnabled = _endButton.IsEnabled = _middleButton.IsEnabled = false;
            _openSearchButton.IsEnabled = _timeButton.IsEnabled = false;
            _searchButton.IsEnabled = _moreButton.IsEnabled = false;
            _hideButton.IsEnabled = _revealButton.IsEnabled = _trimButton.IsEnabled = false;
            _levelFilterButton.Content = "Закрыть уровни";
        }
        if (_levelFilterOpen)
        {
            _levelFilter!.IsEnabled = !HasPendingEdit;
            _levelFilterButton.IsEnabled = true;
            if (_levelFilter.SelectedLevels != LogLevel.All)
                _editPageButton.IsEnabled = _savePageButton.IsEnabled = _cancelPageButton.IsEnabled = false;
        }
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
}
