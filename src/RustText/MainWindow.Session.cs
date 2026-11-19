using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RustText.Demo.Editor;
using RustText.Demo.Logs;

namespace RustText.Demo;

public partial class MainWindow
{
    private readonly StorageService _storage;
    private readonly List<SessionTab> _closedTabs;
    private IReadOnlyList<RecentFileEntry> _recent = [];
    private MenuItem? _recentMenu;
    private MenuItem? _closedFilesMenu;
    private MenuItem? _closedDraftsMenu;
    private Action? _refreshHome;
    private DateTimeOffset _lastHomeRefresh;
    private SessionState? _pendingSession;
    private bool _validatingStartupSession;
    private bool _mountingSessionTabs;

    private async Task RestoreSessionAsync()
    {
        _validatingStartupSession = true;
        var restore = _storage.LoadSessionAsync(_lifetime.Token, ShowPendingSessionAsync);
        try
        {
            try
            {
                _workspace.Settings = await _storage.LoadSettingsAsync(_lifetime.Token);
            }
            finally
            {
                _workspace.SettingsReady = true;
                ApplyWorkspaceTheme();
            }
            _recent = await _storage.GetRecentFilesAsync(_lifetime.Token);
        }
        catch (Exception error) when (IsUserError(error))
        {
            SetStatus(error.Message);
        }

        SessionState? state;
        try { state = await restore; }
        finally { _validatingStartupSession = false; }
        if (_closed) return;

        if (state is null && _pendingSession is null)
        {
            RefreshHistoryMenus();
            return;
        }

        if (state is not null) _closedTabs.AddRange((state.ClosedTabs ?? []).Take(100));
        if (state is null || !ReferenceEquals(state, _pendingSession))
        {
            MountSessionTabs(state ?? new SessionState([]), pending: false);
        }
        else
        {
            foreach (var tab in _tabs.Values.Where(tab => tab.CheckingSession))
            {
                tab.CheckingSession = false;
                tab.Item.Content = DeferredContent(tab);
            }
        }
        _pendingSession = null;

        if (SelectedIn(_active) is { } selected)
            await GuardAsync(() => ActivateDeferredAsync(selected));

        RefreshHistoryMenus();
    }

    private async Task ShowPendingSessionAsync(SessionState state)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_closed || _pendingSession is not null) return;
            _pendingSession = state;
            MountSessionTabs(state, pending: true);
        });
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private void MountSessionTabs(SessionState state, bool pending)
    {
        _mountingSessionTabs = true;
        try
        {
            foreach (var tab in _tabs.Values.Where(tab => tab.CheckingSession).ToArray()) RemoveTab(tab);
            var liveTabs = TabsInVisualOrder().ToArray();
            foreach (var tab in liveTabs) tab.Group.Tabs.Items.Remove(tab.Item);

            Width = Math.Clamp(state.WindowWidth, MinWidth, 4000);
            Height = Math.Clamp(state.WindowHeight, MinHeight, 2400);
            WindowState = state.Maximized ? WindowState.Maximized : WindowState.Normal;

            _wordWrap = state.Wrap;
            _fontSize = Math.Clamp(state.FontSize, 8, 48);
            _workspace.NextId = Math.Max(_workspace.NextId, state.Tabs.Concat(state.ClosedTabs ?? []).Select(tab => tab.Id).DefaultIfEmpty().Max());
            _root = state.Layout is { } layout ? RestoreLayout(layout) : MakeGroup();
            var groups = Groups().ToArray();
            _active = groups.FirstOrDefault(group => group.Id == state.ActiveGroupId) ?? groups[0];
            foreach (var saved in state.Tabs)
            {
                if (_tabs.ContainsKey(saved.Id)) continue;
                var group = FindSavedGroup(state.Layout, saved.Id);
                var tab = AddTab(saved.Id, groups.FirstOrDefault(current => current.Id == group) ?? _active, false);
                tab.CheckingSession = pending;
                SetDeferred(tab, saved);
            }

            foreach (var group in groups)
            {
                if (FindSavedSelection(state.Layout, group.Id) is { } id && _tabs.TryGetValue(id, out var tab))
                    group.Tabs.SelectedItem = tab.Item;
            }

            if (state.ActiveGroupId is null && state.Active >= 0 && state.Active < state.Tabs.Count
                && _tabs.TryGetValue(state.Tabs[state.Active].Id, out var active))
            {
                _active = active.Group;
                _active.Tabs.SelectedItem = active.Item;
            }

            foreach (var tab in liveTabs)
            {
                tab.Group = _active;
                _active.Tabs.Items.Add(tab.Item);
            }
            if (liveTabs.LastOrDefault() is { } live) _active.Tabs.SelectedItem = live.Item;
            ShowGroups();
        }
        finally { _mountingSessionTabs = false; }
    }

    private Group RestoreLayout(WorkspaceLayout state)
    {
        _workspace.NextId = Math.Max(_workspace.NextId, state.GroupId);
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
            return tab.Log is { } preview
                ? recovered with
                {
                    DraftOffset = preview.CurrentOffset ?? recovered.DraftOffset,
                    LineCount = preview.KnownLineCount
                }
                : recovered;
        }

        var editor = tab.Editor;
        var log = tab.Log?.CaptureState() ?? (tab.PendingFileOpenMode is FileOpenMode.PagedEditor or FileOpenMode.Log
            ? new LogTabState(tab.Encoding, WordWrap: _wordWrap, AutoRefresh: tab.AutoRefresh,
                EditPages: tab.PendingFileOpenMode == FileOpenMode.PagedEditor) : null);
        var caret = editor?.CaretOffset ?? 0;
        var selection = editor is null ? default : MultiCaretEditing.Primary(editor.TextArea);
        var selectionStart = selection.Start;
        var selectionLength = selection.Length;
        var lineEndings = tab.OriginalEndings.IsDefaultOrEmpty ? null : tab.OriginalEndings.Select(value => (byte)value).ToArray();
        return new SessionTab(tab.Id, tab.Path, tab.Encoding, selectionStart, selectionLength,
            caret < selectionStart + selectionLength, editor?.HorizontalOffset ?? 0, editor?.VerticalOffset ?? 0,
            LogOffset: log?.Offset, LineCount: editor?.Document.LineCount,
            LastWorkedMs: tab.LastWorked.ToUnixTimeMilliseconds(), LineEnding: tab.Ending,
            LineEndingMap: lineEndings, Stamp: tab.Stamp, Log: log)
        {
            JsonValidationDisabled = tab.JsonDisabled,
            AutoRefresh = tab.AutoRefresh,
            UntitledName = tab.Path is null ? UntitledName(tab) : null
        };
    }

    private Task SaveSessionAsync() => SaveWorkspaceSessionAsync(excludeWindow: null);

    private async Task SaveWorkspaceSessionAsync(MainWindow? excludeWindow, SessionTab? forgetClosedTab = null)
    {
        var forgetClosedId = forgetClosedTab?.Id;
        if (!_restoreFinished && !_closing && forgetClosedId is null)
        {
            return;
        }

        await _workspace.SaveGate.WaitAsync();
        try
        {
            var windows = _workspace.Windows.Where(window => !window._closed && !ReferenceEquals(window, excludeWindow)).ToArray();
            if (windows.Length == 0) return;
            var homeWindow = windows[0];
            var entries = windows.SelectMany(window => window.TabsInVisualOrder().Select(tab => (Window: window, Tab: tab))).ToArray();
            var tabs = entries.Select(entry => entry.Tab).ToArray();
            var recoveries = tabs.Select(tab => tab.Recovery).ToArray();
            var state = entries.Select(entry => entry.Window.CaptureTab(entry.Tab)).ToList();
            var index = homeWindow.ActiveTab is { } active ? Array.IndexOf(tabs, active) : 0;
            var width = double.IsFinite(homeWindow.Width) ? homeWindow.Width : 1080;
            var height = double.IsFinite(homeWindow.Height) ? homeWindow.Height : 700;
            var session = new SessionState(state, _closedTabs.Where(tab => tab.Id != forgetClosedId).Take(100).ToList(),
                Math.Max(0, index), width, height, homeWindow.WindowState == WindowState.Maximized,
                homeWindow._wordWrap, homeWindow._fontSize, homeWindow.CaptureLayout(homeWindow._root), homeWindow._active.Id);
            var fingerprint = CheckpointFingerprint(session, tabs);
            if (forgetClosedId is null && !_closing && _workspace.LastCheckpoint == fingerprint) return;

            var drafts = new Dictionary<long, Func<TextReader>>();
            var liveDrafts = new Dictionary<long, (AvaloniaEdit.Document.TextDocument Document, long Revision)>();
            for (var i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                if (tab.Editor is not { } editor || !(tab.Dirty || tab.Path is null)) continue;

                var document = editor.Document;
                liveDrafts.Add(tab.Id, (document, tab.Revision));
                if (FindSavedLiveDraft(tab) is { } previous)
                {
                    session.Tabs[i] = session.Tabs[i] with { DraftRef = previous.DraftRef, DraftSha256 = previous.DraftSha256 };
                }
                else
                {
                    var snapshot = document.CreateSnapshot();
                    drafts.Add(tab.Id, snapshot.CreateReader);
                }
            }

            var committed = await _storage.SaveSessionWithDraftReadersAsync(session, drafts);
            _workspace.LiveDrafts.Clear();
            for (var i = 0; i < tabs.Length; i++)
            {
                var captured = session.Tabs[i];
                var saved = committed.Tabs[i];
                if (ReferenceEquals(tabs[i].Deferred, captured)) tabs[i].Deferred = saved;
                if (recoveries[i] is { } recovery && ReferenceEquals(tabs[i].Recovery, recovery)) tabs[i].Recovery = saved;
                if (saved.DraftRef is not null && liveDrafts.TryGetValue(saved.Id, out var live))
                {
                    _workspace.LiveDrafts.Add(saved.Id, new(new(live.Document), live.Revision, saved));
                }
            }

            var capturedClosed = session.ClosedTabs ?? [];
            for (var i = 0; i < Math.Min(capturedClosed.Count, committed.ClosedTabs?.Count ?? 0); i++)
            {
                var closedIndex = _closedTabs.FindIndex(item => ReferenceEquals(item, capturedClosed[i]));
                if (closedIndex >= 0) _closedTabs[closedIndex] = committed.ClosedTabs![i];
            }
            _workspace.LastCheckpoint = fingerprint;
            if (forgetClosedTab is { } removed)
            {
                _closedTabs.RemoveAll(tab => tab.Id == removed.Id);
                _workspace.LastCheckpoint = null;
                var removeBackupDraft = removed.DraftRef is not null && committed.Tabs.All(tab => tab.Id != removed.Id);
                await _storage.RemoveHistoryFromPreviousCheckpointAsync(removed.Id, removeBackupDraft);
                _workspace.LastCheckpoint = fingerprint;
            }
        }
        finally
        {
            _workspace.SaveGate.Release();
        }
    }

    private SessionTab? FindSavedLiveDraft(WorkTab tab) =>
        _workspace.LiveDrafts.TryGetValue(tab.Id, out var saved)
        && saved.Revision == tab.Revision
        && saved.Document.TryGetTarget(out var document)
        && ReferenceEquals(document, tab.Editor?.Document) ? saved.Saved : null;

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
        tab.AutoRefresh = saved.AutoRefresh ?? true;
        tab.LastWorked = saved.LastWorkedMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.Now;
        tab.Deferred = saved;
        tab.Item.Content = DeferredContent(tab);
        UpdateTab(tab);
    }

    private Control DeferredContent(WorkTab tab) => tab.CheckingSession
        ? new TextBlock { Text = "Проверка сохранённой вкладки…", Margin = new Thickness(18) }
        : FocusButton(Button("Загрузить вкладку", () => Run(() => ActivateDeferredAsync(tab))), primary: true);

    private async Task ActivateDeferredAsync(WorkTab tab)
    {
        if (tab.Deferred is not { } saved || tab.Loading || tab.CheckingSession || _closed)
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
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(tab.Deferred, previousDeferred))
            {
                tab.Deferred = saved;
            }
            if ((saved.Log is not null || saved.LogOffset is not null) && saved.Path is { } logPath)
            {
                RestoreSavedLog(tab, logPath, saved);
            }
            else if (saved.DraftRef is not null)
            {
                var draftPath = _storage.GetDraftPath(saved);
                if (draftPath is not null && new FileInfo(draftPath).Length > LineGeneratorThreshold)
                {
                    AttachPagedDraftEditor(tab, saved, draftPath);
                }
                else
                {
                    var draft = await _storage.LoadDraftAsync(saved, token) ?? "";
                    if (draftPath is not null && IsOversizedEditorText(draft))
                    {
                        AttachPagedDraftEditor(tab, saved, draftPath);
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
                if (new FileInfo(path).Length > DocumentFiles.MaximumDocumentBytes)
                    AttachLog(tab, path, saved.Encoding,
                        new LogTabState(saved.Encoding, WordWrap: _wordWrap, AutoRefresh: tab.AutoRefresh, EditPages: true));
                else
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

    private void RestoreSavedLog(WorkTab tab, string path, SessionTab saved)
    {
        var state = saved.Log ?? new LogTabState(saved.Encoding, saved.LogOffset ?? 0, WordWrap: _wordWrap);
        AttachLog(tab, path, state.Encoding, state);
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

    private void AttachPagedDraftEditor(WorkTab tab, SessionTab saved, string draftPath, int? pageCaret = null,
        bool focus = false, long? byteCaret = null)
    {
        var name = saved.Path is { } path ? Path.GetFileName(path) : saved.UntitledName;
        var state = new LogTabState(TextEncodingKind.Utf8, saved.DraftOffset ?? 0, WordWrap: _wordWrap);
        var lineCount = saved.DraftOffset.HasValue ? saved.LineCount : null;
        AttachLog(tab, draftPath, TextEncodingKind.Utf8, state, name ?? "Черновик", lineCount, initialDraftCaretBytes: byteCaret);
        tab.Recovery = saved;
        UpdateTab(tab);
        var editor = tab.Log!;
        if (pageCaret is not null || focus) RestorePagedEditorCaret(tab, editor, pageCaret, focus);
        editor.CommitDraftPage = async (temporary, hash, offset, caret, focusEditor, token) =>
        {
            await _workspace.SaveGate.WaitAsync(token);
            try
            {
                var current = tab.Recovery ?? throw new IOException("Черновик недоступен.");
                var edited = _storage.AdoptEditedDraft(current, temporary, hash) with { DraftOffset = offset, LineCount = null };
                AttachPagedDraftEditor(tab, edited, _storage.GetDraftPath(edited)!, caret, focusEditor);
            }
            finally { _workspace.SaveGate.Release(); }
            await GuardAsync(async () =>
            {
                await SaveSessionAsync();
                if (ReferenceEquals(tab, ActiveTab)) SetStatus("Страница черновика сохранена.");
            });
        };
    }

    private void RestorePagedEditorCaret(WorkTab tab, LogView editor, int? pageCaret, bool focus) => Run(async () =>
    {
        await editor.InitialPageLoad;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(tab.Log, editor)) return;
            if (pageCaret is { } caret) editor.RestorePageCaret(caret, focus: false);
            if (focus && ReferenceEquals(tab, ActiveTab)) editor.FocusPageEditor();
        }, DispatcherPriority.Loaded);
    });

    private async Task SaveCurrentDraftCopyAsync(WorkTab tab)
    {
        if (tab.Log?.HasPendingEdit == true) throw new IOException("Сохраните страницу перед созданием копии.");
        if (tab.Recovery is not { } recovery) return;
        var current = await _storage.ResolveCurrentDraftAsync(recovery, _lifetime.Token);
        if (ReferenceEquals(tab.Recovery, recovery)) tab.Recovery = current;
        if (_storage.GetDraftPath(current) is { } path) await SaveDraftCopyAsync(path);
    }

    private async Task RememberClosedAsync(
        WorkTab tab,
        bool preserveUnsavedText = false,
        bool deferHistoryLimit = false)
    {
        await _workspace.SaveGate.WaitAsync(_lifetime.Token);
        try
        {
            var saved = CaptureTab(tab);
            if (tab.Editor is { } editor && (tab.Path is null || (preserveUnsavedText && tab.Dirty)))
            {
                if (FindSavedLiveDraft(tab) is { } existingDraft)
                {
                    saved = saved with { DraftRef = existingDraft.DraftRef, DraftSha256 = existingDraft.DraftSha256 };
                }
                else
                {
                    var snapshot = editor.Document.CreateSnapshot();
                    saved = await _storage.SaveClosedDraftAsync(saved, snapshot.CreateReader, _lifetime.Token);
                }
            }

            _closedTabs.RemoveAll(item => item.Id == saved.Id);
            _closedTabs.Insert(0, saved);
            if (!deferHistoryLimit && _closedTabs.Count > 100) _closedTabs.RemoveAt(_closedTabs.Count - 1);
        }
        finally { _workspace.SaveGate.Release(); }

        RefreshHistoryMenus();
    }

    private Task RestoreLastClosedAsync() => _closedTabs.Count > 0 ? RestoreClosedAsync(_closedTabs[0]) : Task.CompletedTask;

    private async Task RestoreClosedAsync(SessionTab saved)
    {
        if (!_closedTabs.Any(tab => tab.Id == saved.Id) || !_workspace.ClosedTabOperations.Add(saved.Id))
        {
            return;
        }

        try
        {
            await RestoreClosedCoreAsync(saved);
        }
        finally
        {
            _workspace.ClosedTabOperations.Remove(saved.Id);
        }
    }

    private async Task RestoreClosedCoreAsync(SessionTab saved)
    {
        if (saved is { Path: { } path, DraftRef: null })
        {
            var isLog = saved.Log is not null || saved.LogOffset is not null;
            var opensAsLog = isLog && saved.Log?.EditPages != true;
            var existing = _tabs.Values.FirstOrDefault(tab =>
                tab.OpensAsLog == opensAsLog && tab.Recovery is null && SamePath(tab.Path, path));
            if (existing is not null)
            {
                if (existing.Loading)
                {
                    Select(existing);
                    return;
                }

                if (!existing.Dirty && existing.Log?.HasPendingEdit != true && existing.Deferred?.DraftRef is null)
                {
                    if (existing.Deferred is not null)
                    {
                        SetDeferred(existing, saved with { Id = existing.Id });
                        await ActivateDeferredAsync(existing);
                    }
                    else if (isLog)
                    {
                        RestoreSavedLog(existing, path, saved);
                    }
                    else if (existing.Editor is not null)
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
        var selection = editor is null ? default : MultiCaretEditing.Primary(editor.TextArea);
        var entry = new RecentFileEntry(path, tab.Encoding, tab.IsLog, selection.Start,
            selection.Length, log?.Offset, editor?.Document.LineCount,
            Reversed: editor is not null && editor.CaretOffset < selection.End,
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
        var encoding = entry.Log is { } bookmark ? bookmark.Encoding : entry.Encoding;
        var tab = entry.Log?.EditPages == true
            ? await OpenSinglePathAsync(entry.Path, asLog: false, encoding, editPages: true)
            : await OpenPathAsync(entry.Path, entry.ViewAsLog, encoding);
        if (tab is not null && _tabs.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, tab))
        {
            if (tab.Log is { } paged)
            {
                if (paged.HasPendingEdit || tab.Recovery is not null || entry.Log is null && entry.LogOffset is null) return;
                var log = (entry.Log ?? new LogTabState(entry.Encoding, entry.LogOffset ?? 0)) with
                {
                    WordWrap = _wordWrap,
                    EditPages = paged.EditsPagesByDefault
                };
                AttachLog(tab, entry.Path, encoding, log);
                if (paged.EditsPagesByDefault) RestorePagedEditorCaret(tab, tab.Log!, null, focus: true);
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

    private void RefreshHistoryMenus()
    {
        foreach (var window in _workspace.Windows.ToArray())
        {
            window._recent = _recent;
            window.RefreshHistoryMenusLocal();
            window._refreshHome?.Invoke();
        }
    }

    private void RefreshHistoryMenusLocal()
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

        if (_closedFilesMenu is { } closedFiles)
        {
            closedFiles.Items.Clear();
            foreach (var item in _closedTabs.Where(item => item.Path is not null))
            {
                var name = ClosedTabDisplayName(item);
                closedFiles.Items.Add(Command(name + DescribeWork(item.LineCount, item.LastWorkedMs),
                    () => Run(() => RestoreClosedAsync(item))));
            }
        }

        if (_closedDraftsMenu is { } closedDrafts)
        {
            closedDrafts.Items.Clear();
            foreach (var item in _closedTabs.Where(item => item.Path is null))
            {
                var name = ClosedTabDisplayName(item);
                closedDrafts.Items.Add(Command(name + DescribeWork(item.LineCount, item.LastWorkedMs),
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
        foreach (var rune in line.EnumerateRunes().Take(Settings.UntitledTabLimit))
        {
            builder.Append(rune);
        }

        return builder.Length == 0 ? "Без имени" : builder.ToString();
    }

    private static string DescribeWork(long? lines, long? stamp)
    {
        var summary = lines is { } count ? $" · {LineNumbers.Format(count)} строк" : "";
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
