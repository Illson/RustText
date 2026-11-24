using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RustText.Demo.Platform;

internal static class LocalProcesses
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Terminate = 0x0001;
    private const uint TokenQuery = 0x0008;

    internal sealed record Owner(int Id, string Name, long Started, bool CanClose)
    {
        public string Label => $"{Name} (PID {Id})";
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<Owner> Describe(IReadOnlyList<LockedFileInspection.BlockedItem> blocked)
    {
        // Windows reports all users of a file, not which handle denied delete sharing.
        // A process is eligible only if it is the sole possible owner for every item it uses.
        var ambiguous = blocked.Where(item => item.ProcessIds.Count != 1).SelectMany(item => item.ProcessIds).ToHashSet();
        return Describe(blocked.SelectMany(item => item.ProcessIds))
            .Select(owner => ambiguous.Contains(owner.Id) ? owner with { CanClose = false } : owner).ToArray();
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<Owner> Describe(IEnumerable<int> ids)
    {
        using var caller = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        return ids.Distinct().Order().Select(id =>
        {
            using var handle = OpenProcess(QueryLimitedInformation, false, id);
            var name = "Неизвестный процесс";
            try
            {
                using var process = Process.GetProcessById(id);
                name = process.ProcessName;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }

            var start = ReadStart(handle);
            var eligible = start != 0 && name != "Неизвестный процесс"
                && CanAct(handle, id, caller.User, current.SessionId, name);
            return new Owner(id, name, start, eligible);
        }).ToArray();
    }

    [SupportedOSPlatform("windows")]
    public static void ForceClose(IEnumerable<Owner> candidates, CancellationToken token)
    {
        foreach (var owner in candidates) TryForceClose(owner, token);
    }

    [SupportedOSPlatform("windows")]
    public static bool TryForceClose(Owner expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!expected.CanClose) return false;
        using var caller = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        using var target = OpenProcess(QueryLimitedInformation | Terminate, false, expected.Id);
        if (!Matches(expected, target, caller.User, current.SessionId)) return false;
        token.ThrowIfCancellationRequested();
        // Use the validated handle; a recycled PID cannot redirect termination.
        return TerminateProcess(target, 1);
    }

    [SupportedOSPlatform("windows")]
    public static void RequestClose(IEnumerable<Owner> candidates, CancellationToken token)
    {
        using var caller = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        foreach (var expected in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!expected.CanClose) continue;
            using var target = OpenProcess(QueryLimitedInformation, false, expected.Id);
            if (!Matches(expected, target, caller.User, current.SessionId)) continue;
            try
            {
                using var process = Process.GetProcessById(expected.Id);
                if (!process.HasExited && process.StartTime.ToFileTimeUtc() == expected.Started)
                    process.CloseMainWindow();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool Matches(Owner expected, SafeProcessHandle process, SecurityIdentifier? user, int session) =>
        expected.Started != 0 && ReadStart(process) == expected.Started
        && CanAct(process, expected.Id, user, session, expected.Name);

    [SupportedOSPlatform("windows")]
    private static bool CanAct(SafeProcessHandle process, int id, SecurityIdentifier? currentUser, int session, string name)
    {
        if (process.IsInvalid || id == Environment.ProcessId || id <= 4
            || name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dwm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("conhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
            || GetWindowThreadProcessId(GetShellWindow(), out var shellId) != 0 && shellId == id
            || !ProcessIdToSessionId(id, out var ownerSession) || ownerSession != session
            || !IsProcessCritical(process, out var critical) || critical
            || currentUser is null || !OpenProcessToken(process, TokenQuery, out var token))
            return false;
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return owner.User == currentUser;
        }
    }

    private static long ReadStart(SafeProcessHandle process) =>
        !process.IsInvalid && GetProcessTimes(process, out var start, out _, out _, out _) ? start : 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long started, out long ended, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int processId, out int session);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);
}
