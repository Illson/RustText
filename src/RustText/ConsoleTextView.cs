using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;

namespace RustText.Demo;

internal sealed class ConsoleTextView : Grid, IDisposable
{
    // shortcut: keep recent output in the editor; the log viewer still has the whole capture.
    internal const int TextLimit = 250_000;
    private readonly StreamReader _reader;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly char[] _buffer = new char[32 * 1024];
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap };
    private string? _disconnected;
    private bool _earlierOutput;
    private bool _reading;
    private bool _disposed;

    internal TextEditor Editor { get; } = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Consolas, monospace"),
        ShowLineNumbers = true
    };

    internal ConsoleTextView(string path, double fontSize, bool wordWrap)
    {
        RowDefinitions = new RowDefinitions("*,Auto");
        Editor.Document.UndoStack.SizeLimit = 0;
        Editor.Background = FocusTheme.Editor;
        Editor.Foreground = FocusTheme.Text;
        SetAppearance(fontSize, wordWrap);
        AutomationProperties.SetName(Editor, "Текст консоли");
        Children.Add(Editor);
        SetRow(_status, 1);
        Children.Add(_status);

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (file.Length > TextLimit)
            {
                _earlierOutput = true;
                file.Position = file.Length - TextLimit;
                int next;
                do { next = file.ReadByte(); } while ((next & 0xC0) == 0x80);
                if (next >= 0) file.Position--;
            }
            _reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        }
        catch { file.Dispose(); throw; }

        UpdateStatus();
        _timer.Tick += ReadOutput;
        _timer.Start();
    }

    internal void SetAppearance(double size, bool wrap)
    {
        Editor.FontSize = size;
        Editor.WordWrap = wrap;
        Editor.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    internal void ReportDisconnected(string message)
    {
        _disconnected = message;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _status.Text = _disconnected ?? (_earlierOutput
            ? "Показан конец вывода. Для более ранних строк включите «Режим лога»."
            : "");
        _status.IsVisible = _status.Text.Length > 0;
    }

    private async void ReadOutput(object? sender, EventArgs e)
    {
        if (_disposed || _reading) return;
        _reading = true;
        try
        {
            var read = await _reader.ReadBlockAsync(_buffer.AsMemory());
            if (_disposed) return;
            if (read == 0)
            {
                if (_disconnected is not null) _timer.Stop();
                return;
            }

            var follow = Editor.SelectionLength == 0 && Editor.VerticalOffset >= Editor.ExtentHeight - Editor.ViewportHeight - 1;
            var document = Editor.Document;
            document.Insert(document.TextLength, new string(_buffer, 0, read));
            if (document.TextLength > TextLimit)
            {
                var excess = document.TextLength - TextLimit;
                var line = document.GetLineByOffset(excess);
                var remove = line.NextLine?.Offset ?? excess;
                if (remove == excess && char.IsLowSurrogate(document.GetCharAt(remove))
                    && char.IsHighSurrogate(document.GetCharAt(remove - 1))) remove++;
                document.Remove(0, remove);
                if (!_earlierOutput)
                {
                    _earlierOutput = true;
                    UpdateStatus();
                }
            }
            if (follow) Editor.ScrollToLine(document.LineCount);
        }
        catch (IOException error)
        {
            if (!_disposed)
            {
                ReportDisconnected($"Не удалось прочитать текст консоли. {error.Message}");
                _timer.Stop();
            }
        }
        finally
        {
            _reading = false;
            if (_disposed) _reader.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        if (!_reading) _reader.Dispose();
    }
}
