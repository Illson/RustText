using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RustText.Demo.Platform;

internal sealed partial class ConsoleScreenBuffer : IDisposable
{
    private const int OverlapRows = 256;
    private const uint GenericRead = 0x80000000;
    private const ushort TrailingCharacterCell = 0x0200;
    private readonly SafeFileHandle _output;
    private string[] _tail = [];
    private int _lastCursorRow;
    private int _lastWidth;

    public ConsoleScreenBuffer(int processId)
    {
        FreeConsole();
        if (!AttachConsole((uint)processId))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось подключиться к консоли процесса.");
        _output = OpenOutput("CONOUT$", GenericRead, FileShare.ReadWrite, 0, FileMode.Open, 0, 0);
        if (_output.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            _output.Dispose();
            FreeConsole();
            throw new Win32Exception(error, "Не удалось открыть буфер консоли.");
        }
    }

    public string[] ReadNewRows(CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var before = GetBufferInfo();
            var end = before.Cursor.Y;
            var width = before.Size.X;
            var first = _lastWidth == 0 ? 0
                : Math.Max(0, Math.Min(end, _lastCursorRow) - OverlapRows);
            var rows = ReadRows(width, first, end, token);
            var after = GetBufferInfo();
            if (rows is null || before.Size != after.Size || before.Cursor != after.Cursor) continue;

            var count = _lastWidth == width && end >= _lastCursorRow
                && end < before.Size.Y - 1
                ? _lastCursorRow - first
                : SharedRows(_tail, rows);
            _tail = rows.AsSpan(Math.Max(0, rows.Length - OverlapRows)).ToArray();
            _lastCursorRow = end;
            _lastWidth = width;
            return rows.AsSpan(count).ToArray();
        }
        return [];
    }

    // A full screen buffer scrolls without advancing the cursor row. Keep
    // the longest overlap so existing rows aren't appended a second time.
    internal static int SharedRows(ReadOnlySpan<string> previous, ReadOnlySpan<string> current)
    {
        for (var count = Math.Min(previous.Length, current.Length); count > 0; count--)
        {
            if (previous[^count..].SequenceEqual(current[..count])) return count;
        }
        return 0;
    }

    private BufferInfo GetBufferInfo()
    {
        if (!GetConsoleScreenBufferInfo(_output, out var info))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось прочитать буфер консоли.");
        return info;
    }

    private unsafe string[]? ReadRows(short width, int first, int end, CancellationToken token)
    {
        var rows = new string[end - first];
        if (rows.Length == 0) return rows;
        var chunkRows = Math.Min(rows.Length, Math.Max(1, 8192 / width));
        var cells = new Cell[width * chunkRows];
        var text = new char[width];
        fixed (Cell* buffer = cells)
        {
            for (var start = first; start < end; start += chunkRows)
            {
                token.ThrowIfCancellationRequested();
                var height = (short)Math.Min(chunkRows, end - start);
                var area = new Rect(0, (short)start, (short)(width - 1), (short)(start + height - 1));
                if (!ReadConsoleOutput(_output, buffer, new Coord(width, height), default, ref area))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось прочитать вывод консоли.");
                if (area != new Rect(0, (short)start, (short)(width - 1), (short)(start + height - 1)))
                    return null;

                for (var row = 0; row < height; row++)
                {
                    var length = 0;
                    for (var column = 0; column < width; column++)
                    {
                        var cell = cells[row * width + column];
                        if ((cell.Attributes & TrailingCharacterCell) != 0) continue;
                        text[length++] = cell.Character is 0 or '\r' or '\n' ? ' ' : (char)cell.Character;
                    }
                    while (length > 0 && text[length - 1] == ' ') length--;
                    rows[start - first + row] = new string(text, 0, length);
                }
            }
        }
        return rows;
    }

    public void Dispose()
    {
        _output.Dispose();
        FreeConsole();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(short Left, short Top, short Right, short Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferInfo
    {
        public Coord Size;
        public Coord Cursor;
        public ushort Attributes;
        public Rect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Cell
    {
        public ushort Character;
        public ushort Attributes;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenOutput(string name, uint access, FileShare share, nint security,
        FileMode creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleScreenBufferInfo(SafeFileHandle output, out BufferInfo info);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleOutputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadConsoleOutput(SafeFileHandle output, Cell* cells, Coord size,
        Coord position, ref Rect area);
}
