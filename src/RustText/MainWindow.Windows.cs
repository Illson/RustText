using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Document;

namespace RustText.Demo;

public partial class MainWindow
{
    private readonly WorkspaceSession _workspace;
    private readonly bool _restoreWorkspace;

    private sealed class WorkspaceSession
    {
        public StorageService Storage { get; } = StorageService.OpenDefault();
        public SemaphoreSlim SaveGate { get; } = new(1, 1);
        public SemaphoreSlim CloseGate { get; } = new(1, 1);
        public SemaphoreSlim SettingsGate { get; } = new(1, 1);
        public UserSettings Settings { get; set; } = new();
        public bool SettingsReady { get; set; }
        public List<MainWindow> Windows { get; } = [];
        public List<SessionTab> ClosedTabs { get; } = [];
        public HashSet<long> ClosedTabOperations { get; } = [];
        public Dictionary<long, SavedLiveDraft> LiveDrafts { get; } = [];
        public long NextId;
        public int? LastCheckpoint;
        public TabDrag? Drag;
    }

    private sealed record SavedLiveDraft(WeakReference<TextDocument> Document, long Revision, SessionTab Saved);

    internal MainWindow? FindOtherWindow() =>
        _workspace.Windows.LastOrDefault(window => !ReferenceEquals(window, this) && !window._closed && window.IsVisible);

    private IEnumerable<WorkTab> TabsInVisualOrder()
    {
        var byItem = _tabs.Values.ToDictionary(tab => tab.Item);
        return Groups().SelectMany(group => group.Tabs.Items.OfType<TabItem>()).Select(item => byItem[item]);
    }

    private void OpenTabWindow(WorkTab tab, PixelPoint? dropPoint = null)
    {
        if (!CanDragTab(tab)) return;

        var window = new MainWindow([], _workspace)
        {
            _fontSize = _fontSize,
            _wordWrap = _wordWrap,
            _recent = _recent
        };
        if (dropPoint is { } point)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var x = point.X - 65;
            var y = point.Y - 18;
            if (Screens.ScreenFromPoint(point) is { } screen)
            {
                var work = screen.WorkingArea;
                window.Width = Math.Min(window.Width, Math.Max(window.MinWidth, work.Width / screen.Scaling));
                window.Height = Math.Min(window.Height, Math.Max(window.MinHeight, work.Height / screen.Scaling));
                var width = (int)Math.Ceiling(window.Width * screen.Scaling);
                var height = (int)Math.Ceiling(window.Height * screen.Scaling);
                x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - width));
                y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - height));
            }
            window.Position = new PixelPoint(x, y);
        }

        App.TrackWindow(window);
        window.Show();
        try
        {
            TransferTab(tab, window, window._active, null);
        }
        catch
        {
            window.Close();
            throw;
        }

        window.Activate();
    }

    private void TransferTab(WorkTab tab, MainWindow destination, Group group, WorkTab? before)
    {
        if (tab.Closing || tab.Saving || tab.Log?.HasPendingEdit == true || !_tabs.ContainsKey(tab.Id) || ReferenceEquals(tab, before)) return;
        if (ReferenceEquals(this, destination))
        {
            MoveTab(tab, group, before);
            return;
        }

        var saved = CaptureTab(tab);
        var copy = destination.AddTab(tab.Id, group, select: false);
        try
        {
            destination.SetDeferred(copy, saved);
            if (tab.Editor is { } editor)
            {
                destination.AttachEditor(copy, editor.Document);
                copy.Deferred = null;
                copy.Dirty = tab.Dirty;
                copy.RestoredDirty = tab.RestoredDirty;
                copy.Revision = tab.Revision;
                copy.DiskStatus = tab.DiskStatus;
                copy.LastWorked = tab.LastWorked;
                RestoreEditorView(copy, saved);
                destination.UpdateTab(copy);
            }
        }
        catch
        {
            destination.RemoveTab(copy);
            throw;
        }

        RemoveTab(tab);
        destination.MoveTab(copy, group, before);
        if (_tabs.Count == 0 && !_closing && destination.IsVisible)
        {
            Close();
        }
    }

}
