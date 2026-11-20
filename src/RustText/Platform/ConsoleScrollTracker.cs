using System.Runtime.InteropServices;

namespace RustText.Demo.Platform;

// Classic conhost reports the number of scrolled cells even when all the text is identical.
// Out-of-context callbacks run on this worker thread when it pumps its message queue.
internal sealed partial class ConsoleScrollTracker : IDisposable
{
    internal const uint ConsoleUpdateRegion = 0x4002;
    internal const uint ConsoleUpdateScroll = 0x4004;
    internal const uint ConsoleLayout = 0x4005;
    private readonly nint _consoleWindow;
    private readonly WinEventProcedure _callback;
    private readonly nint _hook;
    private int _bottomRow = -1;
    private long _scroll;
    private int _scrollCount;
    private int _firstScrollDelta;
    private bool _sawUpdate;
    private bool _layoutChanged;
    private bool _leadingLayout;
    private int _bottomRegionRows;
    private bool _historyRegionUpdated;
    private long _eventVersion;

    private ConsoleScrollTracker(nint window)
    {
        _consoleWindow = window;
        _callback = OnScroll;
        // The event source can differ from the process that owns the console HWND.
        _hook = SetWinEventHook(ConsoleUpdateRegion, ConsoleLayout, 0, _callback, 0, 0, 0);
    }

    public static ConsoleScrollTracker? TryCreate()
    {
        var window = GetConsoleWindow();
        if (window == 0) return null;
        var tracker = new ConsoleScrollTracker(window);
        return tracker._hook != 0 ? tracker : null;
    }

    public ConsoleEventBatch ReadEvents(int bottomRow)
    {
        _bottomRow = bottomRow;
        while (PeekMessage(out var message, 0, 0, 0, 1))
            DispatchMessage(ref message);
        return new ConsoleEventBatch(_scroll, _scrollCount, _firstScrollDelta, _layoutChanged, _leadingLayout,
            _bottomRegionRows, _historyRegionUpdated && _bottomRegionRows == 0 && _scrollCount == 0,
            _eventVersion);
    }

    private void OnScroll(nint hook, uint eventType, nint window, int dx, int dy, uint thread, uint time)
    {
        if (window != _consoleWindow) return;
        _eventVersion++;

        if (eventType == ConsoleLayout)
        {
            _layoutChanged = true;
            if (!_sawUpdate) _leadingLayout = true;
            return;
        }

        if (eventType == ConsoleUpdateScroll)
        {
            if (_scrollCount == 0) _firstScrollDelta = dy;
            if (_scrollCount < int.MaxValue) _scrollCount++;
            _scroll -= dy;
            _sawUpdate = true;
            return;
        }

        if (eventType != ConsoleUpdateRegion) return;
        _sawUpdate = true;
        if (_bottomRow < 0) return;

        var firstRow = (short)(dx >> 16);
        var lastRow = (short)(dy >> 16);
        if (firstRow > lastRow) (firstRow, lastRow) = (lastRow, firstRow);
        if (lastRow < _bottomRow)
        {
            _historyRegionUpdated = true;
            return;
        }
        if (firstRow > _bottomRow) return;
        var span = Math.Max(1, lastRow - firstRow + 1);
        _bottomRegionRows = (int)Math.Min(int.MaxValue, (long)_bottomRegionRows + span);
    }

    public void ClearEvents()
    {
        _scroll = 0;
        _scrollCount = 0;
        _firstScrollDelta = 0;
        _sawUpdate = false;
        _layoutChanged = false;
        _leadingLayout = false;
        _bottomRegionRows = 0;
        _historyRegionUpdated = false;
    }

    public void Dispose() => UnhookWinEvent(_hook);

    private delegate void WinEventProcedure(nint hook, uint eventType, nint window, int dx, int dy, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProcedure callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(IntPtr hook);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum,
        uint remove);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(ref NativeMessage message);
}

internal readonly record struct ConsoleEventBatch(long Scroll, int ScrollCount, int FirstScrollDelta,
    bool LayoutChanged, bool LeadingLayout, int BottomRegionRows, bool HistoryRedrawn, long EventVersion);
