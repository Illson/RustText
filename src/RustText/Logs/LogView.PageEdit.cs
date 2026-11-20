using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Automation;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using RustText.Demo.Editor;

namespace RustText.Demo.Logs;

internal sealed partial class LogView
{
    private TextEditor? _pageEditor;
    private GroupedLineNumberMargin? _pageLineNumbers;
    private Action? _unbindPagePaste;
    private bool _editedPageChanged;
    private int _originalPageLines;
    private readonly TextBlock _readModeLabel = new()
    {
        FontSize = 11,
        Foreground = FocusTheme.Muted,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _editPageButton = new() { Content = "Редактировать" };
    private readonly Button _savePageButton = new() { Content = "Сохранить страницу" };
    private readonly Button _cancelPageButton = new() { Content = "Отменить" };
    private PageEdit? _pageEdit;
    private CancellationTokenSource? _pageWrite;

    internal Func<string, string, long, int, bool, CancellationToken, Task>? CommitDraftPage { get; set; }
    internal Action<string>? PreserveExternalPageEdit { get; init; }
    public bool HasPendingEdit => IsWritingPage || _editedPageChanged;
    internal bool IsWritingPage => _pageWrite is not null;
    internal long? DisplayLineCount => KnownLineCount is { } total
        ? Math.Max(0, total + (_pageEdit is null ? 0 : EditedRowCount() - _originalPageLines))
        : null;

    private int EditedRowCount()
    {
        var document = _pageEditor!.Document;
        var endsWithText = _pageEdit!.To == _pageEdit.Stamp.Length && document.TextLength > 0
            && document.GetCharAt(document.TextLength - 1) is not ('\r' or '\n');
        return document.LineCount - 1 + (endsWithText ? 1 : 0);
    }

    internal void DiscardPageEdits()
    {
        if (_pageEdit is { } edit && !IsWritingPage) SetPageText(edit.Original, _pageEditor!.CaretOffset);
    }
    private bool PageNavigationBlocked => EditsPagesByDefault ? HasPendingEdit : _pageEdit is not null;

    private Control BuildPageModeControls()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        SetButtonStyle(_editPageButton, "focus-quiet");
        SetButtonStyle(_savePageButton, "focus-primary");
        SetButtonStyle(_cancelPageButton, "focus-quiet");
        ToolTip.SetTip(_editPageButton, "Открыть текущую страницу для правок");
        _editPageButton.Click += async (_, _) => await TogglePageEditAsync();
        _savePageButton.Click += async (_, _) => await SavePageEditAsync();
        _cancelPageButton.Click += async (_, _) =>
        {
            if (_pageWrite is { } write) write.Cancel();
            else if (EditsPagesByDefault && _pageEdit is not null)
            {
                DiscardPageEdits();
                SetStatus(PageEditHint);
            }
            else await EndPageEditAsync();
        };
        panel.Children.Add(_readModeLabel);
        panel.Children.Add(_editPageButton);
        panel.Children.Add(_savePageButton);
        panel.Children.Add(_cancelPageButton);
        return panel;
    }

    private async Task TogglePageEditAsync()
    {
        if (_closed || _busy || IsConsole || EditsPagesByDefault || _page is not { } page) return;
        if (_pageEdit is not null)
        {
            if (HasPendingEdit) SetStatus("Сохраните или отмените правки страницы.");
            else await EndPageEditAsync();
            return;
        }

        _busy = true;
        RefreshButtons();
        try
        {
            var reader = _reader;
            var stamp = _fileStamp;
            var token = _closing.Token;
            var edit = await Task.Run(() => ReadPageEdit(page, reader, stamp, token), token);
            if (_closed) return;
            _searchPanel.IsVisible = false;
            BeginPageEdit(edit);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_closed && ReferenceEquals(_pageEdit, edit)) FocusPageEditor();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
            SetStatus(PageEditHint);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (!_closed) SetStatus($"Не удалось открыть страницу для правок. {error.Message}");
        }
        finally
        {
            _busy = false;
            if (!_closed) RefreshButtons();
        }
    }

    private const string PageEditHint = "Ctrl+S сохраняет изменения страницы. Перед переходом сохраните или отмените правки.";

    private PageEdit ReadPageEdit(LogPage page, LogDataReader reader, FileStamp expected, CancellationToken token)
    {
        var from = page.Rows.Count == 0
            ? Math.Min(expected.Length, Math.Max(reader.PreambleLength, page.StartOffset))
            : page.Rows[0].StartOffset;
        var to = Math.Max(from, page.NextOffset);
        var encoding = DocumentFiles.GetEncoding(reader.Encoding);
        using var source = LogDataReader.Open(_path);
        if (ReadFileStamp(_path, source) != expected) throw ChangedSincePageRead();
        source.Position = from;
        var bytes = new byte[checked((int)(to - from))];
        source.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        if (ReadFileStamp(_path, source) != expected) throw ChangedSincePageRead();
        return new(from, to, encoding.GetString(bytes), encoding, expected);
    }

    private void BeginPageEdit(PageEdit edit)
    {
        StopFollowingEnd();
        _pageEditor ??= CreatePageEditor();
        _pageEdit = edit;
        _pageLineNumbers!.FirstLineNumber = _displaySourceLineNumbers.FirstOrDefault();
        var filtering = _levelFilterViewport?.IsVisible == true;
        _lineGutterScrollViewer.IsVisible = filtering;
        _logText.IsVisible = false;
        _pageEditor.IsVisible = !filtering;
        _pageEditor.IsReadOnly = false;
        SetPageText(edit.Original, 0);
    }

    private TextEditor CreatePageEditor()
    {
        var editor = new TextEditor
        {
            FontFamily = _logText.FontFamily, FontSize = _logText.FontSize,
            Background = _logText.Background, Foreground = _logText.Foreground,
            WordWrap = _wordWrap, Padding = _logText.Padding,
            ShowLineNumbers = true, LineNumbersForeground = _lineGutterText.Foreground,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        _pageLineNumbers = GroupedLineNumberMargin.ReplaceExisting(editor);
        EditorCaret.Attach(editor.TextArea);
        var levels = new LogLevelColorizer { Enabled = !_isDraft && (!EditsPagesByDefault || LogLevels.IsLogPath(_path)) };
        editor.TextArea.TextView.LineTransformers.Add(levels);
        editor.TextArea.TextView.BackgroundRenderers.Add(levels);
        editor.TextChanged += (_, _) =>
        {
            if (_pageEdit is not { } edit || _closed) return;
            var changed = editor.Document.TextLength != edit.Original.Length || editor.Text != edit.Original;
            if (changed != _editedPageChanged)
            {
                _editedPageChanged = changed;
                RefreshButtons();
            }
            UpdateDisplayedLineCount();
            TextPositionChanged?.Invoke();
        };
        editor.TextArea.Caret.PositionChanged += (_, _) => TextPositionChanged?.Invoke();
        editor.TextArea.SelectionChanged += (_, _) => TextPositionChanged?.Invoke();
        editor.TextArea.AddHandler(KeyDownEvent, OnLogKeyDown, RoutingStrategies.Tunnel);
        _unbindPagePaste = EditorClipboard.BindPaste(editor, () => !_closed && _pageEdit is not null && !IsWritingPage,
            editor.TextArea.PerformTextInput, RunPageClipboard);

        editor.LayoutUpdated += OnLogTextLayoutUpdated;
        AutomationProperties.SetAutomationId(editor, "LogPageEditor");
        Grid.SetColumn(editor, 1);
        ((Grid)_logText.Parent!).Children.Add(editor);
        return editor;
    }

    private void SetPageText(string text, int caret)
    {
        var editor = _pageEditor!;
        editor.Document = new TextDocument(text);
        _originalPageLines = EditedRowCount();
        _editedPageChanged = false;
        RestorePageCaret(caret, focus: false);
        RefreshButtons();
        UpdateDisplayedLineCount();
        TextPositionChanged?.Invoke();
    }

    internal void FocusPageEditor()
    {
        if (_pageEdit is not null) _pageEditor!.TextArea.Focus();
        else _logText.Focus();
    }

    private async void RunPageClipboard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            if (!_closed) SetStatus($"Буфер обмена недоступен. {error.Message}");
        }
    }

    internal void RestorePageCaret(int caret, bool focus)
    {
        if (_pageEdit is null || _closed) return;
        _pageEditor!.Select(Math.Clamp(caret, 0, _pageEditor.Document.TextLength), 0);
        if (focus) FocusPageEditor();
    }

    public async Task SavePageEditAsync()
    {
        if (_closed || _busy || _pageWrite is not null || _pageEdit is not { } edit || _pageEditor is not { } editor) return;
        var text = editor.Text;
        if (text == edit.Original)
        {
            if (!EditsPagesByDefault) await EndPageEditAsync();
            return;
        }

        var caret = editor.CaretOffset;
        var hadFocus = IsKeyboardFocusWithin;
        using var write = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _pageWrite = write;
        editor.IsReadOnly = true;
        RefreshButtons();
        SetStatus("Сохранение страницы…");
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, $".rusttext-page-{Guid.NewGuid():N}.pending");
        var committedInPlace = false;
        try
        {
            var hash = await Task.Run(() => StageEditedPage(edit, text, temporary, write.Token), write.Token);
            if (_isDraft)
            {
                write.Token.ThrowIfCancellationRequested();
                var publish = CommitDraftPage ?? throw new IOException("Черновик недоступен для сохранения.");
                await publish(temporary, hash!, edit.From, caret, hadFocus, write.Token);
            }
            else
            {
                committedInPlace = true;
                ResetFileHandle();
            }
        }
        catch (OperationCanceledException) when (write.IsCancellationRequested)
        {
            if (!_closed) SetStatus("Сохранение страницы отменено.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            if (!_closed) SetStatus($"Не удалось сохранить страницу. {error.Message}");
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            _pageWrite = null;
            if (!_closed)
            {
                editor.IsReadOnly = false;
                RefreshButtons();
            }
        }

        if (committedInPlace && !_closed)
        {
            await EndPageEditAsync();
            if (EditsPagesByDefault) RestorePageCaret(caret, hadFocus && IsKeyboardFocusWithin);
            StartIndexIfNeeded();
            LoadMinimapSamples();
            SetStatus("Страница сохранена.");
        }
    }

    private string? StageEditedPage(PageEdit edit, string text, string temporary, CancellationToken token)
    {
        var replacement = edit.Encoding.GetBytes(text);
        using var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            1024 * 1024, FileOptions.SequentialScan);
        if (ReadFileStamp(_path, source) != edit.Stamp) throw ChangedSincePageRead();
        source.Position = edit.From;
        var originalBytes = new byte[checked((int)(edit.To - edit.From))];
        source.ReadExactly(originalBytes);
        if (!originalBytes.AsSpan().SequenceEqual(edit.Encoding.GetBytes(edit.Original))) throw ChangedSincePageRead();
        source.Position = 0;
        using var digest = _isDraft ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
        {
            var buffer = new byte[1024 * 1024];
            CopyRange(edit.From);
            Write(replacement);
            source.Position = edit.To;
            CopyRange(edit.Stamp.Length - edit.To);
            output.Flush(flushToDisk: true);

            void CopyRange(long remaining)
            {
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(remaining, buffer.Length);
                    source.ReadExactly(buffer.AsSpan(0, count));
                    Write(buffer.AsSpan(0, count));
                    remaining -= count;
                }
            }

            void Write(ReadOnlySpan<byte> data)
            {
                output.Write(data);
                digest?.AppendData(data);
            }
        }

        token.ThrowIfCancellationRequested();
        if (ReadFileStamp(_path, source) != edit.Stamp) throw ChangedSincePageRead();
        if (digest is not null) return Convert.ToHexString(digest.GetHashAndReset());
        File.Replace(temporary, _path, destinationBackupFileName: null);
        return null;
    }

    private async Task EndPageEditAsync()
    {
        if (_closed || _pageEdit is not { } edit) return;
        ClearPageEdit();
        await LoadPageAsync(edit.From, saveHistory: false);
        RefreshButtons();
    }

    private bool ExitPageEditForExternalRefresh()
    {
        if (_pageEdit is not { } edit) return false;
        var text = _pageEditor!.Text;
        var modified = text != edit.Original;
        if (modified) PreserveExternalPageEdit!(text);
        ClearPageEdit();
        return modified;
    }

    private void ClearPageEdit()
    {
        _pageEdit = null;
        _editedPageChanged = false;
        if (_pageEditor is { } editor)
        {
            editor.IsVisible = false;
            editor.Document = new TextDocument();
        }
        _logText.IsVisible = _levelFilterViewport?.IsVisible != true;
        _lineGutterScrollViewer.IsVisible = true;
    }

    private void UpdatePageModeControls()
    {
        var editing = _pageEdit is not null;
        var saving = _pageWrite is not null;
        _readModeLabel.Text = _isDraft && (editing || EditsPagesByDefault) ? "РЕДАКТИРОВАНИЕ ЧЕРНОВИКА"
            : EditsPagesByDefault || editing ? "РЕДАКТИРОВАНИЕ" : "ТОЛЬКО ЧТЕНИЕ";
        _editPageButton.IsVisible = !EditsPagesByDefault && !IsConsole;
        _editPageButton.Content = editing ? "Только чтение" : "Редактировать";
        _editPageButton.IsEnabled = !_closed && !_busy && !saving && _page is not null;
        _savePageButton.IsVisible = editing && !saving;
        _savePageButton.IsEnabled = !_busy && HasPendingEdit;
        _cancelPageButton.IsVisible = editing;
        _cancelPageButton.IsEnabled = saving || !_busy && (!EditsPagesByDefault || HasPendingEdit);
        _cancelPageButton.Content = saving ? "Остановить сохранение" : "Отменить";
    }

    private static IOException ChangedSincePageRead() => new("Файл изменился на диске. Обновите страницу перед сохранением.");

    private sealed record PageEdit(long From, long To, string Original, Encoding Encoding, FileStamp Stamp);
}
