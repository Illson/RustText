using Avalonia.Threading;

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
            return;
        }
        tab.FileWatch?.Dispose();
        tab.FileWatch = null;
    }

    private async Task<bool> RefreshEditorFileAsync(WorkTab tab, EditorFileWatch watch, int attempt)
    {
        if (!IsCurrent() || tab.Editor is not { } editor || tab.Stamp is not { } stamp) return false;
        if (tab.Saving || tab.Loading) return true;
        var revision = tab.Revision;
        try
        {
            var file = new FileInfo(watch.Path);
            if (!file.Exists) throw new FileNotFoundException("Файл отсутствует на диске.", watch.Path);
            if (tab.Dirty)
            {
                var unchanged = file.Length == stamp.Length && file.LastWriteTimeUtc == stamp.LastWriteUtc
                    && await DocumentFiles.GetStampAsync(watch.Path, watch.Token) == stamp;
                if (IsCurrent() && tab.Stamp == stamp)
                    ReportDiskState(unchanged ? null : "Файл изменён на диске. Несохранённые правки сохранены в редакторе.");
                return false;
            }

            if (file.Length > LogThreshold)
            {
                AttachLog(tab, watch.Path, tab.Encoding);
                return false;
            }

            // Keep the old view while the new snapshot is read. Check again before publishing it.
            var loaded = await DocumentFiles.ReadAsync(watch.Path, tab.Encoding, watch.Token);
            if (!IsCurrent() || tab.Stamp != stamp || !ReferenceEquals(tab.Editor, editor)) return false;
            if (tab.Saving) return true;
            if (tab.Dirty || tab.Revision != revision)
            {
                if (loaded.Stamp != stamp) ReportDiskState("Файл изменён на диске. Несохранённые правки сохранены в редакторе.");
            }
            else if (loaded.Stamp != stamp)
            {
                if (IsOversizedEditorText(loaded.Text)) AttachLog(tab, watch.Path, tab.Encoding);
                else await ApplyEditorReloadAsync(tab, editor, Task.FromResult(loaded));
            }
            else
            {
                ReportDiskState(null);
            }
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
