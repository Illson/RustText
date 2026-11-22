using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

public static class RustTextPerformanceWindows
{
    private delegate bool WindowCallback(IntPtr window, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    public static IntPtr VisibleWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process != (uint)pid || GetWindow(window, 4) != IntPtr.Zero || !IsWindowVisible(window)) return true;
            if (!GetWindowRect(window, out var rect) || rect.Right - rect.Left < 200 || rect.Bottom - rect.Top < 150) return true;
            if (Title(window).Length == 0) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string Title(IntPtr window)
    {
        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        return title.ToString();
    }

    public static bool Responds(IntPtr window) =>
        SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 2, 25, out _) != IntPtr.Zero;

    public static void Close(IntPtr window, int pid)
    {
        GetWindowThreadProcessId(window, out uint process);
        if (process == (uint)pid) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
}

public sealed class RustTextPerformanceOutput
{
    private const string Marker = "RUSTTEXT first_log_frame_ms=";
    private readonly object _gate = new object();
    private readonly StringBuilder _text = new StringBuilder();
    private readonly Task _reader;
    private double? _markerMs;

    public RustTextPerformanceOutput(StreamReader reader, Stopwatch clock)
    {
        _reader = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                lock (_gate)
                {
                    _text.AppendLine(line);
                    if (!_markerMs.HasValue && line.StartsWith(Marker, StringComparison.Ordinal) &&
                        long.TryParse(line.Substring(Marker.Length), out _))
                    {
                        _markerMs = clock.Elapsed.TotalMilliseconds;
                    }
                }
            }
        });
    }

    public double? MarkerMs { get { lock (_gate) return _markerMs; } }
    public string Text { get { lock (_gate) return _text.ToString(); } }
    public bool Finish(int timeoutMs) => _reader.Wait(timeoutMs);
}
