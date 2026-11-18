using System.Runtime.InteropServices;

namespace RustText.Demo.Platform;

public static class ExplorerReveal
{
    public static void SelectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Explorer reveal is available on Windows.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The item to reveal does not exist.", fullPath);
        }

        var result = SHParseDisplayName(fullPath, IntPtr.Zero, out var itemIdList, 0, out _);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(itemIdList, 0, IntPtr.Zero, 0));
        }
        finally
        {
            Marshal.FreeCoTaskMem(itemIdList);
        }
    }

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
