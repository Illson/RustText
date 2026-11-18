using System.Runtime.InteropServices;
using System.Text;

namespace RustText.Demo.Platform;

public sealed record WindowIdentity(nint Handle, uint ProcessId, string Title);

public static class WindowFinder
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const int SwRestore = 9;
    private const uint DwmwaCloaked = 14;

    public static IReadOnlyList<WindowIdentity> EnumerateVisibleWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Window enumeration is available on Windows.");
        }

        var handles = new List<nint>();
        EnumWindows((window, _) =>
        {
            handles.Add(window);
            return true;
        }, 0);

        var shellWindow = GetShellWindow();
        var results = new List<WindowIdentity>();
        foreach (var handle in handles)
        {
            if (handle == shellWindow || !IsWindowVisible(handle) ||
                (GetWindowLongPtrW(handle, GwlExStyle).ToInt64() & WsExNoActivate) != 0 || IsCloaked(handle))
            {
                continue;
            }

            var titleLength = GetWindowTextLengthW(handle);
            var title = new StringBuilder(titleLength + 1);
            var copied = GetWindowTextW(handle, title, title.Capacity);
            uint processId = 0;
            if (GetWindowThreadProcessId(handle, out processId) == 0)
            {
                continue;
            }

            results.Add(new WindowIdentity(handle, processId, title.ToString(0, Math.Max(0, copied))));
        }

        return results
            .OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.ProcessId)
            .ToArray();
    }

    /// <summary>Restores and activates the selected window. Call only from the explicit user action.</summary>
    public static bool ActivateWindow(WindowIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Window activation is available on Windows.");
        }

        if (!IsWindow(identity.Handle) ||
            GetWindowThreadProcessId(identity.Handle, out var currentProcessId) == 0 ||
            currentProcessId != identity.ProcessId)
        {
            return false;
        }

        if (IsIconic(identity.Handle))
        {
            ShowWindowAsync(identity.Handle, SwRestore);
        }

        return SetForegroundWindow(identity.Handle) || GetForegroundWindow() == identity.Handle;
    }

    private static bool IsCloaked(nint window)
    {
        var result = DwmGetWindowAttribute(window, DwmwaCloaked, out uint cloaked, sizeof(uint));
        return result >= 0 && cloaked != 0;
    }

    private delegate bool EnumWindowsCallback(nint window, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint data);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, int size);
}
