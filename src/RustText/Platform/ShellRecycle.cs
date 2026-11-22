using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RustText.Demo.Platform;

[SupportedOSPlatform("windows")]
internal static class ShellRecycle
{
    private const uint NoConfirmation = 0x0010;
    private const uint ConfirmPermanentDeletion = 0x4000;
    private const uint RecycleOnDelete = 0x00080000;
    private const uint AddUndoRecord = 0x20000000;
    private static readonly Guid ClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid OperationId = new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
    private static readonly Guid ItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static Task<bool> MoveAsync(LockedFileInspection.Target target, IntPtr owner, CancellationToken token)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(Move(target, owner, token)); }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static unsafe bool Move(LockedFileInspection.Target target, IntPtr owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        target.EnsureUnchanged();
        Check(CoInitializeEx(IntPtr.Zero, 2));
        IntPtr operation = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            Check(CoCreateInstance(in ClassId, IntPtr.Zero, 1, in OperationId, out operation));
            Check(SHCreateItemFromParsingName(target.Path, IntPtr.Zero, in ItemId, out item));
            var vtable = *(IntPtr**)operation;
            // IFileOperation slot numbers, including the three IUnknown entries.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtable[5])(
                operation, NoConfirmation | ConfirmPermanentDeletion | RecycleOnDelete | AddUndoRecord));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[9])(operation, owner));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)vtable[18])(operation, item, IntPtr.Zero));
            token.ThrowIfCancellationRequested();
            target.EnsureUnchanged();
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[21])(operation));
            int aborted = 0;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtable[22])(operation, &aborted));
            return aborted == 0 && !File.Exists(target.Path) && !Directory.Exists(target.Path);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (operation != IntPtr.Zero) Marshal.Release(operation);
            CoUninitialize();
        }
    }

    private static void Check(int status)
    {
        if (status < 0)
            throw new IOException("Проводник не смог переместить объект в корзину.", status);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr result);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, in Guid iid, out IntPtr result);
}
