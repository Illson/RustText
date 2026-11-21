using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RustText.Demo.Platform;

// Only the helper owns a console. CMD reads normal Windows Unicode keyboard input from it;
// stdout/stderr are still byte streams, separate from the command text.
internal static partial class CmdInputHost
{
    internal const string Argument = "--host-cmd-input";

    public static int Run(uint initialCodePage)
    {
        if (!OperatingSystem.IsWindows() || !Console.IsInputRedirected || !Console.IsOutputRedirected) return 1;
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        using var output = Console.OpenStandardOutput();
        using var errors = Console.OpenStandardError();
        FreeConsole();
        if (!AllocConsole()) return 1;
        try
        {
            if (!SetConsoleOutputCP(initialCodePage) || !SetConsoleCP(initialCodePage)) throw new Win32Exception();
            using var keyboard = OpenInput("CONIN$", 0xC0000000, FileShare.ReadWrite, 0, FileMode.Open, 0, 0);
            if (keyboard.IsInvalid) throw new Win32Exception();

            // Process.Start duplicates our standard input for the child when redirecting stdout.
            if (!SetStdHandle(-10, keyboard)) throw new Win32Exception();
            var info = new ProcessStartInfo(ShellTab.GetExecutablePath(ShellKind.Cmd), "/D /Q")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var cmd = Process.Start(info) ?? throw new IOException("Не удалось запустить CMD.");
            try
            {
                var exit = cmd.WaitForExitAsync();
                var pump = Task.Run(() => ForwardInput(input, keyboard));
                var stdout = cmd.StandardOutput.BaseStream.CopyToAsync(output);
                var stderr = cmd.StandardError.BaseStream.CopyToAsync(errors);
                // EOF means the editor closed its input (including an unexpected editor exit).
                if (Task.WhenAny(exit, pump).GetAwaiter().GetResult() == pump)
                {
                    pump.GetAwaiter().GetResult();
                    if (!cmd.HasExited) cmd.Kill(entireProcessTree: true);
                }
                Task.WhenAll(exit, stdout, stderr).GetAwaiter().GetResult();
                return cmd.ExitCode;
            }
            finally
            {
                if (!cmd.HasExited) cmd.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException)
        {
            using var writer = new StreamWriter(errors, new UTF8Encoding(false), leaveOpen: true);
            writer.WriteLine($"CMD: {error.Message}");
            return 1;
        }
        finally { FreeConsole(); }
    }

    private static unsafe void ForwardInput(StreamReader input, SafeFileHandle keyboard)
    {
        var records = new InputRecord[256];
        while (input.ReadLine() is { } line)
        {
            for (var offset = 0; offset <= line.Length;)
            {
                var count = Math.Min(records.Length, line.Length - offset + 1);
                for (var i = 0; i < count; i++, offset++)
                {
                    var enter = offset == line.Length;
                    records[i] = new InputRecord
                    {
                        Type = 1, Down = 1, Repeat = 1, VirtualKey = enter ? (ushort)13 : (ushort)0,
                        UnicodeChar = enter ? (ushort)'\r' : line[offset]
                    };
                }
                fixed (InputRecord* first = records)
                {
                    for (var written = 0; written < count;)
                    {
                        if (!WriteConsoleInput(keyboard, first + written, count - written, out var n) || n == 0)
                            throw new Win32Exception();
                        written += n;
                    }
                }
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(14)] public ushort UnicodeChar;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleOutputCP(uint codePage);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCP(uint codePage);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenInput(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int kind, SafeFileHandle handle);
    [LibraryImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteConsoleInput(SafeFileHandle handle, InputRecord* records, int length, out int written);
}
