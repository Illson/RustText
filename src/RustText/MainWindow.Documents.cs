using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private const long LogThreshold = 50L * 1024 * 1024;
    private const int MaxEditorCharacters = 8 * 1024 * 1024;
    private const int MaxEditorLines = 50_000;
    private const int MaxEditorLineLength = 64 * 1024;
    private static readonly FilePickerFileType TextFiles = new("Текст и логи") { Patterns = ["*.txt", "*.log", "*.json", "*.xml"] };
    private bool _confirmingTabClose;

    private void NewTab(string text = "")
    {
        var tab = AddTab();
        if (IsOversizedEditorText(text))
        {
            StartLargeDraftPersistence(tab, () => new StringReader(text), null, text);
            return;
        }

        AttachEditor(tab, text);
        if (text.Length > 0)
        {
            tab.RestoredDirty = tab.Dirty = true;
            UpdateTab(tab);
        }

        tab.Editor?.Focus();
    }

    private async Task OpenDialogAsync(bool asLog = false, TextEncodingKind? encoding = null)
    {
        var items = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = asLog ? "Открыть как лог" : "Открыть файлы",
            AllowMultiple = true,
            FileTypeFilter = [TextFiles, FilePickerFileTypes.All]
        });
        var paths = new List<string>();
        foreach (var item in items)
        {
            using (item)
            {
                if (item.TryGetLocalPath() is { } path)
                {
                    paths.Add(path);
                }
            }
        }

        await OpenPathsAsync(paths, asLog, encoding);
    }

    private async Task OpenPathsAsync(IEnumerable<string> paths, bool asLog = false, TextEncodingKind? encoding = null)
    {
        foreach (var path in paths)
        {
            await GuardAsync(() => OpenPathAsync(path, asLog, encoding));
        }
    }

    private async Task OpenPathAsync(string path, bool asLog = false, TextEncodingKind? encoding = null)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Файл не найден. {fullPath}", fullPath);
        }

        var useLog = asLog || file.Length > LogThreshold;
        var existing = _tabs.Values.FirstOrDefault(tab => SamePath(tab.Path, fullPath) && tab.IsLog == useLog);
        if (existing is not null)
        {
            Select(existing);
            if (encoding is not null && (existing.Editor is not null || existing.Log is not null) && !existing.Dirty)
            {
                await ReloadAsync(existing, encoding);
            }

            return;
        }

        var tab = AddTab();
        tab.Path = fullPath;
        tab.Encoding = encoding ?? TextEncodingKind.Utf8;
        try
        {
            if (useLog)
            {
                AttachLog(tab, fullPath, encoding);
            }
            else
            {
                await LoadFileAsync(tab, encoding);
            }

            await RecordRecentAsync(tab);
        }
        catch
        {
            RemoveTab(tab);
            throw;
        }
    }

    private async Task LoadFileAsync(WorkTab tab, TextEncodingKind? encoding = null)
    {
        var path = tab.Path ?? throw new InvalidOperationException("У вкладки нет пути.");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        tab.Loading = true;
        tab.LoadCancellation = source;
        tab.Item.Content = LoadingSurface(source, "Чтение файла…");
        UpdateTab(tab);
        try
        {
            var loaded = await DocumentFiles.ReadAsync(path, encoding, source.Token);
            source.Token.ThrowIfCancellationRequested();
            AttachLoadedEditor(tab, loaded);
        }
        finally
        {
            tab.Loading = false;
            tab.LoadCancellation = null;
        }
    }

    private void AttachLoadedEditor(WorkTab tab, LoadedDocument loaded)
    {
        tab.Stamp = loaded.Stamp;
        tab.DiskStatus = null;
        tab.Encoding = loaded.Encoding;
        tab.Ending = loaded.LineEnding;
        tab.OriginalEndings = loaded.OriginalLineEndings;
        AttachEditor(tab, loaded.Text);
    }

    private void AttachEditor(WorkTab tab, string text)
    {
        var document = new TextDocument(text);
        document.UndoStack.MarkAsOriginalFile();
        AttachEditor(tab, document, resetDirtyState: true);
    }

    private void AttachEditor(WorkTab tab, TextDocument document)
    {
        AttachEditor(tab, document, resetDirtyState: false);
    }

    private void AttachEditor(WorkTab tab, TextDocument document, bool resetDirtyState)
    {
        EnsureIdleUnloadTimer();
        tab.Log?.Close();
        tab.Log = null;
        tab.LastWorked = DateTimeOffset.Now;
        var editor = new TextEditor
        {
            Document = document,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = _fontSize,
            Background = FocusTheme.Editor,
            Foreground = FocusTheme.Text,
            LineNumbersForeground = FocusTheme.Gutter,
            LineNumbersMargin = new Thickness(8, 0, 12, 0),
            WordWrap = _wordWrap,
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        GroupedLineNumberMargin.ReplaceExisting(editor);
        editor.TextArea.SelectionBrush = FocusTheme.Selection;
        editor.Options.ShowBoxForControlCharacters = true;
        editor.TextArea.TextView.LineTransformers.Add(new JsonColorizer());
        tab.Editor = editor;
        SetSyntax(editor, tab.Path);
        if (resetDirtyState)
        {
            tab.RestoredDirty = false;
            tab.Dirty = false;
        }
        tab.Highlights = new SearchHighlights(editor);
        editor.TextArea.TextView.BackgroundRenderers.Add(tab.Highlights);
        var undoStack = document.UndoStack;

        var warning = new TextBlock
        {
            Foreground = FocusTheme.Danger,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
        };
        var jump = Button("Перейти к ошибке", () =>
        {
            if (tab.JsonError is { } error)
            {
                SelectText(tab, error.Offset, 0);
            }
        });
        var disable = Button("Отключить проверку", () =>
        {
            tab.JsonDisabled = true;
            ScheduleJsonValidation(tab);
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(warning);
        actions.Children.Add(jump);
        actions.Children.Add(disable);
        var banner = new Border
        {
            Background = FocusTheme.DangerSurface,
            Padding = new Thickness(10, 5),
            IsVisible = false,
            Child = actions
        };
        tab.JsonBanner = banner;
        tab.JsonMessage = warning;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetColumnSpan(banner, 2);
        grid.Children.Add(banner);
        Grid.SetRow(editor, 1);
        grid.Children.Add(editor);
        var minimap = new DocumentMinimap(editor);
        Grid.SetRow(minimap, 1);
        Grid.SetColumn(minimap, 1);
        grid.Children.Add(minimap);
        tab.Item.Content = grid;
        System.ComponentModel.PropertyChangedEventHandler undoHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(undoStack.IsOriginalFile) && ReferenceEquals(tab.Editor, editor))
            {
                tab.Dirty = tab.RestoredDirty || !undoStack.IsOriginalFile;
                UpdateTab(tab);
            }
        };
        undoStack.PropertyChanged += undoHandler;
        EventHandler textChangedHandler = (_, _) =>
        {
            tab.Revision++;
            tab.LastWorked = DateTimeOffset.Now;
            if (tab.Path is null)
            {
                SetSyntax(editor, null);
            }
            UpdateTab(tab);
            ScheduleJsonValidation(tab);
            if (ReferenceEquals(tab, ActiveTab))
            {
                RefreshSearchHighlights();
            }
        };
        editor.TextChanged += textChangedHandler;
        EventHandler caretHandler = (_, _) =>
        {
            if (ReferenceEquals(tab, ActiveTab))
            {
                UpdateStatus(tab);
            }
        };
        editor.TextArea.Caret.PositionChanged += caretHandler;
        EventHandler<Avalonia.Input.TextInputEventArgs> textEnteringHandler = (_, e) =>
        {
            if (e.Text is not { Length: > 0 } insertion || tab.Loading
                || !WouldExceedEditorLimits(editor, insertion))
            {
                return;
            }

            var start = editor.SelectionStart;
            var length = editor.SelectionLength;
            var snapshot = document.CreateSnapshot();
            e.Handled = true;
            StartLargeDraftPersistence(tab,
                () => new ReplacingTextReader(snapshot.CreateReader(), start, length, insertion), editor, insertion,
                start, length);
        };
        editor.TextArea.TextEntering += textEnteringHandler;
        EventHandler<CanExecuteRoutedEventArgs> pasteCanExecute = (_, e) =>
        {
            e.CanExecute = editor.IsEnabled;
            e.Handled = true;
        };
        EventHandler<ExecutedRoutedEventArgs> pasteExecuted = (_, e) =>
        {
            e.Handled = true;
            if (editor.IsEnabled)
            {
                Run(() => PasteSafelyAsync(tab, editor));
            }
        };
        var pasteBinding = new RoutedCommandBinding(ApplicationCommands.Paste, pasteExecuted, pasteCanExecute);
        editor.TextArea.CommandBindings.Insert(0, pasteBinding);
        tab.EditorSubscriptions =
        [
            () => undoStack.PropertyChanged -= undoHandler,
            () => editor.TextChanged -= textChangedHandler,
            () => editor.TextArea.Caret.PositionChanged -= caretHandler,
            () => editor.TextArea.TextEntering -= textEnteringHandler,
            () => editor.TextArea.CommandBindings.Remove(pasteBinding)
        ];
        tab.Minimap = minimap;
        UpdateTab(tab);
        ScheduleJsonValidation(tab);
    }

    private async Task PasteSafelyAsync(WorkTab tab, TextEditor editor)
    {
        var start = editor.SelectionStart;
        var length = editor.SelectionLength;
        var snapshot = editor.Document.CreateSnapshot();
        var topLevel = TopLevel.GetTopLevel(editor);
        if (topLevel?.Clipboard is not { } clipboard)
        {
            return;
        }

        editor.IsEnabled = false;
        var persistenceStarted = false;
        try
        {
            var transfer = await clipboard.TryGetDataAsync();
            var text = transfer is null ? null : await transfer.TryGetTextAsync();
            if (string.IsNullOrEmpty(text) || !ReferenceEquals(tab.Editor, editor))
            {
                return;
            }

            if (WouldExceedEditorLimits(editor, text))
            {
                StartLargeDraftPersistence(tab,
                    () => new ReplacingTextReader(snapshot.CreateReader(), start, length, text), editor, text,
                    start, length);
                persistenceStarted = true;
                return;
            }

            editor.IsEnabled = true;
            editor.TextArea.PerformTextInput(NormalizeBoundedPaste(editor, text));
        }
        finally
        {
            if (!persistenceStarted && ReferenceEquals(tab.Editor, editor))
            {
                editor.IsEnabled = true;
            }
        }
    }

    private static string NormalizeBoundedPaste(TextEditor editor, string text)
    {
        if (text.Contains('\r'))
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }
        if (editor.Options.ConvertTabsToSpaces && text.Contains('\t'))
        {
            text = text.Replace("\t", new string(' ', Math.Max(1, editor.Options.IndentationSize)), StringComparison.Ordinal);
        }
        return text;
    }

    private void StartLargeDraftPersistence(
        WorkTab tab,
        Func<TextReader> openReader,
        TextEditor? editor,
        string insertedText,
        int selectionStart = 0,
        int selectionLength = 0)
    {
        using var titleReader = openReader();
        var title = ReadUntitledTitle(titleReader);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        tab.LoadCancellation?.Cancel();
        tab.LoadCancellation = source;
        tab.Loading = true;
        if (editor is not null)
        {
            editor.IsEnabled = false;
        }
        else
        {
            tab.Item.Content = new TextBlock { Text = "Сохраняю большой черновик…", Margin = new Thickness(16) };
        }

        Run(async () =>
        {
            var saveGateAcquired = false;
            try
            {
                await _workspace.SaveGate.WaitAsync(source.Token);
                saveGateAcquired = true;
                var saved = CaptureTab(tab) with
                {
                    SelectionStart = selectionStart + insertedText.Length,
                    SelectionLength = 0,
                    Reversed = false,
                    LineCount = null,
                    DraftOffset = 0,
                    UntitledName = title
                };
                saved = await _storage.SaveClosedDraftAsync(saved, openReader, source.Token);
                saved = saved with { DraftOffset = 0 };
                source.Token.ThrowIfCancellationRequested();
                if (!_tabs.ContainsKey(tab.Id) || !ReferenceEquals(tab.Editor, editor))
                {
                    return;
                }

                tab.Dirty = true;
                tab.RestoredDirty = true;
                tab.Revision++;
                tab.LastWorked = DateTimeOffset.Now;
                if (editor is not null)
                {
                    _ = DetachEditor(tab);
                }
                AttachDraftPreview(tab, saved, _storage.GetDraftPath(saved)!);
                UpdateTab(tab);
            }
            finally
            {
                tab.Loading = false;
                if (ReferenceEquals(tab.LoadCancellation, source))
                {
                    tab.LoadCancellation = null;
                }
                if (editor is not null && ReferenceEquals(tab.Editor, editor))
                {
                    editor.IsEnabled = true;
                }
                if (saveGateAcquired)
                {
                    _workspace.SaveGate.Release();
                }
                source.Dispose();
            }
        });
    }

    private static bool WouldExceedEditorLimits(TextEditor editor, string insertion)
    {
        var document = editor.Document;
        var start = editor.SelectionStart;
        var end = start + editor.SelectionLength;
        var projectedCharacters = document.TextLength - editor.SelectionLength + insertion.Length;
        if (projectedCharacters > MaxEditorCharacters)
        {
            return true;
        }

        var firstLine = document.GetLineByOffset(Math.Clamp(start, 0, document.TextLength));
        var lastLine = document.GetLineByOffset(Math.Clamp(end, 0, document.TextLength));
        var projectedLines = document.LineCount - (lastLine.LineNumber - firstLine.LineNumber);
        var insertedLines = 0;
        var longestInsertedLine = 0;
        var currentLineLength = 0;
        var previousCarriageReturn = false;
        foreach (var character in insertion)
        {
            if (character == '\r')
            {
                insertedLines++;
                longestInsertedLine = Math.Max(longestInsertedLine, currentLineLength);
                currentLineLength = 0;
                previousCarriageReturn = true;
            }
            else if (character == '\n')
            {
                if (!previousCarriageReturn)
                {
                    insertedLines++;
                    longestInsertedLine = Math.Max(longestInsertedLine, currentLineLength);
                    currentLineLength = 0;
                }
                previousCarriageReturn = false;
            }
            else
            {
                currentLineLength++;
                previousCarriageReturn = false;
            }
        }
        longestInsertedLine = Math.Max(longestInsertedLine, currentLineLength);
        if (projectedLines + insertedLines > MaxEditorLines || longestInsertedLine > MaxEditorLineLength)
        {
            return true;
        }

        if (insertedLines == 0)
        {
            var lineSelectionLength = start < firstLine.EndOffset && end > firstLine.Offset
                ? Math.Min(end, firstLine.EndOffset) - Math.Max(start, firstLine.Offset)
                : 0;
            return firstLine.Length - lineSelectionLength + insertion.Length > MaxEditorLineLength;
        }

        return false;
    }

    private static bool IsOversizedEditorText(string text)
    {
        if (text.Length > MaxEditorCharacters)
        {
            return true;
        }

        var lineLength = 0;
        var lineCount = 1;
        var previousCarriageReturn = false;
        foreach (var character in text)
        {
            if (character == '\r')
            {
                if (lineLength > MaxEditorLineLength || ++lineCount > MaxEditorLines)
                {
                    return true;
                }
                lineLength = 0;
                previousCarriageReturn = true;
            }
            else if (character == '\n')
            {
                if (!previousCarriageReturn)
                {
                    if (lineLength > MaxEditorLineLength || ++lineCount > MaxEditorLines)
                    {
                        return true;
                    }
                    lineLength = 0;
                }
                previousCarriageReturn = false;
            }
            else if (++lineLength > MaxEditorLineLength)
            {
                return true;
            }
            else
            {
                previousCarriageReturn = false;
            }
        }
        return false;
    }

    private static string ReadUntitledTitle(TextReader reader)
    {
        var buffer = new char[1600];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = reader.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            var newline = buffer.AsSpan(count, read).IndexOfAny('\r', '\n');
            count += newline >= 0 ? newline : read;
            if (newline >= 0) break;
        }
        var firstLine = new string(buffer, 0, count).Trim();
        if (firstLine.Length == 0) return "Без имени";
        var builder = new StringBuilder();
        foreach (var rune in firstLine.EnumerateRunes().Take(10)) builder.Append(rune);
        return builder.ToString();
    }

    private AvaloniaEdit.Document.TextDocument? DetachEditor(WorkTab tab)
    {
        if (tab.Editor is not { } editor)
        {
            return null;
        }

        tab.JsonCancellation?.Cancel();
        tab.JsonCancellation = null;
        foreach (var unsubscribe in tab.EditorSubscriptions)
        {
            unsubscribe();
        }
        tab.EditorSubscriptions = [];
        if (tab.Highlights is { } highlights)
        {
            editor.TextArea.TextView.BackgroundRenderers.Remove(highlights);
        }
        tab.Highlights = null;
        tab.Minimap?.Dispose();
        tab.Minimap = null;
        var document = editor.Document;
        editor.Document = new TextDocument();
        tab.Editor = null;
        tab.JsonBanner = null;
        tab.JsonMessage = null;
        return document;
    }

    private void AttachLog(
        WorkTab tab,
        string path,
        TextEncodingKind? encoding = null,
        LogTabState? state = null,
        string? draftName = null,
        long? draftLineCount = null)
    {
        tab.Log?.Close();
        _ = DetachEditor(tab);
        tab.Editor = null;
        var log = new LogView(path, encoding, state, draftName, draftLineCount) { WordWrap = state?.WordWrap ?? _wordWrap };
        log.SetTextSize(_fontSize);
        tab.Log = log;
        log.StatusChanged += status =>
        {
            if (ReferenceEquals(tab, ActiveTab))
            {
                SetStatus(status.Length == 0 ? tab.Path : status);
            }
        };
        tab.Item.Content = log;
        UpdateTab(tab);
    }

    private async Task ReloadAsync(WorkTab tab, TextEncodingKind? encoding = null)
    {
        if (tab.Path is not { } path)
        {
            return;
        }

        if (tab.Dirty)
        {
            throw new InvalidOperationException("Сначала сохраните изменения.");
        }

        if (tab.Log is not null)
        {
            AttachLog(tab, path, encoding);
            return;
        }

        if (tab.Editor is { } editor)
        {
            await ApplyEditorReloadAsync(tab, editor, DocumentFiles.ReadAsync(path, encoding, _lifetime.Token));
        }
    }

    private async Task ApplyEditorReloadAsync(WorkTab tab, TextEditor editor, Task<LoadedDocument> read)
    {
        var revision = tab.Revision;
        var path = tab.Path;
        var loaded = await read;
        if (_closed || !_tabs.TryGetValue(tab.Id, out var current) || !ReferenceEquals(tab, current)
            || !ReferenceEquals(tab.Editor, editor) || !SamePath(tab.Path, path))
        {
            return;
        }
        if (tab.Dirty || tab.Revision != revision)
        {
            if (ReferenceEquals(tab, ActiveTab))
            {
                SetStatus("Перечитывание отменено: документ изменён.");
            }
            return;
        }

        var view = CaptureTab(tab);
        var hadFocus = editor.IsKeyboardFocusWithin;
        AttachLoadedEditor(tab, loaded);
        RestoreEditorView(tab, view);
        if (ReferenceEquals(tab, ActiveTab))
        {
            if (hadFocus)
            {
                tab.Editor?.Focus();
            }
            SetStatus("Файл перечитан.");
        }
    }

    private async Task SaveAsync(bool saveAs = false)
    {
        if (ActiveTab is not { Editor: { } editor } tab)
        {
            return;
        }

        var path = tab.Path;
        FileStamp? expected = tab.Stamp;
        if (saveAs || path is null)
        {
            using var item = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить документ",
                SuggestedFileName = path is null ? "текст.txt" : Path.GetFileName(path),
                FileTypeChoices = [TextFiles, FilePickerFileTypes.All]
            });
            var target = item?.TryGetLocalPath();
            if (target is null)
            {
                return;
            }

            if (!SamePath(path, target) || expected is null)
            {
                expected = File.Exists(target) ? await DocumentFiles.GetStampAsync(target, _lifetime.Token) : null;
            }
            path = target;
        }

        var duplicate = _tabs.Values.FirstOrDefault(other => other != tab && !other.IsLog && SamePath(other.Path, path));
        if (duplicate is not null)
        {
            throw new IOException("Этот файл уже открыт в другой вкладке.");
        }

        var text = editor.Text;
        var revision = tab.Revision;
        var iconPosition = expected is null ? null : await DesktopIconPosition.CaptureAsync(path, _lifetime.Token);
        var preSaveVersionWarning = expected is null ? null : await CaptureVersionAsync(path, tab.Encoding, expected);
        var stamp = await DocumentFiles.SaveAsync(path, text, tab.Encoding, tab.Ending, expected, tab.OriginalEndings, _lifetime.Token);
        tab.Path = path;
        SetSyntax(editor, path);
        tab.Stamp = stamp;
        if (iconPosition is not null)
        {
            _ = iconPosition.RestoreAsync(path, stamp);
        }
        tab.DiskStatus = null;
        tab.LastWorked = DateTimeOffset.Now;
        if (tab.Revision == revision)
        {
            tab.RestoredDirty = false;
            tab.Dirty = false;
            editor.Document.UndoStack.MarkAsOriginalFile();
        }

        UpdateTab(tab);
        await RecordRecentAsync(tab);
        var postSaveVersionWarning = await CaptureVersionAsync(path, tab.Encoding, stamp);
        var versionWarning = preSaveVersionWarning ?? postSaveVersionWarning;
        SetStatus(versionWarning is null
            ? $"Сохранено · {path}"
            : $"Сохранено · {path}. Версию не удалось записать: {versionWarning}");
    }

    private async Task CloseTabAsync(WorkTab tab)
    {
        if (_closing || !_tabs.ContainsKey(tab.Id) || _confirmingTabClose || tab.Closing)
        {
            return;
        }

        tab.Closing = true;
        try
        {
            if (tab is { Dirty: true, Path: { } path, Recovery: null })
            {
                var body = DialogPanel(new TextBlock
                {
                    Text = $"Вкладка «{Path.GetFileName(path)}» содержит несохранённый текст.",
                    TextWrapping = TextWrapping.Wrap
                });
                var dialog = Dialog("Закрыть без сохранения?", body);
                var cancel = FocusButton(Button("Отмена", () => dialog.Close(false)));
                cancel.IsCancel = true;
                var discard = Button("Закрыть", () => dialog.Close(true));
                discard.Classes.Add("focus-danger");
                body.Children.Add(Row(cancel, discard));
                _confirmingTabClose = true;
                try
                {
                    if (!await dialog.ShowDialog<bool>(this))
                    {
                        return;
                    }
                }
                finally
                {
                    _confirmingTabClose = false;
                }
            }

            if (!_tabs.ContainsKey(tab.Id) || _closing)
            {
                return;
            }

            await RememberClosedAsync(tab);
            await RecordRecentAsync(tab);
            RemoveTab(tab);
            if (_tabs.Count == 0)
            {
                await ShowHomeAsync();
            }
        }
        finally
        {
            tab.Closing = false;
        }
    }

    private void RemoveTab(WorkTab tab)
    {
        tab.LoadCancellation?.Cancel();
        tab.JsonCancellation?.Cancel();
        tab.Log?.Close();
        tab.Log = null;
        _ = DetachEditor(tab);
        _tabs.Remove(tab.Id);
        tab.Group.Tabs.Items.Remove(tab.Item);
        if (ReferenceEquals(_active, tab.Group) && SelectedIn(_active) is { } next)
        {
            ActivateTab(next);
        }
    }

    private void Select(WorkTab tab)
    {
        if (_home)
        {
            ShowGroups();
        }

        _active = tab.Group;
        tab.Group.Tabs.SelectedItem = tab.Item;
        ActivateTab(tab);
    }

    private void SelectText(WorkTab tab, int offset, int count)
    {
        Select(tab);
        if (tab.Editor is not { } editor)
        {
            return;
        }

        offset = Math.Clamp(offset, 0, editor.Document.TextLength);
        count = Math.Clamp(count, 0, editor.Document.TextLength - offset);
        editor.Select(offset, count);
        editor.CaretOffset = offset + count;
        editor.ScrollToLine(editor.Document.GetLineByOffset(offset).LineNumber);
        editor.Focus();
    }

    private static Control LoadingSurface(CancellationTokenSource source, string message)
    {
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
        content.Children.Add(new TextBlock { Text = message });
        content.Children.Add(Button("Отменить загрузку", source.Cancel));
        return content;
    }

    private void UpdateTab(WorkTab tab)
    {
        var title = tab.Path is { } path ? Path.GetFileName(path) : UntitledName(tab);
        tab.Label.Text = title;
        tab.DirtyMark.Opacity = tab.Dirty ? 1 : 0;
        AutomationProperties.SetName(tab.Item, title + (tab.Dirty ? ", изменено" : ""));
        ToolTip.SetTip(tab.Label, tab.Path ?? title);
        if (ReferenceEquals(tab, ActiveTab))
        {
            UpdateStatus(tab);
        }
    }

    private string UntitledName(WorkTab tab)
    {
        if (tab.Editor?.Document is not { } document || document.TextLength == 0)
        {
            return (tab.Deferred ?? tab.Recovery)?.UntitledName ?? "Без имени";
        }

        var line = document.GetLineByNumber(1);
        var raw = document.GetText(line.Offset, Math.Min(line.Length, 1600)).Trim();
        if (raw.Length == 0)
        {
            return "Без имени";
        }

        var builder = new StringBuilder();
        foreach (var rune in raw.EnumerateRunes().Take(_titleLimit))
        {
            builder.Append(rune);
        }

        return builder.ToString();
    }

    private void UpdateStatus(WorkTab tab)
    {
        SetCaption(tab.Label.Text);
        if (tab.Editor is { } editor)
        {
            var status = tab.Dirty ? "Изменено" : "Сохранено";
            SetStatus($"{status} · {tab.Path ?? "Без имени"}{(tab.DiskStatus is { } disk ? $" · {disk}" : "")}");
            FormatText.Text = $"Стр. {LineNumbers.Format(editor.TextArea.Caret.Line)}, стлб. {LineNumbers.Format(editor.TextArea.Caret.Column)} · {EncodingName(tab.Encoding)} · {tab.Ending}";
        }
        else
        {
            SetStatus(tab.Log?.StatusText is { Length: > 0 } message ? message : tab.Path);
            FormatText.Text = tab.IsLog ? "Просмотр лога" : "";
        }
    }

    private static void SetSyntax(TextEditor editor, string? path)
    {
        var colorizer = editor.TextArea.TextView.LineTransformers.OfType<JsonColorizer>().Single();
        var enabled = JsonColorizer.Applies(path, editor.Document);
        if (colorizer.Enabled != enabled)
        {
            colorizer.Enabled = enabled;
            editor.TextArea.TextView.Redraw();
        }

        editor.SyntaxHighlighting = enabled || path is null ? null
            : HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
    }

    private static string EncodingName(TextEncodingKind value) => value switch
    {
        TextEncodingKind.Utf8Bom => "UTF-8 BOM",
        TextEncodingKind.Utf16LittleEndian => "UTF-16 LE",
        TextEncodingKind.Utf16BigEndian => "UTF-16 BE",
        TextEncodingKind.Windows1251 => "Windows-1251",
        _ => "UTF-8"
    };

    private bool SamePath(string? left, string? right) => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void ScheduleJsonValidation(WorkTab tab)
    {
        tab.JsonCancellation?.Cancel();
        tab.JsonError = null;
        if (tab.JsonBanner is not { } banner || tab.Editor is not { } editor)
        {
            return;
        }

        banner.IsVisible = false;
        if (tab.JsonDisabled || _closed)
        {
            return;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        tab.JsonCancellation = source;
        _ = ValidateJsonAsync(tab, editor, source);
    }

    private async Task ValidateJsonAsync(WorkTab tab, TextEditor editor, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(270, source.Token);
            var text = editor.Text;
            if (!TextOperations.LooksLikeJson(text, tab.Path))
            {
                return;
            }

            var problem = await Task.Run(() => TextOperations.ValidateJson(text), source.Token);
            if (!source.IsCancellationRequested && ReferenceEquals(tab.Editor, editor) && problem is not null)
            {
                tab.JsonError = problem;
                tab.JsonMessage!.Text = $"Ошибка JSON · строка {problem.Line}, столбец {problem.Column}";
                ToolTip.SetTip(tab.JsonMessage, problem.Message);
                tab.JsonBanner!.IsVisible = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidDataException)
        {
        }
        finally
        {
            if (ReferenceEquals(tab.JsonCancellation, source))
            {
                tab.JsonCancellation = null;
            }

            source.Dispose();
        }
    }

    private sealed class WorkTab(long id, Group group)
    {
        public long Id { get; } = id;
        public Group Group { get; set; } = group;
        public TabItem Item { get; } = new();
        public TextBlock Label { get; set; } = null!;
        public TextBlock DirtyMark { get; set; } = null!;
        public string? Path { get; set; }
        public TextEditor? Editor { get; set; }
        public LogView? Log { get; set; }
        public bool IsLog => Log is not null || Deferred?.Log is not null || Deferred?.LogOffset is not null;
        public SessionTab? Deferred { get; set; }
        public SessionTab? Recovery { get; set; }
        public TextEncodingKind Encoding { get; set; }
        public LineEndingKind Ending { get; set; } = OperatingSystem.IsWindows() ? LineEndingKind.CrLf : LineEndingKind.Lf;
        public ImmutableArray<LineEndingKind> OriginalEndings { get; set; }
        public FileStamp? Stamp { get; set; }
        public string? DiskStatus { get; set; }
        public bool Dirty { get; set; }
        public bool RestoredDirty { get; set; }
        public bool Loading { get; set; }
        public bool Closing { get; set; }
        public bool JsonDisabled { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset LastWorked { get; set; } = DateTimeOffset.Now;
        public SearchHighlights? Highlights { get; set; }
        public DocumentMinimap? Minimap { get; set; }
        public List<Action> EditorSubscriptions { get; set; } = [];
        public JsonProblem? JsonError { get; set; }
        public Border? JsonBanner { get; set; }
        public TextBlock? JsonMessage { get; set; }
        public CancellationTokenSource? JsonCancellation { get; set; }
        public CancellationTokenSource? LoadCancellation { get; set; }
    }
}
