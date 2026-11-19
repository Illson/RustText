using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;

namespace RustText.Demo.Logs;

internal sealed partial class LogView
{
    private const int EditCharacterLimit = 256 * 1024;
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
    public bool HasPendingEdit => _pageWrite is not null || (_pageEdit is { } edit && _logText.Text != edit.Original);
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
            else if (EditsPagesByDefault && _pageEdit is { } edit)
            {
                SetPageText(edit.Original, _logText.CaretIndex);
                SetStatus(PageEditHint);
            }
            else await EndPageEditAsync();
        };
        _logText.TextChanged += (_, _) =>
        {
            if (_pageEdit is not null && _levelBands.IsVisible) _levelBands.SetEditedText(_logText.Text ?? "");
            if (EditsPagesByDefault) RefreshButtons();
            else { UpdatePageModeControls(); _levelFilterButton.IsEnabled = CanFilterLevels; }
        };
        panel.Children.Add(_readModeLabel);
        panel.Children.Add(_editPageButton);
        panel.Children.Add(_savePageButton);
        panel.Children.Add(_cancelPageButton);
        return panel;
    }

    private async Task TogglePageEditAsync()
    {
        if (_closed || _busy || EditsPagesByDefault || _page is not { } page) return;
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
            FocusPageEditor();
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

    private PageEdit ReadPageEdit(LogPage page, LogDataReader reader, FileStamp expected, CancellationToken token,
        long? caretOffset = null)
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
        var caret = caretOffset is { } offset
            ? encoding.GetCharCount(bytes.AsSpan(0, (int)Math.Clamp(offset - from, 0, bytes.Length))) : 0;
        return new(from, to, encoding.GetString(bytes), encoding, expected, caret);
    }

    private void BeginPageEdit(PageEdit edit)
    {
        _pageEdit = edit;
        _lineGutterScrollViewer.IsVisible = false;
        _logText.MaxLength = Math.Max(EditCharacterLimit, edit.Original.Length);
        SetPageText(edit.Original, edit.InitialCaret);
        _logText.IsReadOnly = false;
    }

    private void SetPageText(string text, int caret)
    {
        // Undo must never bring the previous page's text into the new page.
        _logText.IsUndoEnabled = false;
        _logText.Text = text;
        _logText.IsUndoEnabled = true;
        RestorePageCaret(caret, focus: false);
    }

    internal void FocusPageEditor() => _logText.Focus();

    private async Task CutCurrentPageLineAsync()
    {
        if (_closed || _pageWrite is not null || _pageEdit is not { } pageEdit
            || _logText.IsReadOnly || _logText.SelectionStart != _logText.SelectionEnd
            || TopLevel.GetTopLevel(_logText)?.Clipboard is not { } clipboard
            || _logText.Text is not { } text)
        {
            return;
        }

        var caret = _logText.CaretIndex;
        var previousLineBreak = caret == 0 ? -1 : LastLineBreak(text, caret - 1);
        var lineStart = previousLineBreak + 1;
        var lineEnd = NextLineBreak(text, caret);
        if (lineEnd < 0 && lineStart == text.Length)
        {
            return;
        }

        var lineTextEnd = lineEnd < 0 ? text.Length
            : lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1);
        var deleteStart = lineStart;
        if (lineEnd < 0 && lineStart > 0)
        {
            deleteStart--;
            if (deleteStart > 0 && text[deleteStart] == '\n' && text[deleteStart - 1] == '\r')
            {
                deleteStart--;
            }
        }
        var deleteLength = lineTextEnd - deleteStart;
        var lineText = text[lineStart..lineTextEnd];
        await clipboard.SetTextAsync(lineText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Replace("\n", Environment.NewLine, StringComparison.Ordinal));

        if (_closed || !ReferenceEquals(_pageEdit, pageEdit) || _pageWrite is not null
            || _logText.IsReadOnly || _logText.Text != text || _logText.CaretIndex != caret
            || _logText.SelectionStart != _logText.SelectionEnd)
        {
            return;
        }

        _logText.SelectionStart = deleteStart;
        _logText.SelectionEnd = deleteStart + deleteLength;
        _logText.SelectedText = "";
        _logText.CaretIndex = deleteStart;
        _logText.SelectionStart = _logText.SelectionEnd = deleteStart;
    }

    private static int LastLineBreak(string text, int start)
    {
        for (var index = start; index >= 0; index--)
        {
            if (text[index] is '\r' or '\n') return index;
        }
        return -1;
    }

    private static int NextLineBreak(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] is '\r' or '\n') return index;
        }
        return -1;
    }

    internal void RestorePageCaret(int caret, bool focus)
    {
        if (_pageEdit is null || _closed) return;
        _logText.CaretIndex = Math.Clamp(caret, 0, _logText.Text?.Length ?? 0);
        _logText.SelectionStart = _logText.SelectionEnd = _logText.CaretIndex;
        if (focus) FocusPageEditor();
    }

    public async Task SavePageEditAsync()
    {
        if (_closed || _busy || _pageWrite is not null || _pageEdit is not { } edit || _logText.Text is not { } text) return;
        if (text == edit.Original)
        {
            if (!EditsPagesByDefault) await EndPageEditAsync();
            return;
        }

        var caret = _logText.CaretIndex;
        var hadFocus = IsKeyboardFocusWithin;
        using var write = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _pageWrite = write;
        _logText.IsReadOnly = true;
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
                _logText.IsReadOnly = false;
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
        await LoadPageAsync(edit.From, saveHistory: false, allowFollowEnd: false);
        RefreshButtons();
    }

    private bool ExitPageEditForExternalRefresh()
    {
        if (_pageEdit is not { } edit) return false;
        var text = _logText.Text ?? "";
        var modified = text != edit.Original;
        if (modified) PreserveExternalPageEdit!(text);
        ClearPageEdit();
        return modified;
    }

    private void ClearPageEdit()
    {
        _pageEdit = null;
        _logText.IsReadOnly = true;
        _logText.MaxLength = 0;
        _lineGutterScrollViewer.IsVisible = true;
    }

    private void UpdatePageModeControls()
    {
        var editing = _pageEdit is not null;
        var saving = _pageWrite is not null;
        _readModeLabel.Text = _isDraft ? "РЕДАКТИРОВАНИЕ ЧЕРНОВИКА"
            : EditsPagesByDefault || editing ? "РЕДАКТИРОВАНИЕ" : "ТОЛЬКО ЧТЕНИЕ";
        _editPageButton.IsVisible = !EditsPagesByDefault;
        _editPageButton.Content = editing ? "Только чтение" : "Редактировать";
        _editPageButton.IsEnabled = !_closed && !_busy && !saving && _page is not null;
        _savePageButton.IsVisible = editing && !saving;
        _savePageButton.IsEnabled = !_busy && HasPendingEdit;
        _cancelPageButton.IsVisible = editing;
        _cancelPageButton.IsEnabled = saving || !_busy && (!EditsPagesByDefault || HasPendingEdit);
        _cancelPageButton.Content = saving ? "Остановить сохранение" : "Отменить";
    }

    private static IOException ChangedSincePageRead() => new("Файл изменился на диске. Обновите страницу перед сохранением.");

    private sealed record PageEdit(long From, long To, string Original, Encoding Encoding, FileStamp Stamp, int InitialCaret);
}
