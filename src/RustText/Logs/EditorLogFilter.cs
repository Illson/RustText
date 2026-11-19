using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using RustText.Demo.Editor;

namespace RustText.Demo.Logs;

internal sealed class EditorLogFilter : IDisposable
{
    private readonly TextEditor _source;
    private readonly Control? _minimap;
    private readonly bool _sourceWasReadOnly;
    private readonly Action _closeRequested;
    private readonly LogLevelFilterPanel _panel;
    private TextEditor? _projection;
    private TextBlock? _status;
    private readonly Grid _filteredView;
    private readonly EventHandler<DocumentChangeEventArgs> _sourceChanged;
    private GroupedLineNumberMargin? _lineNumberMargin;
    private CancellationTokenSource? _scanCancellation;
    private long _generation;
    private bool _closing;
    private bool _disposed;

    internal EditorLogFilter(TextEditor source, Control? minimap, Action closeRequested)
    {
        _source = source;
        _minimap = minimap;
        _sourceWasReadOnly = source.IsReadOnly;
        _closeRequested = closeRequested;
        _panel = new LogLevelFilterPanel();
        _panel.LevelsChanged += OnLevelsChanged;
        _panel.CloseRequested += OnCloseRequested;
        _sourceChanged = (_, _) =>
        {
            if (_panel.SelectedLevels != LogLevel.All) _ = RefreshProjectionAsync(_panel.SelectedLevels);
        };
        source.Document.Changed += _sourceChanged;
        _filteredView = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), IsVisible = false };
    }

    internal LogLevelFilterPanel PanelControl => _panel;
    internal Control FilteredView => _filteredView;
    internal bool IsFiltering => _panel.SelectedLevels != LogLevel.All;
    internal TextEditor VisibleEditor => IsFiltering && _projection is { } projection ? projection : _source;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing = true;
        _panel.LevelsChanged -= OnLevelsChanged;
        _panel.CloseRequested -= OnCloseRequested;
        _source.Document.Changed -= _sourceChanged;
        CancelScan();
        _source.IsReadOnly = _sourceWasReadOnly;
        _source.IsVisible = true;
        if (_minimap is not null) _minimap.IsVisible = true;
        ReleaseProjection();
    }

    private void OnLevelsChanged()
    {
        if (_panel.SelectedLevels == LogLevel.All)
        {
            CancelScan();
            _filteredView.IsVisible = false;
            _source.IsVisible = true;
            _source.IsReadOnly = _sourceWasReadOnly;
            if (_minimap is not null) _minimap.IsVisible = true;
            ReleaseProjection();
            return;
        }
        EnsureProjection();
        _source.IsReadOnly = true;
        _source.IsVisible = false;
        if (_minimap is not null) _minimap.IsVisible = false;
        _filteredView.IsVisible = true;
        _ = RefreshProjectionAsync(_panel.SelectedLevels);
    }

    private void OnCloseRequested() => RequestClose();

    private void RequestClose()
    {
        if (_closing || _disposed) return;
        _closing = true;
        _closeRequested();
    }

    private void CancelScan()
    {
        _generation++;
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = null;
    }

    private async Task RefreshProjectionAsync(LogLevel levels)
    {
        var generation = ++_generation;
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        var token = cancellation.Token;
        var snapshot = _source.Document.CreateSnapshot();
        try
        {
            var filtered = await Task.Run(() =>
            {
                using var reader = snapshot.CreateReader();
                return FilterText(reader, levels, token);
            }, token);
            if (_disposed || token.IsCancellationRequested || generation != _generation) return;
            var document = new TextDocument(filtered.Text);
            document.UndoStack.MarkAsOriginalFile();
            if (_projection is not { } projection || _status is not { } status) return;
            projection.Document = document;
            projection.CaretOffset = 0;
            _lineNumberMargin ??= GroupedLineNumberMargin.ReplaceExisting(projection);
            _lineNumberMargin?.SetSourceLineNumbers(filtered.SourceLines);
            status.Text = $"Просмотр фильтра · только чтение · {filtered.SourceLines.Count} строк";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested && generation == _generation && _status is { } status)
                status.Text = $"Не удалось обновить фильтр: {ex.Message}";
        }
    }

    private void EnsureProjection()
    {
        if (_projection is not null) return;
        var status = new TextBlock
        {
            Text = "Подготовка фильтрованного просмотра · только чтение",
            Foreground = FocusTheme.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 2)
        };
        var projection = new TextEditor
        {
            FontFamily = _source.FontFamily, FontSize = _source.FontSize,
            Background = FocusTheme.Editor, Foreground = FocusTheme.Text, LineNumbersForeground = FocusTheme.Gutter,
            LineNumbersMargin = new Thickness(8, 0, 12, 0), WordWrap = _source.WordWrap, ShowLineNumbers = true,
            IsReadOnly = true, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(projection, "Строки лога с выбранными уровнями");
        AutomationProperties.SetAutomationId(projection, "LogLevelFilterResults");
        AutomationProperties.SetAutomationId(status, "LogLevelFilterStatus");
        var logLevels = new LogLevelColorizer { Enabled = true };
        projection.TextArea.TextView.LineTransformers.Add(logLevels);
        projection.TextArea.TextView.BackgroundRenderers.Add(logLevels);
        FocusTheme.ThemeChanged += projection.TextArea.TextView.Redraw;
        Grid.SetRow(status, 0);
        Grid.SetRow(projection, 1);
        _filteredView.Children.Add(status);
        _filteredView.Children.Add(projection);
        _status = status;
        _projection = projection;
    }

    private void ReleaseProjection()
    {
        if (_projection is { } projection)
        {
            FocusTheme.ThemeChanged -= projection.TextArea.TextView.Redraw;
            projection.Document = new TextDocument();
        }
        _filteredView.Children.Clear();
        _projection = null;
        _status = null;
        _lineNumberMargin = null;
    }

    private static (string Text, IReadOnlyList<int> SourceLines) FilterText(TextReader reader, LogLevel levels, CancellationToken token)
    {
        if (levels == 0) return (string.Empty, Array.Empty<int>());
        var result = new StringBuilder();
        var sourceLines = new List<int>();
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            lineNumber++;
            if ((levels & LogLevels.Classify(line)) == 0) continue;
            if (sourceLines.Count > 0) result.Append('\n');
            result.Append(line);
            sourceLines.Add(lineNumber);
        }
        return (result.ToString(), sourceLines);
    }
}
