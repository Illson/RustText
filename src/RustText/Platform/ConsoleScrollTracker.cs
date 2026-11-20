using System.Runtime.InteropServices;

namespace RustText.Demo.Platform;

// Classic conhost reports the number of scrolled cells even when all the text is identical.
// Out-of-context callbacks run on this worker thread when it pumps its message queue.
internal sealed partial class ConsoleScrollTracker : IDisposable
{
    private const uint ConsoleUpdateScroll = 0x4004;
    private readonly nint _consoleWindow;
    private readonly WinEventProcedure _callback;
    private readonly nint _hook;
    private long _scroll;

    private ConsoleScrollTracker(nint window)
    {
        _consoleWindow = window;
        _callback = OnScroll;
        // The event source can differ from the process that owns the console HWND.
        _hook = SetWinEventHook(ConsoleUpdateScroll, ConsoleUpdateScroll, 0, _callback, 0, 0, 0);
    }

    public static ConsoleScrollTracker? TryCreate()
    {
        var window = GetConsoleWindow();
        if (window == 0) return null;
        var tracker = new ConsoleScrollTracker(window);
        return tracker._hook != 0 ? tracker : null;
    }

    public long ReadScroll()
    {
        while (PeekMessage(out var message, 0, 0, 0, 1))
            DispatchMessage(ref message);
        var result = _scroll;
        _scroll = 0;
        return result;
    }

    private void OnScroll(nint hook, uint eventType, nint window, int dx, int dy, uint thread, uint time)
    {
        if (window == _consoleWindow) _scroll -= dy;
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
