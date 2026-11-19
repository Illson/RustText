using System.Runtime.InteropServices;

namespace RustText.Demo.Platform;

public static class ExplorerReveal
{
    public static Task SelectFileAsync(string path)
    {
        ValidateCall(path);
        return RunOnSta(path, select: true);
    }

    // Uses the same validation, normalization, STA and PIDL parsing path as
    // SelectFileAsync, without asking the shell to open or activate a folder.
    internal static Task ParseItemAsync(string path)
    {
        ValidateCall(path);
        return RunOnSta(path, select: false);
    }

    internal static string NormalizeShellPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (TryStripPrefix(path, @"\\?\UNC\", out var uncPath))
        {
            if (uncPath.Length == 0)
            {
                throw new IOException("UNC path does not contain a server name.");
            }
            return @"\\" + uncPath;
        }

        if (TryStripPrefix(path, @"\\?\", out var localPath))
        {
            if (IsDrivePath(localPath))
            {
                return localPath;
            }
            throw new IOException("Windows Shell does not support this extended path type.");
        }

        if (path.StartsWith(@"\\.", StringComparison.Ordinal))
        {
            throw new IOException("Windows Shell does not support device paths.");
        }

        return path;
    }

    private static string PreparePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var shellPath = NormalizeShellPath(fullPath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The item to reveal does not exist.", fullPath);
        }
        return shellPath;
    }

    private static void ValidateCall(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Explorer reveal is available on Windows.");
        }
    }

    private static Task RunOnSta(string path, bool select)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var comInitialized = false;
            IntPtr itemIdList = IntPtr.Zero;
            Exception? failure = null;
            try
            {
                // Canonicalization and existence checks can block on network paths.
                var shellPath = PreparePath(path);
                var result = CoInitializeEx(IntPtr.Zero, CoInitializeApartmentThreaded);
                if (result < 0)
                {
                    throw ShellFailure("initialize COM", result);
                }
                comInitialized = true;

                result = SHParseDisplayName(shellPath, IntPtr.Zero, out itemIdList, 0, out _);
                if (result < 0)
                {
                    throw ShellFailure("parse the path", result);
                }
                if (itemIdList == IntPtr.Zero)
                {
                    throw new IOException("Windows Shell returned an empty item identifier.");
                }

                if (select)
                {
                    result = SHOpenFolderAndSelectItems(itemIdList, 0, IntPtr.Zero, 0);
                    if (result < 0)
                    {
                        throw ShellFailure("select the item", result);
                    }
                }

            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                if (itemIdList != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(itemIdList);
                }
                if (comInitialized)
                {
                    CoUninitialize();
                }
            }

            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }
        })
        {
            IsBackground = true,
            Name = "rusttext-explorer-select"
        };

        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException)
        {
            completion.TrySetException(new IOException("Could not start the Explorer STA worker thread.", error));
        }
        return completion.Task;
    }

    private static IOException ShellFailure(string action, int hresult)
    {
        var cause = Marshal.GetExceptionForHR(hresult);
        return new IOException($"Windows Shell could not {action}: {cause?.Message ?? $"HRESULT 0x{hresult:X8}"}", cause);
    }

    private static bool TryStripPrefix(string value, string prefix, out string suffix)
    {
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            suffix = value[prefix.Length..];
            return true;
        }
        suffix = string.Empty;
        return false;
    }

    private static bool IsDrivePath(string path) => path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] == '\\';

    private const uint CoInitializeApartmentThreaded = 0x2;

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr itemIdList,
        uint childCount,
        IntPtr childItemIdLists,
        uint flags);
}
