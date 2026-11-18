using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
using RustText.Demo.Platform;

namespace RustText.Demo;

public partial class MainWindow
{
    private const long LogThreshold = 50L * 1024 * 1024;
    private static readonly FilePickerFileType TextFiles = new("Текст и логи") { Patterns = ["*.txt", "*.log", "*.json", "*.xml"] };
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
        tab.Log?.Close();
        tab.Log = null;
        tab.LastWorked = DateTimeOffset.Now;
        var editor = new TextEditor
        {
            Text = text,
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = _fontSize,
            WordWrap = _wordWrap,
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        editor.Options.ShowBoxForControlCharacters = true;
        tab.Editor = editor;
        SetSyntax(editor, tab.Path);
        tab.RestoredDirty = false;
        tab.Dirty = false;
        tab.Highlights = new SearchHighlights(editor);
        editor.TextArea.TextView.BackgroundRenderers.Add(tab.Highlights);
        var undoStack = editor.Document.UndoStack;
        undoStack.MarkAsOriginalFile();

        var warning = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
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
            Background = new SolidColorBrush(Color.Parse("#58282E")),
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
        undoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(undoStack.IsOriginalFile) && ReferenceEquals(tab.Editor, editor))
            {
                tab.Dirty = tab.RestoredDirty || !undoStack.IsOriginalFile;
                UpdateTab(tab);
            }
        };
        editor.TextChanged += (_, _) =>
        {
            tab.Revision++;
            tab.LastWorked = DateTimeOffset.Now;
            UpdateTab(tab);
            ScheduleJsonValidation(tab);
            if (ReferenceEquals(tab, ActiveTab))
            {
                RefreshSearchHighlights();
            }
        };
        editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (ReferenceEquals(tab, ActiveTab))
            {
                UpdateStatus(tab);
            }
        };
        UpdateTab(tab);
        ScheduleJsonValidation(tab);
    }

    private void AttachLog(WorkTab tab, string path, TextEncodingKind? encoding = null, LogTabState? state = null)
    {
        tab.Log?.Close();
        tab.Editor = null;
        var log = new LogView(path, encoding, state) { WordWrap = state?.WordWrap ?? _wordWrap };
        log.SetTextSize(_fontSize);
        tab.Log = log;
        log.StatusChanged += status =>
        {
            if (ReferenceEquals(tab, ActiveTab))
            {
                SetStatus(status);
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

        var old = tab.Editor;
        var offset = old?.CaretOffset ?? 0;
        var loaded = await DocumentFiles.ReadAsync(path, encoding, _lifetime.Token);
        AttachLoadedEditor(tab, loaded);
        SelectText(tab, offset, 0);
        SetStatus("Файл перечитан.");
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
        SetStatus($"Сохранено · {path}");
        await RecordRecentAsync(tab);
        await CaptureVersionAsync(path, tab.Encoding);
    }

    private async Task CloseTabAsync(WorkTab tab)
    {
        if (!_tabs.ContainsKey(tab.Id) || _confirmingTabClose)
        {
            return;
        }

        if (tab is { Dirty: true, Path: { } path, Recovery: null })
        {
            var body = DialogPanel(new TextBlock
            {
                Text = $"Вкладка «{Path.GetFileName(path)}» содержит несохранённый текст.",
                TextWrapping = TextWrapping.Wrap
            });
            var dialog = Dialog("Закрыть без сохранения?", body);
            var cancel = Button("Отмена", () => dialog.Close(false));
            cancel.IsCancel = true;
            body.Children.Add(Row(cancel, Button("Закрыть", () => dialog.Close(true))));
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

        await RememberClosedAsync(tab);
        await RecordRecentAsync(tab);
        RemoveTab(tab);
        if (_tabs.Count == 0)
        {
            await ShowHomeAsync();
        }
    }

    private void RemoveTab(WorkTab tab)
    {
        tab.LoadCancellation?.Cancel();
        tab.JsonCancellation?.Cancel();
        tab.Log?.Close();
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
        tab.Label.Text = title + (tab.Dirty ? " •" : "");
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
            return "Без имени";
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
        if (tab.Editor is { } editor)
        {
            var status = tab.Dirty ? "Изменено" : "Сохранено";
            SetStatus($"{status} · {tab.Path ?? "Без имени"}{(tab.DiskStatus is { } disk ? $" · {disk}" : "")}");
            FormatText.Text = $"Стр. {editor.TextArea.Caret.Line}, стлб. {editor.TextArea.Caret.Column} · {EncodingName(tab.Encoding)} · {tab.Ending}";
        }
        else
        {
            SetStatus(tab.Path);
            FormatText.Text = tab.IsLog ? "Просмотр лога" : "";
        }
    }

    private static void SetSyntax(TextEditor editor, string? path)
    {
        editor.SyntaxHighlighting = path is null ? null
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
        public bool JsonDisabled { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset LastWorked { get; set; } = DateTimeOffset.Now;
        public SearchHighlights? Highlights { get; set; }
        public JsonProblem? JsonError { get; set; }
        public Border? JsonBanner { get; set; }
        public TextBlock? JsonMessage { get; set; }
        public CancellationTokenSource? JsonCancellation { get; set; }
        public CancellationTokenSource? LoadCancellation { get; set; }
    }
}
