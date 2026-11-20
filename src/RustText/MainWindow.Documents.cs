using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private const long LargeFilePromptBytes = 100L * 1024 * 1024;
    private const int MaxEditorCharacters = 8 * 1024 * 1024;
    private const int MaxEditorLineLength = 64 * 1024;
    private static readonly FilePickerFileType TextFiles = new("Текст и логи") { Patterns = ["*.txt", "*.log", "*.json", "*.jsonl", "*.ndjson", "*.xml"] };
    private bool _confirmingTabClose;

    private void NewTab(string text = "")
    {
        var tab = AddTab();
        AttachEditor(tab, text);
        if (text.Length > 0)
        {
            tab.RestoredDirty = tab.Dirty = true;
            UpdateTab(tab);
        }

        TrackDetachedDraft(tab);
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

    private async Task OpenPathsAsync(IEnumerable<string> paths, bool? asLog = null, TextEncodingKind? encoding = null)
    {
        var pending = new List<OpenRequest>();
        WorkTab? last = null;
        foreach (var path in paths)
        {
            try
            {
                if (await PrepareOpenAsync(path, asLog, encoding) is not { } request) continue;
                last = request.Tab;
                if (request.IsNew || encoding is not null) pending.Add(request);
            }
            catch (Exception error) when (IsUserError(error)) { SetStatus(error.Message); }
        }
        if (last is null) return;
        Select(last);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        foreach (var request in pending)
            await GuardAsync(() => LoadOpenRequestAsync(request, encoding));
    }

    private Task<WorkTab?> OpenPathAsync(string path, bool asLog = false, TextEncodingKind? encoding = null) =>
        OpenSinglePathAsync(path, asLog, encoding, editPages: false);

    private async Task<WorkTab?> OpenSinglePathAsync(string path, bool asLog, TextEncodingKind? encoding, bool editPages)
    {
        if (await PrepareOpenAsync(path, asLog, encoding, editPages) is not { } request) return null;
        Select(request.Tab);
        await LoadOpenRequestAsync(request, encoding);
        return request.Tab;
    }

    private async Task<OpenRequest?> PrepareOpenAsync(string path, bool? asLog, TextEncodingKind? encoding, bool editPages = false)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Файл не найден. {fullPath}", fullPath);
        }

        var openAsLog = asLog ?? (file.Length <= LargeFilePromptBytes && await ShouldOpenAsLogAsync(fullPath, encoding));
        if (_closed || _closing) return null;
        var mode = editPages ? FileOpenMode.PagedEditor : openAsLog ? FileOpenMode.Log : FileOpenMode.Editor;
        if (mode == FileOpenMode.Editor)
        {
            var existingEditor = _tabs.Values.FirstOrDefault(tab =>
                tab.Recovery is null && tab.Deferred?.DraftOffset is null && SamePath(tab.Path, fullPath) && !tab.OpensAsLog);
            if (existingEditor is not null) return new(existingEditor, false, mode);

            if (file.Length > LargeFilePromptBytes)
            {
                var useLog = Settings.OpenLargeFilesAsLog ?? await ChooseLargeFileModeAsync(file);
                if (useLog is null || _closed || _closing) return null;
                mode = useLog == true ? FileOpenMode.Log : FileOpenMode.PagedEditor;
            }
            else if (file.Length > DocumentFiles.MaximumDocumentBytes)
            {
                mode = FileOpenMode.PagedEditor;
            }
        }

        var existing = _tabs.Values.FirstOrDefault(tab =>
            tab.Recovery is null && tab.Deferred?.DraftOffset is null
            && SamePath(tab.Path, fullPath) && tab.OpensAsLog == (mode == FileOpenMode.Log));
        if (existing is not null)
        {
            return new(existing, false, mode);
        }

        var tab = AddTab(select: false);
        tab.Path = fullPath;
        tab.Encoding = encoding ?? TextEncodingKind.Utf8;
        tab.PendingFileOpenMode = mode;
        tab.Loading = true;
        tab.Item.Content = new TextBlock { Text = "Ожидание загрузки…", Margin = new Thickness(18) };
        UpdateTab(tab);
        return new(tab, true, mode);
    }

    private async Task<bool> ShouldOpenAsLogAsync(string path, TextEncodingKind? encoding)
    {
        if (LogLevels.IsLogPath(path)) return true;
        if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (_tabs.Values.Any(tab => tab.OpensAsLog && tab.Recovery is null && SamePath(tab.Path, path))
            || _recent.Any(entry => entry.ViewAsLog && entry.Log?.EditPages != true && SamePath(entry.Path, path))) return true;

        var token = _lifetime.Token;
        return await Task.Run(() => LogLevels.HasLogRecords(path, encoding, token), token);
    }

    private async Task<bool?> ChooseLargeFileModeAsync(FileInfo file)
    {
        var remember = new CheckBox { Content = "Запомнить выбор" };
        var body = DialogPanel(
            new TextBlock { Text = $"{file.Name} · {file.Length / (1024d * 1024d):0.##} МиБ", TextWrapping = TextWrapping.Wrap },
            new TextBlock
            {
                Text = "Для просмотра и поиска рекомендуем режим лога. В редакторе файл можно менять по страницам.",
                TextWrapping = TextWrapping.Wrap
            }, remember);
        var dialog = Dialog("Открытие большого файла", body, 530);
        var cancel = FocusButton(Button("Отмена", () => dialog.Close()));
        cancel.IsCancel = true;
        body.Children.Add(Row(
            FocusButton(Button("Открыть как лог", () => dialog.Close(true)), primary: true),
            FocusButton(Button("Открыть в редакторе", () => dialog.Close(false))), cancel));

        var useLog = await dialog.ShowDialog<bool?>(this);
        if (useLog is not null && remember.IsChecked == true)
            await SaveWorkspaceSettingsAsync(settings => settings with { OpenLargeFilesAsLog = useLog });
        return useLog;
    }

    private async Task LoadOpenRequestAsync(OpenRequest request, TextEncodingKind? encoding)
    {
        var tab = request.Tab;
        if (_closed || !_tabs.TryGetValue(tab.Id, out var current) || !ReferenceEquals(tab, current)) return;
        if (!request.IsNew)
        {
            if (encoding is not null && !tab.Loading && !tab.Dirty)
                await ReloadAsync(tab, encoding);
            return;
        }
        try
        {
            if (request.Mode != FileOpenMode.Editor)
            {
                AttachLog(tab, tab.Path!, encoding, new LogTabState(encoding, WordWrap: _wordWrap,
                    AutoRefresh: tab.AutoRefresh, EditPages: request.Mode == FileOpenMode.PagedEditor));
                if (request.Mode == FileOpenMode.PagedEditor && ReferenceEquals(tab, ActiveTab))
                    RestorePagedEditorCaret(tab, tab.Log!, null, focus: true);
            }
            else
            {
                await LoadFileAsync(tab, encoding);
            }

            await RecordRecentAsync(tab);
        }
        catch
        {
            if (_tabs.TryGetValue(tab.Id, out current) && ReferenceEquals(tab, current)) RemoveTab(tab);
            throw;
        }
        finally
        {
            tab.PendingFileOpenMode = null;
            tab.Loading = false;
        }
    }

    private enum FileOpenMode { Editor, PagedEditor, Log }

    private sealed record OpenRequest(WorkTab Tab, bool IsNew, FileOpenMode Mode);

    private async Task LoadFileAsync(WorkTab tab, TextEncodingKind? encoding = null)
    {
        var path = tab.Path ?? throw new InvalidOperationException("У вкладки нет пути.");
        if (encoding.HasValue && TryShareFileEditor(tab, encoding.Value)) return;
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
        if (TryShareFileEditor(tab, loaded.Encoding)) return;
        AttachDiskSnapshot(tab, loaded, new TextDocument(loaded.Text));
    }

    private void AttachDiskSnapshot(WorkTab tab, LoadedDocument loaded, TextDocument document)
    {
        tab.Stamp = loaded.Stamp;
        tab.DiskStatus = null;
        tab.Encoding = loaded.Encoding;
        tab.Ending = loaded.LineEnding;
        tab.OriginalEndings = loaded.OriginalLineEndings;
        document.UndoStack.MarkAsOriginalFile();
        AttachEditor(tab, document, resetDirtyState: true);
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
        _ = DetachEditor(tab);
        tab.Log?.Close();
        tab.Log = null;
        tab.Recovery = null;
        tab.OpeningPage = false;
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
            HorizontalScrollBarVisibility = _wordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        GroupedLineNumberMargin.ReplaceExisting(editor);
        EditorCaret.Attach(editor.TextArea);
        editor.Options.ShowBoxForControlCharacters = true;
        editor.TextArea.TextView.LineTransformers.Add(new JsonColorizer());
        var logLevels = new LogLevelColorizer();
        editor.TextArea.TextView.LineTransformers.Add(logLevels);
        editor.TextArea.TextView.BackgroundRenderers.Add(logLevels);
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
        var multiCaret = new MultiCaretEditing(editor.TextArea,
            edits => edits.Sum(edit => (long)edit.Text.Length - edit.Range.Length) <= MaxEditorCharacters, SetStatus);
        tab.MultiCaret = multiCaret;


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
        jump.Classes.Add("focus-outline");
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

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetColumnSpan(banner, 2);
        grid.Children.Add(banner);
        Grid.SetRow(editor, 2);
        grid.Children.Add(editor);
        var minimap = new DocumentMinimap(editor);
        var minimapDock = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        minimapDock.Children.Add(minimap);
        var verticalScrollBar = new ViewportScrollBar();
        Grid.SetColumn(verticalScrollBar, 1);
        minimapDock.Children.Add(verticalScrollBar);
        void AttachViewportScrollBar()
        {
            verticalScrollBar.SetOwner(editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault());
        }
        EventHandler<TemplateAppliedEventArgs> templateApplied = (_, _) => AttachViewportScrollBar();
        EventHandler<VisualTreeAttachmentEventArgs> attached = (_, _) => AttachViewportScrollBar();
        EventHandler<VisualTreeAttachmentEventArgs> detached = (_, _) => verticalScrollBar.SetOwner(null);
        editor.TemplateApplied += templateApplied;
        editor.AttachedToVisualTree += attached;
        editor.DetachedFromVisualTree += detached;
        Grid.SetRow(minimapDock, 2);
        Grid.SetColumn(minimapDock, 1);
        grid.Children.Add(minimapDock);
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
            if (PanelHost.IsVisible && (ReferenceEquals(tab, ActiveTab) || _allTabs.IsChecked == true))
            {
                ScheduleSearch();
            }
        };
        editor.TextChanged += textChangedHandler;
        EventHandler caretHandler = (_, _) =>
        {
            if (ReferenceEquals(tab, ActiveTab))
            {
                UpdateEditorPosition(tab, editor);
            }
        };
        editor.TextArea.Caret.PositionChanged += caretHandler;
        editor.TextArea.SelectionChanged += caretHandler;
        bool CanEditClipboard() => !_closing && !tab.Closing && !tab.Loading
            && ReferenceEquals(tab.Editor, editor) && ReferenceEquals(tab, ActiveTab);
        var unbindPaste = EditorClipboard.BindPaste(editor, CanEditClipboard, text =>
        {
            var normalized = NormalizePaste(editor, text);
            if (multiCaret.IsActive) multiCaret.Paste(normalized);
            else editor.TextArea.PerformTextInput(normalized);
        }, Run);
        void CutLineKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.X || e.KeyModifiers != KeyModifiers.Control
                || editor.IsReadOnly || !editor.IsEnabled || !editor.TextArea.Selection.IsEmpty)
            {
                return;
            }

            e.Handled = true;
            if (MultiCaretEditing.Count(editor.TextArea) > 1)
            {
                return;
            }

            Run(() => EditorClipboard.CutLineAsync(editor, CanEditClipboard));
        }
        editor.TextArea.AddHandler(KeyDownEvent, CutLineKeyDown, RoutingStrategies.Tunnel);

        // AvaloniaEdit caches the formatted colors of the visible text.
        Action redrawForTheme = editor.TextArea.TextView.Redraw;
        FocusTheme.ThemeChanged += redrawForTheme;
        tab.EditorSubscriptions =
        [
            () => FocusTheme.ThemeChanged -= redrawForTheme,
            () => undoStack.PropertyChanged -= undoHandler,
            () => editor.TextChanged -= textChangedHandler,
            () => editor.TextArea.Caret.PositionChanged -= caretHandler,
            () => editor.TextArea.SelectionChanged -= caretHandler,
            unbindPaste,
            () => editor.TextArea.RemoveHandler(KeyDownEvent, CutLineKeyDown),
            () => editor.TemplateApplied -= templateApplied,
            () => editor.AttachedToVisualTree -= attached,
            () => editor.DetachedFromVisualTree -= detached,
            () => verticalScrollBar.SetOwner(null),
            multiCaret.Dispose
        ];
        tab.Minimap = minimap;
        tab.MinimapDock = minimapDock;
        WatchEditorFile(tab);
        UpdateTab(tab);
        ScheduleJsonValidation(tab);
        if (PanelHost.IsVisible && ReferenceEquals(tab, ActiveTab) && _searchCancellation is null) ScheduleSearch();
    }

    private void ShowEditorLevelFilter(WorkTab tab)
    {
        if (tab.Editor is not { Parent: Grid grid } editor || tab.EditorLogFilter is not null) return;
        var filter = new EditorLogFilter(editor, tab.MinimapDock, () => CloseEditorLevelFilter(tab));
        tab.EditorLogFilter = filter;
        Grid.SetRow(filter.PanelControl, 1);
        Grid.SetColumnSpan(filter.PanelControl, 2);
        Grid.SetRow(filter.FilteredView, 2);
        Grid.SetColumnSpan(filter.FilteredView, 2);
        grid.Children.Add(filter.PanelControl);
        grid.Children.Add(filter.FilteredView);
        filter.PanelControl.Focus();
    }

    private void CloseEditorLevelFilter(WorkTab tab)
    {
        if (tab.EditorLogFilter is not { } filter) return;
        tab.EditorLogFilter = null;
        if (filter.PanelControl.Parent is Panel panelParent) panelParent.Children.Remove(filter.PanelControl);
        if (filter.FilteredView.Parent is Panel viewParent) viewParent.Children.Remove(filter.FilteredView);
        filter.Dispose();
        if (!_closing && ReferenceEquals(tab, ActiveTab)) tab.Editor?.Focus();
    }

    private static string NormalizePaste(TextEditor editor, string text)
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

    private async Task PersistRecoveredPageAsync(WorkTab tab, string text)
    {
        var (tabs, storage) = FindTabWorkspace(tab)
            ?? throw new InvalidOperationException("Рабочее пространство черновика закрыто.");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        tab.LoadCancellation = source;
        tab.Loading = true;
        tab.Item.Content = LoadingSurface(source, "Сохраняю черновик…");
        try
        {
            await _workspace.SaveGate.WaitAsync(source.Token);
            try
            {
                if (!ReferenceEquals(FindTabWorkspace(tab)?.Tabs, tabs)) return;
                using var reader = new StringReader(text);
                var saved = CaptureTab(tab) with { UntitledName = ReadUntitledTitle(reader), DraftOffset = 0 };
                saved = await storage.SaveClosedDraftAsync(saved, text, source.Token);
                source.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(FindTabWorkspace(tab)?.Tabs, tabs)) return;
                tab.Dirty = tab.RestoredDirty = true;
                tab.Revision++;
                AttachPagedDraftEditor(tab, saved, storage.GetDraftPath(saved)!);
            }
            finally { _workspace.SaveGate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (ReferenceEquals(FindTabWorkspace(tab)?.Tabs, tabs))
            {
                AttachEditor(tab, text);
                tab.RestoredDirty = tab.Dirty = true;
                UpdateTab(tab);
            }
            throw;
        }
        finally
        {
            tab.Loading = false;
            if (ReferenceEquals(tab.LoadCancellation, source)) tab.LoadCancellation = null;
        }
    }

    private static bool IsOversizedEditorText(string text)
    {
        if (text.Length > MaxEditorCharacters)
        {
            return true;
        }

        var lineLength = 0;
        foreach (var character in text)
        {
            if (character is '\r' or '\n') lineLength = 0;
            else if (++lineLength > MaxEditorLineLength) return true;
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
        tab.FileWatch?.Dispose();
        tab.FileWatch = null;
        CloseEditorLevelFilter(tab);
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
        tab.MinimapDock = null;
        var document = editor.Document;
        editor.Document = new TextDocument();
        tab.Editor = null;
        tab.MultiCaret = null;
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
        long? draftLineCount = null,
        bool preserveEditorChanges = false)
    {
        var log = new LogView(path, encoding, state ?? new LogTabState(AutoRefresh: tab.AutoRefresh), draftName, draftLineCount)
        {
            WordWrap = state?.WordWrap ?? _wordWrap,
            PreserveExternalPageEdit = text => PreservePageDraft(tab, text)
        };
        tab.Log?.Close();
        if (preserveEditorChanges)
        {
            PreserveEditorDraft(tab, CaptureTab(tab));
            tab.RestoredDirty = tab.Dirty = false;
            tab.DiskStatus = null;
            tab.Revision++;
        }
        else _ = DetachEditor(tab);
        log.SetTextSize(_fontSize);
        tab.Log = log;
        tab.OpeningPage = !log.InitialPageLoad.IsCompleted;
        if (tab.OpeningPage)
        {
            Run(async () =>
            {
                try { await log.InitialPageLoad; }
                finally { if (ReferenceEquals(tab.Log, log)) tab.OpeningPage = false; }
            });
        }
        log.StatusChanged += status =>
        {
            if (ReferenceEquals(tab, ActiveTab) && ReferenceEquals(tab.Log, log))
            {
                SetStatus(status.Length == 0 ? tab.Path : status);
            }
        };
        log.TextPositionChanged += () =>
        {
            if (tab.Path is null && ReferenceEquals(tab.Log, log)) UpdateDraftLineCount(tab);
            if (ReferenceEquals(tab, ActiveTab) && ReferenceEquals(tab.Log, log))
            {
                UpdateLogPosition(tab);
            }
        };
        tab.Item.Content = log;
        UpdateTab(tab);
    }

    private async Task ReloadAsync(WorkTab tab, TextEncodingKind? encoding = null)
    {
        if (tab.Path is not { } path || tab.Saving)
        {
            return;
        }

        if (tab.Dirty)
        {
            throw new InvalidOperationException("Сначала сохраните изменения.");
        }

        if (tab.Log is { } log)
        {
            if (log.HasPendingEdit) throw new IOException("Сохраните или отмените правки страницы.");
            if (tab.Recovery is null) AttachLog(tab, path, encoding, log.CaptureState() with { Encoding = encoding });
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

        ReplaceEditorFromDisk(tab, editor, loaded);
    }

    private void ReplaceEditorFromDisk(WorkTab tab, TextEditor editor, LoadedDocument loaded, string? diskStatus = null,
        bool preserveEditorChanges = false)
    {
        if (loaded.Encoding != tab.Encoding && TryShareFileEditor(tab, loaded.Encoding)) return;
        var views = loaded.Encoding == tab.Encoding ? DocumentViews(tab) : [(this, tab)];
        var snapshot = new TextDocument(loaded.Text);
        if (preserveEditorChanges)
        {
            var draft = AddExternalDraft(tab, editor.Document);
            RestoreEditorView(draft, CaptureTab(tab));
        }
        foreach (var (owner, viewTab) in views)
        {
            var view = owner.CaptureTab(viewTab);
            var hadFocus = viewTab.Editor?.IsKeyboardFocusWithin == true;
            viewTab.Revision++;
            owner.AttachDiskSnapshot(viewTab, loaded, snapshot);
            viewTab.DiskStatus = diskStatus;
            RestoreEditorView(viewTab, view);
            if (!ReferenceEquals(viewTab, owner.ActiveTab)) continue;
            if (hadFocus) viewTab.Editor?.Focus();
            if (diskStatus is null) owner.SetStatus("Файл перечитан.");
            else owner.UpdateStatus(viewTab);
        }
    }

    private async Task SaveAsync(bool saveAs = false)
    {
        if (ActiveTab is not { } tab || tab.Saving)
        {
            return;
        }
        if (tab.Log is { IsReadOnly: false } log && !saveAs)
        {
            await log.SavePageEditAsync();
            return;
        }
        if (tab.Editor is not { } editor || DocumentViews(tab).Any(view => view.Tab.Saving)) return;
        tab.Saving = true;
        try { await SaveEditorAsync(tab, editor, saveAs); }
        finally { tab.Saving = false; }
    }

    private async Task SaveEditorAsync(WorkTab tab, TextEditor editor, bool saveAs)
    {
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

        var changedPath = !SamePath(tab.Path, path);
        if (changedPath && FileViews(path).Any(view => !ReferenceEquals(view.Tab, tab)))
        {
            throw new IOException("Этот файл уже открыт в другой вкладке.");
        }

        var text = editor.Text;
        var revision = tab.Revision;
        var iconPosition = expected is null ? null : await DesktopIconPosition.CaptureAsync(path, _lifetime.Token);
        var preSaveVersionWarning = expected is null ? null : await CaptureVersionAsync(path, tab.Encoding, expected);
        var stamp = await DocumentFiles.SaveAsync(path, text, tab.Encoding, tab.Ending, expected, tab.OriginalEndings, _lifetime.Token);
        var views = DocumentViews(tab);
        if (changedPath && views.Length > 1)
        {
            // Save As gives this view a distinct file identity. Keep the old views and their undo state.
            var previous = CaptureTab(tab);
            var copy = new TextDocument(editor.Text);
            tab.Path = path;
            AttachEditor(tab, copy);
            RestoreEditorView(tab, previous);
            editor = tab.Editor!;
        }
        tab.Path = path;
        SetSyntax(editor, path);
        tab.Stamp = stamp;
        WatchEditorFile(tab);
        if (iconPosition is not null)
        {
            _ = iconPosition.RestoreAsync(path, stamp);
        }
        tab.LastWorked = DateTimeOffset.Now;
        var savedViews = changedPath ? [(this, tab)] : views;
        foreach (var (owner, savedTab) in savedViews)
        {
            savedTab.Stamp = stamp;
            savedTab.DiskStatus = null;
            if (tab.Revision == revision) savedTab.RestoredDirty = savedTab.Dirty = false;
            owner.UpdateTab(savedTab);
        }
        if (tab.Revision == revision) editor.Document.UndoStack.MarkAsOriginalFile();
        await RecordRecentAsync(tab);
        var postSaveVersionWarning = await CaptureVersionAsync(path, tab.Encoding, stamp);
        var versionWarning = preSaveVersionWarning ?? postSaveVersionWarning;
        SetStatus(versionWarning is null
            ? $"Сохранено · {path}"
            : $"Сохранено · {path}. Версию не удалось записать: {versionWarning}");
    }

    private async Task<bool> ResolvePendingPageAsync(WorkTab tab)
    {
        if (tab.Log is not { HasPendingEdit: true } log) return true;
        if (log.IsWritingPage) return false;
        if (tab.Recovery is null)
        {
            var body = DialogPanel(new TextBlock { Text = $"Сохранить изменения страницы в файле «{Path.GetFileName(tab.Path)}»?" });
            var dialog = Dialog("Изменения страницы", body);
            var cancel = FocusButton(Button("Отмена", () => dialog.Close()));
            cancel.IsCancel = true;
            body.Children.Add(Row(cancel, Button("Без сохранения", () => dialog.Close(false)),
                Button("Сохранить", () => dialog.Close(true))));
            var save = await dialog.ShowDialog<bool?>(this);
            if (save is null) return false;
            if (save == false)
            {
                log.DiscardPageEdits();
                return true;
            }
        }
        await log.SavePageEditAsync();
        return tab.Log?.HasPendingEdit != true;
    }

    private async Task ToggleLogModeAsync(WorkTab tab)
    {
        if (_closing || _workspaceSwitchPending || tab.Saving || tab.Closing || tab.Loading || tab.CheckingSession) return;
        if (tab.Deferred is not null) await ActivateDeferredAsync(tab);
        if (tab.Deferred is not null || !_tabs.ContainsKey(tab.Id) || tab.Loading) return;
        tab.Loading = true;
        var editor = tab.Editor;
        var storage = SessionStorage;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        tab.LoadCancellation = source;
        try
        {
            if (editor is not null)
            {
                if (tab.Path is { } path && !tab.Dirty)
                {
                    AttachLog(tab, path, tab.Encoding, new LogTabState(tab.Encoding, WordWrap: _wordWrap, AutoRefresh: tab.AutoRefresh));
                }
                else
                {
                    var snapshot = editor.Document.CreateSnapshot();
                    var revision = tab.Revision;
                    var saved = CaptureTab(tab) with { DraftLogMode = true, DraftOffset = 0 };
                    await _workspace.SaveGate.WaitAsync(source.Token);
                    try
                    {
                        saved = await storage.SaveClosedDraftAsync(saved, snapshot.CreateReader, source.Token);
                        source.Token.ThrowIfCancellationRequested();
                        if (!IsCurrent() || !ReferenceEquals(tab.Editor, editor)) return;
                        if (revision != tab.Revision)
                        {
                            SetStatus("Текст изменился во время переключения. Повторите команду.");
                            return;
                        }
                        AttachPagedDraftEditor(tab, saved, storage.GetDraftPath(saved)!);
                    }
                    finally { _workspace.SaveGate.Release(); }
                }
            }
            else if (tab.Log is { } previous)
            {
                await previous.InitialPageLoad;
                if (!IsCurrent() || !await ResolvePendingPageAsync(tab) || !IsCurrent()) return;
                var log = tab.Log!;
                if (tab.Recovery is { } recovery)
                {
                    // Very large streamed drafts can also switch between reading and editing by page.
                    if (log.FileLength > DocumentFiles.MaximumDocumentBytes)
                    {
                        AttachPagedDraftEditor(tab, recovery with { DraftLogMode = log.EditsPagesByDefault, DraftOffset = log.CurrentOffset },
                            storage.GetDraftPath(recovery)!, focus: true);
                    }
                    else
                    {
                        var text = await storage.LoadDraftAsync(recovery, source.Token, fullEditor: true) ?? "";
                        if (!IsCurrent() || !ReferenceEquals(tab.Log, log)) return;
                        AttachEditor(tab, text);
                        tab.RestoredDirty = tab.Dirty = text.Length != 0 || tab.Path is not null;
                        RestoreEditorView(tab, recovery);
                    }
                }
                else if (tab.Path is { } path)
                {
                    if (log.EditsPagesByDefault || new FileInfo(path).Length > DocumentFiles.MaximumDocumentBytes)
                    {
                        AttachLog(tab, path, tab.Encoding, log.CaptureState() with { EditPages = !log.EditsPagesByDefault });
                    }
                    else
                    {
                        var loaded = await DocumentFiles.ReadAsync(path, tab.Encoding, source.Token);
                        if (!IsCurrent() || !ReferenceEquals(tab.Log, log)) return;
                        AttachLoadedEditor(tab, loaded);
                    }
                }
            }
            if (!IsCurrent()) return;
            UpdateTab(tab);
            if (ReferenceEquals(tab, ActiveTab)) tab.Editor?.Focus();
            await SaveSessionAsync();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(tab.LoadCancellation, source)) tab.LoadCancellation = null;
            tab.Loading = false;
        }

        bool IsCurrent() => !_closing && !_closed && !tab.Closing && _tabs.ContainsKey(tab.Id)
            && ReferenceEquals(SessionStorage, storage) && !source.IsCancellationRequested;
    }

    private async Task CloseTabAsync(WorkTab tab)
    {
        if (_closing || _workspaceSwitchPending || !_tabs.ContainsKey(tab.Id)
            || _confirmingTabClose || tab.Closing || tab.CheckingSession)
        {
            return;
        }

        if (tab.Saving || tab.Log?.IsWritingPage == true)
        {
            Select(tab);
            SetStatus("Завершите сохранение или отмените правки страницы перед закрытием.");
            return;
        }

        tab.Closing = true;
        var editor = tab.Editor;
        var editorWasEnabled = editor?.IsEnabled == true;
        if (editor is not null) editor.IsEnabled = false;
        try
        {
            if (!await ResolvePendingPageAsync(tab)) return;
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
            if (!_closing && ReferenceEquals(tab.Editor, editor) && editor is not null)
                editor.IsEnabled = editorWasEnabled;
        }
    }

    private void RemoveTab(WorkTab tab)
    {
        ForgetSearchTab(tab);
        tab.LoadCancellation?.Cancel();
        tab.JsonCancellation?.Cancel();
        tab.Log?.Close();
        tab.Log = null;
        _ = DetachEditor(tab);
        tab.Item.Content = null;
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
        if (tab.EditorLogFilter?.IsFiltering == true) CloseEditorLevelFilter(tab);
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
        var title = tab.CustomTitle ?? (tab.Path is { } path ? Path.GetFileName(path) : UntitledName(tab));
        UpdateDraftLineCount(tab);
        tab.Label.Text = title;
        tab.RefreshHeaderMark();
        if (ReferenceEquals(SelectedIn(tab.Group), tab))
            tab.Group.LevelFilterButton.IsVisible = tab.Editor is not null && LogLevels.IsLogPath(tab.Path);
        AutomationProperties.SetName(tab.Item, title + (tab.Dirty ? ", изменено" : ""));
        ToolTip.SetTip(tab.Label, tab.Path ?? title);
        if (ReferenceEquals(tab, ActiveTab))
        {
            UpdateStatus(tab);
        }
    }

    private void UpdateDraftLineCount(WorkTab tab)
    {
        var count = tab.Editor?.Document.LineCount ?? tab.Log?.DisplayLineCount ?? tab.Deferred?.LineCount;
        tab.LineCountLabel.IsVisible = Settings.ShowDraftLineCount && tab.Path is null && count.HasValue;
        if (tab.LineCountLabel.IsVisible)
            tab.LineCountLabel.Text = $"{LineNumbers.Format(count!.Value)} стр.";
    }

    private async Task RenameTabAsync(WorkTab tab)
    {
        var name = await PromptAsync("Переименовать вкладку", "Название вкладки. Оставьте поле пустым для автоматического названия.",
            tab.CustomTitle ?? tab.Label.Text ?? "");
        if (name is null || _closing || !_tabs.ContainsKey(tab.Id)) return;
        tab.CustomTitle = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (tab.Deferred is { } pending) tab.Deferred = pending with { CustomTitle = tab.CustomTitle };
        if (tab.Recovery is { } recovery) tab.Recovery = recovery with { CustomTitle = tab.CustomTitle };
        UpdateTab(tab);
        await SaveSessionAsync();
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
        foreach (var rune in raw.EnumerateRunes().Take(Settings.UntitledTabLimit))
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
            UpdateEditorPosition(tab, editor);
        }
        else
        {
            SetStatus(tab.Log?.StatusText is { Length: > 0 } message ? message : tab.Path);
            UpdateLogPosition(tab);
        }
    }

    private void UpdateEditorPosition(WorkTab tab, TextEditor editor)
    {
        var area = editor.TextArea;
        var count = MultiCaretEditing.Count(area);
        var carets = count > 1 ? $" · Курсоры: {LineNumbers.Format(count)}" : "";
        var position = FormatCaretStatus(area.Caret.Line, area.Selection.Length);
        FormatText.Text = $"{position} · Стлб. {LineNumbers.Format(area.Caret.Column)} · {EncodingName(tab.Encoding)} · {tab.Ending}{carets}";
    }

    private void UpdateLogPosition(WorkTab tab)
    {
        var mode = tab.Log is { EditsPagesByDefault: true }
            ? tab.Recovery is null ? "Редактор по страницам" : "Редактирование черновика"
            : tab.IsLog ? "Просмотр лога" : "";
        FormatText.Text = tab.Log?.TextPosition is { } position
            ? $"{FormatCaretStatus(position.Line, position.Selected, position.IsPageLine)} · {mode}"
            : mode;
    }

    private static string FormatCaretStatus(long line, int selected, bool isPageLine = false) =>
        $"Строка {LineNumbers.Format(line)}{(isPageLine ? " на странице" : "")} · Выделено {LineNumbers.Format(selected)} симв.";

    private static void SetSyntax(TextEditor editor, string? path)
    {
        var colorizer = editor.TextArea.TextView.LineTransformers.OfType<JsonColorizer>().Single();
        var enabled = JsonColorizer.Applies(path, editor.Document);
        var levels = editor.TextArea.TextView.LineTransformers.OfType<LogLevelColorizer>().Single();
        var logFile = LogLevels.IsLogPath(path);
        if (colorizer.Enabled != enabled || levels.Enabled != logFile)
        {
            colorizer.Enabled = enabled;
            levels.Enabled = logFile;
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

        if (!ShouldCheckJson(editor.Document, tab.Path))
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
            var path = tab.Path;
            if (LogLevels.IsLogPath(path) && !MightBeJsonLogLinesPrefix(editor.Document))
            {
                return;
            }

            var snapshot = editor.Document.CreateSnapshot();
            var problem = await Task.Run(() =>
            {
                using var reader = snapshot.CreateReader();
                var text = reader.ReadToEnd();
                return TextOperations.LooksLikeJson(text, path)
                    ? TextOperations.ValidateJson(text, path, source.Token)
                    : null;
            }, source.Token);
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

    private static bool ShouldCheckJson(TextDocument document, string? path)
    {
        if (TextOperations.IsJsonPath(path)) return true;
        if (LogLevels.IsLogPath(path)) return MightBeJsonLogLinesPrefix(document);

        // A long whitespace prefix is inconclusive. Leave the full check on the worker.
        var end = Math.Min(document.TextLength, 64);
        for (var i = 0; i < end; i++)
        {
            var c = document.GetCharAt(i);
            if (c != '\uFEFF' && !char.IsWhiteSpace(c)) return c is '{' or '[';
        }
        return document.TextLength > end;
    }

    private static bool MightBeJsonLogLinesPrefix(TextDocument document)
    {
        var lines = document.Lines;
        if (lines.Count < 2 || lines[0].Length == 0 || lines[1].Length == 0) return false;

        const int prefixLimit = 256;
        var first = lines[0];
        var second = lines[1];
        var firstPrefix = document.GetText(first.Offset, Math.Min(first.Length, prefixLimit));
        var secondPrefix = document.GetText(second.Offset, Math.Min(second.Length, prefixLimit));
        return TextOperations.IsJsonContainerPrefix(firstPrefix) && TextOperations.IsJsonContainerPrefix(secondPrefix);
    }

    private sealed class WorkTab(long id, Group group)
    {
        public long Id { get; } = id;
        public Group Group { get; set; } = group;
        public TabItem Item { get; } = new();
        public string? CustomTitle { get; set; }
        public TextBlock LineCountLabel { get; } = new()
        {
            FontSize = 11, Foreground = FocusTheme.Muted, VerticalAlignment = VerticalAlignment.Center, IsVisible = false
        };
        public TextBlock Label { get; set; } = null!;
        public TextBlock DirtyMark { get; set; } = null!;
        public TextBlock LoadMark { get; set; } = null!;
        public Control HeaderMarks { get; set; } = null!;
        public string? Path { get; set; }
        public TextEditor? Editor { get; set; }
        public EditorLogFilter? EditorLogFilter { get; set; }
        public MultiCaretEditing? MultiCaret { get; set; }
        public LogView? Log { get; set; }
        public bool IsLog => PendingFileOpenMode is FileOpenMode.PagedEditor or FileOpenMode.Log
            || Log is not null || Deferred?.Log is not null || Deferred?.LogOffset is not null;
        public bool OpensAsLog => IsLog && !(Log?.EditsPagesByDefault
            ?? Deferred?.Log?.EditPages ?? PendingFileOpenMode == FileOpenMode.PagedEditor);
        public FileOpenMode? PendingFileOpenMode { get; set; }
        public SessionTab? Deferred { get => field; set { field = value; RefreshHeaderMark(); } }
        public SessionTab? Recovery { get; set; }
        public TextEncodingKind Encoding { get; set; }
        public LineEndingKind Ending { get; set; } = OperatingSystem.IsWindows() ? LineEndingKind.CrLf : LineEndingKind.Lf;
        public ImmutableArray<LineEndingKind> OriginalEndings { get; set; }
        public FileStamp? Stamp { get; set; }
        public string? DiskStatus { get; set; }
        public bool Dirty { get; set; }
        public bool RestoredDirty { get; set; }
        public bool Loading { get => field; set { field = value; RefreshHeaderMark(); } }
        public bool CheckingSession { get => field; set { field = value; RefreshHeaderMark(); } }
        public bool OpeningPage { get => field; set { field = value; RefreshHeaderMark(); } }
        public bool Closing { get; set; }
        public bool Saving { get; set; }
        public bool AutoRefresh { get; set; } = true;
        public EditorFileWatch? FileWatch { get; set; }
        public bool JsonDisabled { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset LastWorked { get; set; } = DateTimeOffset.Now;
        public SearchHighlights? Highlights { get; set; }
        public DocumentMinimap? Minimap { get; set; }
        public Control? MinimapDock { get; set; }
        public List<Action> EditorSubscriptions { get; set; } = [];
        public JsonProblem? JsonError { get; set; }
        public Border? JsonBanner { get; set; }
        public TextBlock? JsonMessage { get; set; }
        public CancellationTokenSource? JsonCancellation { get; set; }
        public CancellationTokenSource? LoadCancellation { get; set; }

        public void RefreshHeaderMark()
        {
            var loading = Loading || CheckingSession || OpeningPage;
            var waiting = Deferred is not null;
            LoadMark.IsVisible = loading || waiting;
            LoadMark.Text = loading ? "↻" : "◷";
            DirtyMark.Opacity = !LoadMark.IsVisible && Dirty ? 1 : 0;
            var status = loading ? "Загрузка вкладки" : waiting ? "Загрузится при выборе" : null;
            AutomationProperties.SetName(LoadMark, status);
            ToolTip.SetTip(HeaderMarks, status);
        }
    }
}
