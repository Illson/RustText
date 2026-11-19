using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
        public List<MainWindow> Windows { get; } = [];
        public List<SessionTab> ClosedTabs { get; } = [];
        public HashSet<long> RestoringClosedTabs { get; } = [];
        public Dictionary<long, SavedLiveDraft> LiveDrafts { get; } = [];
        public long NextId;
        public int? LastCheckpoint;
        public TabTransfer? Drag;
    }

    private sealed record SavedLiveDraft(WeakReference<TextDocument> Document, long Revision, SessionTab Saved);

    private sealed record TabTransfer(MainWindow Source, WorkTab Tab)
    {
        public string Token { get; } = Guid.NewGuid().ToString("N");
    }

    internal MainWindow? FindOtherWindow() =>
        _workspace.Windows.LastOrDefault(window => !ReferenceEquals(window, this) && !window._closed && window.IsVisible);

    private IEnumerable<WorkTab> TabsInVisualOrder()
    {
        var byItem = _tabs.Values.ToDictionary(tab => tab.Item);
        return Groups().SelectMany(group => group.Tabs.Items.OfType<TabItem>()).Select(item => byItem[item]);
    }

    private TabTransfer? GetTabTransfer(IDataTransfer data) =>
        _workspace.Drag is { } drag && data.TryGetValue(TabDrag) == drag.Token ? drag : null;

    private async Task DragTabAsync(PointerPressedEventArgs start, WorkTab tab)
    {
        if (_workspace.Drag is not null || tab.Loading || tab.Closing || !_tabs.ContainsKey(tab.Id)) return;

        var drag = new TabTransfer(this, tab);
        _workspace.Drag = drag;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(TabDrag, drag.Token));
            var effect = await DragDrop.DoDragDropAsync(start, data, DragDropEffects.Move);
            if (effect == DragDropEffects.None && TryGetTabDropPoint(out var point))
            {
                OpenTabWindow(tab, point);
            }
        }
        finally
        {
            _workspace.Drag = null;
        }
    }

    private bool TryGetTabDropPoint(out PixelPoint point)
    {
        point = default;
        // An Escape cancellation while the mouse is still held must leave the tab in place.
        if (!OperatingSystem.IsWindows() || !IsVisible || IsKeyDown(1) || IsKeyDown(0x1B) ||
            !GetCursorPos(out var cursor))
        {
            return false;
        }

        point = new PixelPoint(cursor.X, cursor.Y);
        var location = point;
        return !_workspace.Windows.Any(window => window.IsVisible && window.WindowState != WindowState.Minimized &&
            new Rect(window.Bounds.Size).Contains(window.PointToClient(location)));
    }

    private void OpenTabWindow(WorkTab tab, PixelPoint? dropPoint = null)
    {
        if (_closing || tab.Loading || tab.Closing || !_tabs.ContainsKey(tab.Id)) return;

        var window = new MainWindow([], _workspace)
        {
            _fontSize = _fontSize,
            _wordWrap = _wordWrap,
            _settings = _settings,
            _titleLimit = _titleLimit,
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
        if (tab.Closing || !_tabs.ContainsKey(tab.Id) || ReferenceEquals(tab, before)) return;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPosition
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPosition point);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
}
