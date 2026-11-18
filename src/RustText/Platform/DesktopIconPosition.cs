using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RustText.Demo.Platform;

internal sealed record DesktopIconPosition(int X, int Y)
{
    private const uint PositionWithoutFocus = 0x40000080;
    private const uint AutoArrange = 1;
    private const int FilesystemPath = unchecked((int)0x80058000);
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid ShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    private static readonly Guid ServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid FolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");
    private static readonly Guid ShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid EnumIdList = new("000214F2-0000-0000-C000-000000000046");
    private static readonly Guid ShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static int _worker;

    public static Task<DesktopIconPosition?> CaptureAsync(string path, CancellationToken token) =>
        UseDesktopAsync(path, null, null, TimeSpan.FromMilliseconds(250), token);

    public async Task RestoreAsync(string path, FileStamp stamp)
    {
        await UseDesktopAsync(path, this, stamp, TimeSpan.FromSeconds(3), CancellationToken.None);
    }

    private static async Task<DesktopIconPosition?> UseDesktopAsync(
        string path, DesktopIconPosition? restore, FileStamp? stamp, TimeSpan limit, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !IsDesktopCandidate(path) || Interlocked.CompareExchange(ref _worker, 1, 0) != 0)
        {
            return null;
        }

        var completion = new TaskCompletionSource<DesktopIconPosition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expires = Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency);
        var worker = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(Run(path, restore, stamp, expires));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            {
                completion.TrySetResult(null);
            }
            finally
            {
                Volatile.Write(ref _worker, 0);
            }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        try
        {
            return await completion.Task.WaitAsync(limit, token);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static bool IsDesktopCandidate(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return false;
        }

        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var desktop = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (desktop.Length > 0 && SamePath(desktop, parent))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    // Slots are the C ABI declarations in ExDisp.h and ShObjIdl_core.h. They
    // avoid requiring runtime COM marshalling in the Native AOT executable.
    private static unsafe DesktopIconPosition? Run(string path, DesktopIconPosition? restore, FileStamp? stamp, long expires)
    {
        if (CoInitializeEx(0, 2) < 0)
        {
            return null;
        }

        try
        {
            using var view = OpenDesktopView();
            if (view.Value == 0)
            {
                return null;
            }

            do
            {
                uint flags = 0;
                var getFlags = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(view.Value, 25);
                if (getFlags(view.Value, &flags) < 0 || (flags & AutoArrange) != 0)
                {
                    return null;
                }

                using var item = FindItem(view.Value, path, expires, out var data);
                if (item.Value != 0 && (stamp is null
                    || data.Length == stamp.Length && data.Write.Ticks == stamp.LastWriteUtc.ToFileTimeUtc()
                    && SavedFileStillCurrent(path, stamp)))
                {
                    Point point = default;
                    var getPosition = (delegate* unmanaged[Stdcall]<nint, nint, Point*, int>)Slot(view.Value, 11);
                    if (getPosition(view.Value, item.Value, &point) < 0 || Stopwatch.GetTimestamp() >= expires)
                    {
                        return null;
                    }

                    if (restore is null)
                    {
                        return new DesktopIconPosition(point.X, point.Y);
                    }

                    if (point.X != restore.X || point.Y != restore.Y)
                    {
                        uint selection = 0;
                        var getSelection = (delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(view.Value, 33);
                        if (getSelection(view.Value, item.Value, &selection) >= 0
                            && SavedFileStillCurrent(path, stamp!) && Stopwatch.GetTimestamp() < expires)
                        {
                            var id = item.Value;
                            var desired = new Point { X = restore.X, Y = restore.Y };
                            var position = (delegate* unmanaged[Stdcall]<nint, uint, nint*, Point*, uint, int>)Slot(view.Value, 16);
                            _ = position(view.Value, 1, &id, &desired, PositionWithoutFocus | (selection & 1));
                        }
                    }

                    return restore;
                }

                if (restore is null || !SavedFileStillCurrent(path, stamp!))
                {
                    return null;
                }

                Thread.Sleep(100);
            }
            while (Stopwatch.GetTimestamp() < expires);

            return null;
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static bool SavedFileStillCurrent(string path, FileStamp stamp)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length == stamp.Length && file.LastWriteTimeUtc == stamp.LastWriteUtc;
    }

    private static unsafe ComReference OpenDesktopView()
    {
        var clsid = ShellWindowsClass;
        var iid = ShellWindows;
        nint windowsValue = 0;
        if (CoCreateInstance(&clsid, 0, 4, &iid, &windowsValue) < 0 || windowsValue == 0)
        {
            return default;
        }

        using var windows = new ComReference(windowsValue);
        Variant desktop = new() { Type = 3 };
        Variant root = default;
        nint dispatchValue = 0;
        int hwnd = 0;
        var find = (delegate* unmanaged[Stdcall]<nint, Variant*, Variant*, int, int*, int, nint*, int>)Slot(windows.Value, 15);
        if (find(windows.Value, &desktop, &root, 8, &hwnd, 1, &dispatchValue) < 0 || dispatchValue == 0)
        {
            return default;
        }

        using var dispatch = new ComReference(dispatchValue);
        using var provider = Query(dispatchValue, ServiceProvider);
        if (provider.Value == 0)
        {
            return default;
        }

        var service = TopLevelBrowser;
        var browserIid = ShellBrowser;
        nint browserValue = 0;
        var query = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Slot(provider.Value, 3);
        if (query(provider.Value, &service, &browserIid, &browserValue) < 0 || browserValue == 0)
        {
            return default;
        }

        using var browser = new ComReference(browserValue);
        nint view = 0;
        var active = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(browser.Value, 15);
        if (active(browser.Value, &view) < 0 || view == 0)
        {
            return default;
        }

        using var shellView = new ComReference(view);
        return Query(view, FolderView2);
    }

    private static unsafe Pidl FindItem(nint view, string path, long expires, out FindData result)
    {
        result = default;
        nint folderValue = 0;
        nint itemsValue = 0;
        var iidFolder = ShellFolder;
        var iidList = EnumIdList;
        var getFolder = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(view, 5);
        if (getFolder(view, &iidFolder, &folderValue) < 0 || folderValue == 0)
        {
            return default;
        }

        using var folder = new ComReference(folderValue);
        var getItems = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(view, 8);
        if (getItems(view, 2, &iidList, &itemsValue) < 0 || itemsValue == 0)
        {
            return default;
        }

        using var items = new ComReference(itemsValue);
        var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(itemsValue, 3);
        while (Stopwatch.GetTimestamp() < expires)
        {
            nint id = 0;
            if (next(itemsValue, 1, &id, null) != 0 || id == 0)
            {
                break;
            }

            try
            {
                FindData data = default;
                if (SHGetDataFromIDListW(folderValue, id, 1, &data, sizeof(FindData)) < 0
                    || !string.Equals(new string(data.Name), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                    || !HasFilePath(folderValue, id, path))
                {
                    continue;
                }

                result = data;
                var found = new Pidl(id);
                id = 0;
                return found;
            }
            finally
            {
                Marshal.FreeCoTaskMem(id);
            }
        }

        return default;
    }

    private static unsafe bool HasFilePath(nint folder, nint id, string path)
    {
        var iid = ShellItem;
        nint value = 0;
        if (SHCreateItemWithParent(0, folder, id, &iid, &value) < 0 || value == 0)
        {
            return false;
        }

        using var item = new ComReference(value);
        nint name = 0;
        var getName = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(item.Value, 5);
        try
        {
            return getName(item.Value, FilesystemPath, &name) >= 0
                && Marshal.PtrToStringUni(name) is { } candidate && SamePath(candidate, path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    private static unsafe ComReference Query(nint value, Guid iid)
    {
        nint result = 0;
        var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(value, 0);
        return query(value, &iid, &result) >= 0 ? new ComReference(result) : default;
    }

    private static unsafe nint Slot(nint value, int index) => (*(nint**)value)[index];

    private readonly record struct ComReference(nint Value) : IDisposable
    {
        public unsafe void Dispose()
        {
            if (Value != 0)
            {
                var release = (delegate* unmanaged[Stdcall]<nint, uint>)Slot(Value, 2);
                _ = release(Value);
            }
        }
    }

    private readonly record struct Pidl(nint Value) : IDisposable
    {
        public void Dispose() => Marshal.FreeCoTaskMem(Value);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant { [FieldOffset(0)] public ushort Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly long Ticks => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct FindData
    {
        public uint Attributes;
        public FileTime Creation, Access, Write;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        public fixed char Name[260];
        public fixed char AlternateName[14];
        public readonly long Length => ((long)SizeHigh << 32) | SizeLow;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern unsafe int CoCreateInstance(Guid* clsid, nint outer, uint flags, Guid* iid, nint* result);
    [DllImport("shell32.dll")]
    private static extern unsafe int SHGetDataFromIDListW(nint folder, nint id, int format, void* data, int size);
    [DllImport("shell32.dll")]
    private static extern unsafe int SHCreateItemWithParent(nint parent, nint folder, nint id, Guid* iid, nint* item);
}
