using Avalonia.Threading;
using AvaloniaEdit.Document;
using RustText.Demo.Logs;

namespace RustText.Demo;

public partial class MainWindow
{
    private static bool TabAutoRefresh(WorkTab tab) =>
        tab.Log?.AutoRefresh ?? tab.Deferred?.Log?.AutoRefresh ?? tab.AutoRefresh;

    private void SetTabAutoRefresh(WorkTab tab, bool enabled)
    {
        tab.AutoRefresh = enabled;
        if (tab.Log is { } log) log.SetAutoRefresh(enabled);
        if (tab.Deferred is { } saved)
        {
            tab.Deferred = saved with
            {
                AutoRefresh = enabled,
                Log = saved.Log is { } state ? state with { AutoRefresh = enabled } : null
            };
        }
        WatchEditorFile(tab);
        tab.FileWatch?.Schedule(force: true);
    }

    private void WatchEditorFile(WorkTab tab)
    {
        if (tab.AutoRefresh && tab is { Editor: { }, Path: { } path, Stamp: not null })
        {
            if (tab.FileWatch is { } existing && SamePath(existing.Path, path)) return;
            tab.FileWatch?.Dispose();
            tab.FileWatch = new EditorFileWatch(path, (watch, attempt) => RefreshEditorFileAsync(tab, watch, attempt));
            tab.FileWatch.Schedule(force: true);
            return;
        }
        tab.FileWatch?.Dispose();
        tab.FileWatch = null;
    }

    private async Task<bool> RefreshEditorFileAsync(WorkTab tab, EditorFileWatch watch, int attempt)
    {
        if (!IsCurrent() || tab.Editor is not { } editor || tab.Stamp is not { } stamp) return false;
        if (tab.Loading || DocumentViews(tab).Any(view => view.Tab.Saving)) return true;
        try
        {
            var file = new FileInfo(watch.Path);
            if (!file.Exists) throw new FileNotFoundException("Файл отсутствует на диске.", watch.Path);
            LoadedDocument? loaded = null;
            if (file.Length <= DocumentFiles.MaximumDocumentBytes)
            {
                // Keep accepting edits until the disk snapshot is ready.
                loaded = await DocumentFiles.ReadAsync(watch.Path, tab.Encoding, watch.Token);
                if (!IsCurrent() || tab.Stamp != stamp || !ReferenceEquals(tab.Editor, editor)) return false;
                if (tab.Loading || DocumentViews(tab).Any(view => view.Tab.Saving)) return true;
                if (loaded.Stamp.Sha256 == stamp.Sha256)
                {
                    tab.Stamp = loaded.Stamp;
                    if (tab.DiskStatus != ExternalDraftStatus) ReportDiskState(null);
                    return false;
                }
            }

            var dirty = tab.Dirty;
            if (loaded is null || IsOversizedEditorText(loaded.Text))
            {
                // Open the paged editor before moving the draft: opening the replacement can fail.
                var views = DocumentViews(tab);
                AttachLog(tab, watch.Path, tab.Encoding, new LogTabState(tab.Encoding, WordWrap: _wordWrap,
                    AutoRefresh: tab.AutoRefresh, EditPages: true), preserveEditorChanges: dirty);
                if (dirty) tab.Log?.ReportPreservedExternalEdit();
                foreach (var (owner, peer) in views)
                {
                    if (ReferenceEquals(peer, tab)) continue;
                    owner.AttachLog(peer, watch.Path, peer.Encoding, new LogTabState(peer.Encoding, WordWrap: owner._wordWrap,
                        AutoRefresh: peer.AutoRefresh, EditPages: true));
                    peer.RestoredDirty = peer.Dirty = false;
                    owner.UpdateTab(peer);
                }
            }
            else
            {
                ReplaceEditorFromDisk(tab, editor, loaded, dirty ? ExternalDraftStatus : null, preserveEditorChanges: dirty);
            }

            if (dirty) Run(SaveSessionAsync);
        }
        catch (OperationCanceledException) when (watch.Token.IsCancellationRequested) { }
        catch (IOException error) when (IsCurrent())
        {
            if (attempt < 3) return true;
            ReportDiskState(error is FileNotFoundException or DirectoryNotFoundException
                ? "Файл отсутствует на диске." : $"Не удалось обновить файл. {error.Message}");
        }
        catch (Exception error) when (IsCurrent() && error is UnauthorizedAccessException or InvalidDataException)
        {
            ReportDiskState($"Не удалось обновить файл. {error.Message}");
        }
        catch (Exception error) when (IsUserError(error) && !IsCurrent()) { }
        return false;

        bool IsCurrent() => !_closing && !tab.Closing && tab.AutoRefresh && ReferenceEquals(tab.FileWatch, watch);

        void ReportDiskState(string? message)
        {
            if (tab.DiskStatus != message)
            {
                tab.DiskStatus = message;
                if (ReferenceEquals(tab, ActiveTab)) UpdateStatus(tab);
            }
        }
    }

    internal const string ExternalDraftStatus = "Файл обновлён с диска. Локальные правки перенесены в отдельную вкладку-черновик.";

    private void PreserveEditorDraft(WorkTab source, SessionTab view)
    {
        var document = DetachEditor(source)!;
        var draft = AddExternalDraft(source, document);
        RestoreEditorView(draft, view);
    }

    private void PreservePageDraft(WorkTab source, string text)
    {
        if (IsOversizedEditorText(text))
        {
            var draft = AddRecoveryTab(source);
            Run(() => PersistRecoveredPageAsync(draft, text));
        }
        else
        {
            AddExternalDraft(source, new TextDocument(text));
            Run(SaveSessionAsync);
        }
    }

    private (Dictionary<long, WorkTab> Tabs, StorageService Storage)? FindTabWorkspace(WorkTab tab)
    {
        if (_tabs.TryGetValue(tab.Id, out var active) && ReferenceEquals(active, tab))
            return (_tabs, SessionStorage);
        foreach (var view in _workspaceViews.Values)
        {
            if (view.Tabs.TryGetValue(tab.Id, out var cached) && ReferenceEquals(cached, tab))
                return (view.Tabs, view.Storage);
        }
        return null;
    }

    private WorkTab AddRecoveryTab(WorkTab source)
    {
        var (tabs, _) = FindTabWorkspace(source)
            ?? throw new InvalidOperationException("Рабочее пространство исходной вкладки закрыто.");
        return AddTabTo(tabs, source.Group, id: null, select: false);
    }

    private WorkTab AddExternalDraft(WorkTab source, TextDocument document)
    {
        var draft = AddRecoveryTab(source);
        draft.Encoding = source.Encoding;
        draft.Ending = source.Ending;
        draft.OriginalEndings = source.OriginalEndings;
        draft.RestoredDirty = draft.Dirty = true;
        AttachEditor(draft, document);
        return draft;
    }

    private sealed class EditorFileWatch : IDisposable
    {
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly CancellationTokenSource _stop = new();
        private readonly FileSystemWatcher? _watcher;
        private readonly Func<EditorFileWatch, int, Task<bool>> _refresh;
        private int _pendingNotification;
        private int _attempt;
        private bool _refreshing;
        private bool _forceProbe;
        private (bool Exists, long Length, DateTime Modified)? _fallbackObservation;

        public EditorFileWatch(string path, Func<EditorFileWatch, int, Task<bool>> refresh)
        {
            Path = path;
            _refresh = refresh;
            Token = _stop.Token;
            _debounce.Tick += Tick;
            try
            {
                var watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                _watcher = watcher;
                watcher.Changed += Handle;
                watcher.Created += Handle;
                watcher.Deleted += Handle;
                watcher.Renamed += (_, _) => Notify();
                watcher.Error += (_, _) => Notify();
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _watcher?.Dispose();
                _watcher = null;
                _debounce.Interval = TimeSpan.FromSeconds(5);
                _debounce.Start();
            }
        }

        public string Path { get; }
        public CancellationToken Token { get; }

        private void Handle(object sender, FileSystemEventArgs args) => Notify();

        private void Notify()
        {
            if (Token.IsCancellationRequested || Interlocked.Exchange(ref _pendingNotification, 1) != 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _pendingNotification, 0);
                Schedule(force: true);
            });
        }

        public void Schedule(bool force)
        {
            if (Token.IsCancellationRequested) return;
            if (force)
            {
                _attempt = 0;
                _forceProbe = true;
            }
            _debounce.Stop();
            _debounce.Start();
        }

        private async void Tick(object? sender, EventArgs args)
        {
            if (Token.IsCancellationRequested) return;
            if (_refreshing) return;
            _debounce.Stop();
            _refreshing = true;
            try
            {
                if (_watcher is null && !_forceProbe && _attempt == 0)
                {
                    // Poll only metadata until notifications become available again.
                    var file = new FileInfo(Path);
                    var observation = (file.Exists, file.Exists ? file.Length : 0, file.LastWriteTimeUtc);
                    if (observation == _fallbackObservation) return;
                    _fallbackObservation = observation;
                }
                _forceProbe = false;
                if (await _refresh(this, _attempt++)) Schedule(force: false);
                else _attempt = 0;
            }
            catch (Exception error) when (IsUserError(error)) { }
            finally
            {
                _refreshing = false;
                if (_watcher is null && !Token.IsCancellationRequested) _debounce.Start();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _debounce.Stop();
            _watcher?.Dispose();
            _stop.Dispose();
        }
    }
}
