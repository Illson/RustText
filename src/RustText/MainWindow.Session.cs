using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RustText.Demo.Logs;

namespace RustText.Demo;

public partial class MainWindow
{
    private readonly StorageService _storage = StorageService.OpenDefault();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly List<SessionTab> _closedTabs = [];
    private IReadOnlyList<RecentFileEntry> _recent = [];
    private UserSettings _settings = new();
    private MenuItem? _recentMenu;
    private MenuItem? _closedMenu;
    private Action? _refreshHome;
    private DateTimeOffset _lastHomeRefresh;
    private bool _checkingDisk;
    private int? _lastCheckpoint;

    private async Task RestoreSessionAsync()
    {
        try
        {
            _settings = await _storage.LoadSettingsAsync(_lifetime.Token);
            _titleLimit = _settings.UntitledTabLimit;
            _recent = await _storage.GetRecentFilesAsync(_lifetime.Token);
        }
        catch (Exception error) when (IsUserError(error))
        {
            SetStatus(error.Message);
        }

        var state = await _storage.LoadSessionAsync(_lifetime.Token);
        if (state is null)
        {
            RefreshHistoryMenus();
            return;
        }

        Width = Math.Clamp(state.WindowWidth, MinWidth, 4000);
        Height = Math.Clamp(state.WindowHeight, MinHeight, 2400);
        if (state.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        _wordWrap = state.Wrap;
        _fontSize = Math.Clamp(state.FontSize, 8, 48);
        _closedTabs.AddRange((state.ClosedTabs ?? []).Take(100));
        _nextId = Math.Max(_nextId, state.Tabs.Concat(_closedTabs).Select(tab => tab.Id).DefaultIfEmpty().Max());
        _root = state.Layout is { } layout ? RestoreLayout(layout) : MakeGroup();
        var groups = Groups().ToArray();
        _active = groups.FirstOrDefault(group => group.Id == state.ActiveGroupId) ?? groups[0];
        foreach (var saved in state.Tabs)
        {
            if (!_tabs.ContainsKey(saved.Id))
            {
                var group = FindSavedGroup(state.Layout, saved.Id);
                var tab = AddTab(saved.Id, groups.FirstOrDefault(current => current.Id == group) ?? _active, false);
                SetDeferred(tab, saved);
            }
        }

        foreach (var group in groups)
        {
            if (FindSavedSelection(state.Layout, group.Id) is { } id && _tabs.TryGetValue(id, out var tab))
            {
                group.Tabs.SelectedItem = tab.Item;
            }
        }

        ShowGroups();
        if (SelectedIn(_active) is { } selected)
        {
            await GuardAsync(() => ActivateDeferredAsync(selected));
        }
        else if (state.Active >= 0 && state.Active < state.Tabs.Count
            && _tabs.TryGetValue(state.Tabs[state.Active].Id, out var active))
        {
            Select(active);
        }

        RefreshHistoryMenus();
    }

    private Group RestoreLayout(WorkspaceLayout state)
    {
        _nextId = Math.Max(_nextId, state.GroupId);
        if (state.First is not { } first || state.Second is not { } second)
        {
            return MakeGroup(state.GroupId);
        }

        return new Group(state.GroupId)
        {
            Axis = state.Split == "Vertical" ? Orientation.Vertical : Orientation.Horizontal,
            Ratio = Math.Clamp(state.Ratio, .1, .9),
            First = RestoreLayout(first),
            Second = RestoreLayout(second)
        };
    }

    private static long? FindSavedGroup(WorkspaceLayout? state, long tabId)
    {
        if (state is null)
        {
            return null;
        }

        return state.Tabs?.Contains(tabId) == true ? state.GroupId
            : FindSavedGroup(state.First, tabId) ?? FindSavedGroup(state.Second, tabId);
    }

    private static long? FindSavedSelection(WorkspaceLayout? state, long groupId)
    {
        if (state is null)
        {
            return null;
        }

        return state.GroupId == groupId ? state.ActiveTab
            : FindSavedSelection(state.First, groupId) ?? FindSavedSelection(state.Second, groupId);
    }

    private WorkspaceLayout CaptureLayout(Group group) => group.Axis is { } axis
        ? new WorkspaceLayout(group.Id, Split: axis.ToString(), Ratio: group.Ratio,
            First: CaptureLayout(group.First!), Second: CaptureLayout(group.Second!))
        : new WorkspaceLayout(group.Id, group.Tabs.Items.OfType<TabItem>()
            .Select(item => _tabs.Values.First(tab => ReferenceEquals(tab.Item, item)).Id).ToArray(),
            SelectedIn(group)?.Id);

    private SessionTab CaptureTab(WorkTab tab)
    {
        if (tab.Deferred is { } pending)
        {
            return pending;
        }

        if (tab.Recovery is { } recovered)
        {
            return recovered;
        }

        var editor = tab.Editor;
        var log = tab.Log?.CaptureState();
        var caret = editor?.CaretOffset ?? 0;
        var selectionStart = editor?.SelectionStart ?? 0;
        var selectionLength = editor?.SelectionLength ?? 0;
        var lineEndings = tab.OriginalEndings.IsDefaultOrEmpty ? null : tab.OriginalEndings.Select(value => (byte)value).ToArray();
        return new SessionTab(tab.Id, tab.Path, tab.Encoding, selectionStart, selectionLength,
            caret < selectionStart + selectionLength, editor?.HorizontalOffset ?? 0, editor?.VerticalOffset ?? 0,
            LogOffset: log?.Offset, LineCount: editor?.Document.LineCount,
            LastWorkedMs: tab.LastWorked.ToUnixTimeMilliseconds(), LineEnding: tab.Ending,
            LineEndingMap: lineEndings, Stamp: tab.Stamp, Log: log)
        {
            JsonValidationDisabled = tab.JsonDisabled,
            UntitledName = tab.Path is null ? UntitledName(tab) : null
        };
    }

    private async Task SaveSessionAsync()
    {
        if (!_restoreFinished && !_closing)
        {
            return;
        }

        await _sessionGate.WaitAsync();
        try
        {
            var tabs = _tabs.Values.ToArray();
            var state = tabs.Select(CaptureTab).ToList();
            var drafts = new Dictionary<long, string>();
            foreach (var tab in tabs)
            {
                if (tab.Editor is { } editor && (tab.Dirty || tab.Path is null))
                {
                    drafts[tab.Id] = editor.Text;
                }
            }

            var index = ActiveTab is { } active ? Array.IndexOf(tabs, active) : 0;
            var width = double.IsFinite(Width) ? Width : 1080;
            var height = double.IsFinite(Height) ? Height : 700;

            var session = new SessionState(state, _closedTabs.Take(100).ToList(),
                Math.Max(0, index), width, height, WindowState == WindowState.Maximized,
                _wordWrap, _fontSize, CaptureLayout(_root), _active.Id);
            var fingerprint = CheckpointFingerprint(session, tabs);
            if (_closing || _lastCheckpoint != fingerprint)
            {
                var committed = await _storage.SaveSessionAsync(session, drafts);
                for (var i = 0; i < tabs.Length; i++)
                {
                    var captured = session.Tabs[i];
                    if (ReferenceEquals(tabs[i].Deferred, captured)) tabs[i].Deferred = committed.Tabs[i];
                    if (ReferenceEquals(tabs[i].Recovery, captured)) tabs[i].Recovery = committed.Tabs[i];
                }

                var capturedClosed = session.ClosedTabs ?? [];
                for (var i = 0; i < Math.Min(capturedClosed.Count, committed.ClosedTabs?.Count ?? 0); i++)
                {
                    var closedIndex = _closedTabs.FindIndex(item => ReferenceEquals(item, capturedClosed[i]));
                    if (closedIndex >= 0) _closedTabs[closedIndex] = committed.ClosedTabs![i];
                }

                _lastCheckpoint = fingerprint;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private static int CheckpointFingerprint(SessionState state, WorkTab[] tabs)
    {
        var hash = new HashCode();
        hash.Add(state.Active);
        hash.Add(state.ActiveGroupId);
        hash.Add(state.WindowWidth);
        hash.Add(state.WindowHeight);
        hash.Add(state.Maximized);
        hash.Add(state.Wrap);
        hash.Add(state.FontSize);
        AddLayout(state.Layout);
        for (var i = 0; i < tabs.Length; i++)
        {
            hash.Add(state.Tabs[i] with { LineEndingMap = null });
            hash.Add(tabs[i].Revision);
            hash.Add(tabs[i].Dirty);
        }

        foreach (var tab in state.ClosedTabs ?? [])
        {
            hash.Add(tab with { LineEndingMap = null });
        }

        return hash.ToHashCode();

        void AddLayout(WorkspaceLayout? node)
        {
            if (node is null)
            {
                return;
            }

            hash.Add(node.GroupId);
            hash.Add(node.ActiveTab);
            hash.Add(node.Split);
            hash.Add(node.Ratio);
            foreach (var id in node.Tabs ?? [])
            {
                hash.Add(id);
            }

            AddLayout(node.First);
            AddLayout(node.Second);
        }
    }

    private void SetDeferred(WorkTab tab, SessionTab saved)
    {
        tab.Path = saved.Path;
        tab.Encoding = saved.Encoding;
        tab.Ending = saved.LineEnding;
        tab.Stamp = saved.Stamp;
        tab.OriginalEndings = saved.LineEndingMap is { } markers
            ? markers.Select(value => (LineEndingKind)value).ToImmutableArray() : default;
        tab.JsonDisabled = saved.JsonValidationDisabled;
        tab.LastWorked = saved.LastWorkedMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.Now;
        tab.Deferred = saved;
        tab.Item.Content = FocusButton(Button("Загрузить вкладку", () => Run(() => ActivateDeferredAsync(tab))), primary: true);
        UpdateTab(tab);
    }

    private async Task ActivateDeferredAsync(WorkTab tab)
    {
        if (tab.Deferred is not { } saved || tab.Loading)
        {
            return;
        }

        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        tab.Loading = true;
        tab.LoadCancellation = source;
        tab.Item.Content = LoadingSurface(source, "Восстановление вкладки…");
        try
        {
            var token = source.Token;
            var previousDeferred = saved;
            saved = await _storage.ResolveCurrentDraftAsync(saved, token);
            if (ReferenceEquals(tab.Deferred, previousDeferred))
            {
                tab.Deferred = saved;
            }
            if ((saved.Log is not null || saved.LogOffset is not null) && saved.Path is { } logPath)
            {
                AttachLog(tab, logPath, saved.Encoding,
                    saved.Log ?? new LogTabState(saved.Encoding, saved.LogOffset ?? 0, WordWrap: _wordWrap));
            }
            else if (saved.DraftRef is not null)
            {
                var draftPath = _storage.GetDraftPath(saved);
                if (draftPath is not null && new FileInfo(draftPath).Length > LineGeneratorThreshold)
                {
                    AttachDraftPreview(tab, saved, draftPath);
                }
                else
                {
                    var draft = await _storage.LoadDraftAsync(saved, token) ?? "";
                    if (draftPath is not null && IsOversizedDraft(draft))
                    {
                        AttachDraftPreview(tab, saved, draftPath);
                    }
                    else
                    {
                        AttachEditor(tab, draft);
                        tab.Dirty = draft.Length != 0 || tab.Path is not null;
                        tab.RestoredDirty = tab.Dirty;
                        if (tab.Stamp is null && tab.Path is { } original && File.Exists(original))
                        {
                            try
                            {
                                if (saved.LegacyStamp is { } legacy)
                                {
                                    var stamp = await DocumentFiles.GetStampAsync(original, token);
                                    if (stamp.Length == legacy.Length && stamp.LastWriteUtc == legacy.LastWriteUtc
                                        && (legacy.CreatedUtc is null || File.GetCreationTimeUtc(original) == legacy.CreatedUtc))
                                    {
                                        tab.Stamp = stamp;
                                    }
                                }
                            }
                            catch (IOException)
                            {
                            }
                            catch (UnauthorizedAccessException)
                            {
                            }

                            tab.DiskStatus = tab.Stamp is null
                                ? "Проверьте исходник и сохраните черновик через «Сохранить как»." : null;
                        }
                    }
                }
            }
            else if (saved.Path is { } path)
            {
                AttachLoadedEditor(tab, await DocumentFiles.ReadAsync(path, saved.Encoding, token));
            }
            else
            {
                AttachEditor(tab, "");
            }

            RestoreEditorView(tab, saved);

            tab.Deferred = null;
            UpdateTab(tab);
        }
        catch
        {
            if (_tabs.ContainsKey(tab.Id))
            {
                tab.Item.Content = FocusButton(Button("Повторить загрузку", () => Run(() => ActivateDeferredAsync(tab))), primary: true);
            }

            throw;
        }
        finally
        {
            tab.Loading = false;
            tab.LoadCancellation = null;
        }
    }

    private static void RestoreEditorView(WorkTab tab, SessionTab saved)
    {
        if (tab.Editor is not { } editor)
        {
            return;
        }

        var size = editor.Document.TextLength;
        var start = Math.Clamp(saved.SelectionStart, 0, size);
        var length = Math.Clamp(saved.SelectionLength, 0, size - start);
        if (saved.SelectionUsesUtf8)
        {
            var text = editor.Text;
            start = Utf8IndexToUtf16(text, saved.SelectionStart);
            var end = Utf8IndexToUtf16(text, (long)saved.SelectionStart + saved.SelectionLength);
            length = Math.Max(0, end - start);
        }

        editor.Select(start, length);
        editor.CaretOffset = saved.Reversed ? start : start + length;
        Dispatcher.UIThread.Post(() =>
        {
            if (editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            {
                scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(saved.ScrollX, saved.ScrollY));
            }
        }, DispatcherPriority.Loaded);
    }

    private const long LineGeneratorThreshold = 8L * 1024 * 1024;

    internal static int Utf8IndexToUtf16(string text, long byteOffset)
    {
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (byteOffset < rune.Utf8SequenceLength)
            {
                break;
            }

            byteOffset -= rune.Utf8SequenceLength;
            index += rune.Utf16SequenceLength;
        }

        return index;
    }

    private static bool IsOversizedDraft(string text)
    {
        var lines = 1;
        var current = 0;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                current = 0;
                if (++lines > 50_000)
                {
                    return true;
                }
            }
            else if (++current > 64 * 1024)
            {
                return true;
            }
        }

        return false;
    }

    private void AttachDraftPreview(WorkTab tab, SessionTab saved, string draftPath)
    {
        AttachLog(tab, draftPath, TextEncodingKind.Utf8);
        tab.Recovery = saved;
        var panel = new DockPanel();
        var copy = FocusButton(Button("Сохранить копию…", () => Run(() => SaveCurrentDraftCopyAsync(tab))), primary: true);
        Avalonia.Controls.DockPanel.SetDock(copy, Dock.Top);
        tab.Item.Content = null;
        panel.Children.Add(copy);
        panel.Children.Add(tab.Log!);
        tab.Item.Content = panel;
    }

    private async Task SaveCurrentDraftCopyAsync(WorkTab tab)
    {
        if (tab.Recovery is not { } recovery) return;
        var current = await _storage.ResolveCurrentDraftAsync(recovery, _lifetime.Token);
        if (ReferenceEquals(tab.Recovery, recovery)) tab.Recovery = current;
        if (_storage.GetDraftPath(current) is { } path) await SaveDraftCopyAsync(path);
    }

    private async Task RememberClosedAsync(WorkTab tab)
    {
        var saved = CaptureTab(tab);
        if (tab.Editor is { } editor && tab.Path is null)
        {
            saved = await _storage.SaveClosedDraftAsync(saved, editor.Text, _lifetime.Token);
        }

        _closedTabs.RemoveAll(item => item.Id == saved.Id);
        _closedTabs.Insert(0, saved);
        if (_closedTabs.Count > 100)
        {
            _closedTabs.RemoveAt(_closedTabs.Count - 1);
        }

        RefreshHistoryMenus();
    }

    private Task RestoreLastClosedAsync() => _closedTabs.Count > 0 ? RestoreClosedAsync(_closedTabs[0]) : Task.CompletedTask;

    private async Task RestoreClosedAsync(SessionTab saved)
    {
        if (saved is { Path: { } path, DraftRef: null })
        {
            var isLog = saved.Log is not null || saved.LogOffset is not null;
            var existing = _tabs.Values.FirstOrDefault(tab =>
                tab.IsLog == isLog && tab.Recovery is null && SamePath(tab.Path, path));
            if (existing is not null)
            {
                if (existing.Loading)
                {
                    Select(existing);
                    return;
                }

                if (!existing.Dirty && existing.Deferred?.DraftRef is null)
                {
                    if (existing.Deferred is not null)
                    {
                        SetDeferred(existing, saved with { Id = existing.Id });
                        await ActivateDeferredAsync(existing);
                    }
                    else if (isLog)
                    {
                        AttachLog(existing, path, saved.Encoding,
                            saved.Log ?? new LogTabState(saved.Encoding, saved.LogOffset ?? 0, WordWrap: _wordWrap));
                    }
                    else
                    {
                        await RestoreClosedEditorAsync(existing, path, saved);
                    }
                }

                if (_tabs.ContainsKey(existing.Id))
                {
                    Select(existing);
                    RemoveClosedEntry(saved);
                }
                return;
            }
        }

        if (_tabs.TryGetValue(saved.Id, out var sameTab))
        {
            Select(sameTab);
            return;
        }

        var tab = AddTab(saved.Id);
        SetDeferred(tab, saved);
        try
        {
            await ActivateDeferredAsync(tab);
            RemoveClosedEntry(saved);
        }
        catch
        {
            RemoveTab(tab);
            throw;
        }
    }

    private async Task RestoreClosedEditorAsync(WorkTab tab, string path, SessionTab saved)
    {
        if (tab.Encoding != saved.Encoding)
        {
            var revision = tab.Revision;
            var loaded = await DocumentFiles.ReadAsync(path, saved.Encoding, _lifetime.Token);
            if (tab.Dirty || tab.Revision != revision || !_tabs.ContainsKey(tab.Id))
            {
                return;
            }

            AttachLoadedEditor(tab, loaded);
        }

        tab.JsonDisabled = saved.JsonValidationDisabled;
        RestoreEditorView(tab, saved);
        ScheduleJsonValidation(tab);
    }

    private void RemoveClosedEntry(SessionTab saved)
    {
        _closedTabs.RemoveAll(item => item.Id == saved.Id);
        RefreshHistoryMenus();
    }

    private async Task RecordRecentAsync(WorkTab tab)
    {
        if (tab.Path is not { } path || tab.Recovery is not null)
        {
            return;
        }

        var editor = tab.Editor;
        var log = tab.Log?.CaptureState();
        var entry = new RecentFileEntry(path, tab.Encoding, tab.IsLog, editor?.SelectionStart ?? 0,
            editor?.SelectionLength ?? 0, log?.Offset, editor?.Document.LineCount,
            Reversed: editor is not null && editor.CaretOffset < editor.SelectionStart + editor.SelectionLength,
            ScrollX: editor?.HorizontalOffset ?? 0,
            ScrollY: editor?.VerticalOffset ?? 0,
            JsonValidationDisabled: tab.JsonDisabled,
            Log: log);
        await _storage.AddRecentAsync(entry, _lifetime.Token);
        _recent = await _storage.GetRecentFilesAsync(_lifetime.Token);
        RefreshHistoryMenus();
    }

    private async Task OpenRecentAsync(RecentFileEntry entry)
    {
        await OpenPathAsync(entry.Path, entry.ViewAsLog, entry.Encoding);
        if (ActiveTab is { } tab)
        {
            if (tab.Log is not null)
            {
                var log = (entry.Log ?? new LogTabState(entry.Encoding, entry.LogOffset ?? 0)) with
                {
                    Encoding = entry.Encoding,
                    WordWrap = _wordWrap
                };
                AttachLog(tab, entry.Path, entry.Encoding, log);
            }
            else if (tab.Editor is not null)
            {
                tab.JsonDisabled = entry.JsonValidationDisabled;
                ScheduleJsonValidation(tab);
                RestoreEditorView(tab, new SessionTab(
                    tab.Id,
                    entry.Path,
                    entry.Encoding ?? tab.Encoding,
                    entry.SelectionStart,
                    entry.SelectionLength,
                    entry.Reversed,
                    entry.ScrollX,
                    entry.ScrollY)
                {
                    SelectionUsesUtf8 = entry.SelectionUsesUtf8
                });
            }
        }
    }

    private async Task ShowHomeAsync()
    {
        _home = true;
        SetCaption(null);
        _recent = await _storage.GetRecentFilesAsync(_lifetime.Token);
        if (!_home)
        {
            return;
        }

        var input = new TextBox
        {
            PlaceholderText = "Поиск по имени или пути",
            Background = FocusTheme.Field,
            Foreground = FocusTheme.Text,
            BorderBrush = FocusTheme.Border
        };
        var rows = new StackPanel { Spacing = 3 };
        var footer = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var index = 0;
        void Rebuild()
        {
            rows.Children.Clear();
            var term = input.Text?.Trim() ?? "";
            var visible = _recent.Where(item => item.Path.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
            var count = Math.Max(1, (visible.Length + 15) / 16);
            index = Math.Clamp(index, 0, count - 1);
            foreach (var entry in visible.Skip(index * 16).Take(16))
            {
                var label = FocusButton(Button($"{Path.GetFileName(entry.Path)}   {DescribeWork(entry.LineCount, entry.LastWorkedMs)}",
                    () => Run(() => OpenRecentAsync(entry))));
                label.HorizontalAlignment = HorizontalAlignment.Stretch;
                label.HorizontalContentAlignment = HorizontalAlignment.Left;
                ToolTip.SetTip(label, entry.Path);
                rows.Children.Add(label);
            }

            footer.Text = $"Страница {index + 1} из {count}";
        }

        input.TextChanged += (_, _) => { index = 0; Rebuild(); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(FocusButton(Button("Назад", () => { index--; Rebuild(); })));
        actions.Children.Add(footer);
        actions.Children.Add(FocusButton(Button("Дальше", () => { index++; Rebuild(); })));
        var surface = new Grid
        {
            MaxWidth = 840,
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 10,
            Background = FocusTheme.Editor
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(new TextBlock { Text = "История файлов", FontSize = 20, Foreground = FocusTheme.Text });
        head.Children.Add(FocusButton(Button("Открыть…", () => Run(() => OpenDialogAsync())), primary: true));
        head.Children.Add(FocusButton(Button("Новый документ", () => { ShowGroups(); NewTab(); })));
        if (_tabs.Count > 0)
        {
            head.Children.Add(FocusButton(Button("К вкладкам", ShowGroups)));
        }

        surface.Children.Add(head);
        Grid.SetRow(input, 1);
        surface.Children.Add(input);
        var scroll = new ScrollViewer { Content = rows };
        Grid.SetRow(scroll, 2);
        surface.Children.Add(scroll);
        Grid.SetRow(actions, 3);
        surface.Children.Add(actions);
        WorkspaceHost.Content = surface;
        _lastHomeRefresh = DateTimeOffset.Now;
        _refreshHome = Rebuild;
        Rebuild();
        SetStatus("История файлов");
        FormatText.Text = "";
    }

    private async Task CheckActiveFileAsync()
    {
        if (_checkingDisk || ActiveTab is not { Path: { } path, Stamp: { } saved, Editor: not null } tab)
        {
            return;
        }

        _checkingDisk = true;
        try
        {
            var state = await Task.Run(() =>
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    return "файл отсутствует на диске";
                }

                return file.Length != saved.Length || file.LastWriteTimeUtc != saved.LastWriteUtc
                    ? "файл изменён на диске" : null;
            }, _lifetime.Token);
            if (Equals(tab.Stamp, saved) && ReferenceEquals(tab, ActiveTab) && tab.DiskStatus != state)
            {
                tab.DiskStatus = state;
                UpdateStatus(tab);
            }
        }
        finally
        {
            _checkingDisk = false;
        }
    }

    private void RefreshHistoryMenus()
    {
        if (_recentMenu is { } recent)
        {
            recent.Items.Clear();
            foreach (var item in _recent.Take(16))
            {
                recent.Items.Add(Command(Path.GetFileName(item.Path) + DescribeWork(item.LineCount, item.LastWorkedMs),
                    () => Run(() => OpenRecentAsync(item))));
            }

            recent.Items.Add(Command("Вся история", () => Run(ShowHomeAsync)));
        }

        if (_closedMenu is { } closed)
        {
            closed.Items.Clear();
            foreach (var item in _closedTabs)
            {
                var name = ClosedTabDisplayName(item);
                closed.Items.Add(Command(name + DescribeWork(item.LineCount, item.LastWorkedMs),
                    () => Run(() => RestoreClosedAsync(item))));
            }
        }
    }

    private string ClosedTabDisplayName(SessionTab tab)
    {
        if (tab.Path is { } path)
        {
            return Path.GetFileName(path);
        }

        var line = tab.UntitledName;
        if (string.IsNullOrWhiteSpace(line) && tab.DraftRef is not null && _storage.GetDraftPath(tab) is { } draftPath)
        {
            try
            {
                using var reader = new StreamReader(draftPath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
                var buffer = new char[1600];
                var count = reader.Read(buffer, 0, buffer.Length);
                line = new string(buffer, 0, count).Split(['\r', '\n'], 2)[0].Trim();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return "Без имени";
        }

        var builder = new StringBuilder();
        foreach (var rune in line.EnumerateRunes().Take(_titleLimit))
        {
            builder.Append(rune);
        }

        return builder.Length == 0 ? "Без имени" : builder.ToString();
    }

    private static string DescribeWork(long? lines, long? stamp)
    {
        var summary = lines is { } count ? $" · {count:N0} строк" : "";
        if (stamp is { } milliseconds)
        {
            var worked = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            var ago = DateTimeOffset.UtcNow - worked;
            summary += ago switch
            {
                { TotalMinutes: < 1 } => " · только что",
                { TotalHours: < 1 } => $" · {(int)ago.TotalMinutes} мин назад",
                { TotalDays: < 1 } => $" · {(int)ago.TotalHours} ч назад",
                _ => $" · {worked.LocalDateTime:g}"
            };
        }

        return summary;
    }
}
