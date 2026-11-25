using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RustText.Demo.Platform;

internal static class LockedFileInspection
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePointAndDirectories = 0x02200000;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int FileProcessIdsUsingFileInformation = 47;
    private const int BufferOverflow = unchecked((int)0x80000005);
    private const int BufferTooSmall = unchecked((int)0xC0000023);
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Identity(ulong Volume, ulong IdLow, ulong IdHigh);

    internal sealed record Target(string Path, bool Directory, Identity FileId)
    {
        public void EnsureUnchanged()
        {
            if (Select(Path) != this)
                throw new IOException("Файл или папка были заменены. Выберите объект заново.");
        }
    }

    internal sealed record BlockedItem(string Path, IReadOnlyList<int> ProcessIds);

    public static Target Select(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || path[1] != ':' || path.AsSpan(2).Contains(':'))
            throw new IOException("Операция доступна только для обычных путей на локальном диске Windows.");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(path)!;
        if (path.Length == root.Length || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("Выберите файл или папку на локальном фиксированном диске, но не корень диска.");
        for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if (ancestor.EndsWith('.') || ancestor.EndsWith(' '))
                throw new IOException("Имена с завершающей точкой или пробелом не поддерживаются.");
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Удаление через ссылки и точки подключения не поддерживается.");
        }
        var attributes = File.GetAttributes(path);
        using var handle = Open(path, 0);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var identity, 24))
            throw NativeError(path, Marshal.GetLastPInvokeError());
        return new Target(path, (attributes & FileAttributes.Directory) != 0, identity);
    }

    internal static bool ContainsPath(string ancestor, string path) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(ancestor) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<BlockedItem> Inspect(Target target, CancellationToken token)
    {
        target.EnsureUnchanged();
        var blocked = new List<BlockedItem>();
        var folders = new Stack<string>();
        InspectPath(target.Path);
        if (target.Directory) folders.Push(target.Path);
        while (folders.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            foreach (var item in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Папка содержит ссылку или точку подключения; удаление не начато: {item}");
                InspectPath(item);
                if ((attributes & FileAttributes.Directory) != 0) folders.Push(item);
            }
        }
        target.EnsureUnchanged();
        return blocked;

        void InspectPath(string path)
        {
            token.ThrowIfCancellationRequested();
            using var probe = Open(path, DeleteAccess);
            if (!probe.IsInvalid) return;
            var error = Marshal.GetLastPInvokeError();
            if (error is not (SharingViolation or LockViolation))
                throw NativeError(path, error);
            var owners = FindProcessIds(path);
            // Keep our own PID in the evidence: otherwise a second reader can look like the sole blocker.
            // LocalProcesses excludes RustText itself from the processes that may be closed.
            using var recheck = Open(path, DeleteAccess);
            if (!recheck.IsInvalid) return;
            var remainingError = Marshal.GetLastPInvokeError();
            if (remainingError is not (SharingViolation or LockViolation)) throw NativeError(path, remainingError);
            blocked.Add(new BlockedItem(path, owners));
        }
    }

    internal static unsafe IReadOnlyList<int> FindProcessIds(string path)
    {
        using var handle = Open(path, 0);
        if (handle.IsInvalid) return [];
        for (var size = 512; size <= 64 * 1024; size *= 2)
        {
            var bytes = new byte[size];
            fixed (byte* buffer = bytes)
            {
                var status = NtQueryInformationFile(handle, out _, buffer, (uint)size, FileProcessIdsUsingFileInformation);
                if (status is BufferOverflow or BufferTooSmall or InfoLengthMismatch) continue;
                if (status < 0) return [];
                var count = *(uint*)buffer;
                if (count > (size - IntPtr.Size) / IntPtr.Size) return [];
                var ids = (nuint*)(buffer + IntPtr.Size);
                var result = new HashSet<int>();
                for (var index = 0; index < count; index++)
                    if (ids[index] is > 0 and <= int.MaxValue)
                        result.Add((int)ids[index]);
                return result.Order().ToArray();
            }
        }
        return [];
    }

    private static IOException NativeError(string path, int error) =>
        new($"Не удалось проверить «{path}»: {new Win32Exception(error).Message}", unchecked((int)0x80070000) | error);

    private static SafeFileHandle Open(string path, uint access) =>
        CreateFileW(path, access, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePointAndDirectories, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out Identity identity, uint size);

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtQueryInformationFile(SafeFileHandle handle, out IoStatus status, byte* buffer,
        uint size, int informationClass);
}
