using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RustText.Demo.Platform;

internal sealed partial class ConsoleScreenBuffer : IDisposable
{
    private const int TailRows = 64;
    private const uint GenericRead = 0x80000000;
    private const ushort TrailingCharacterCell = 0x0200;
    private readonly SafeFileHandle _output;
    private readonly ConsoleScrollTracker? _scrollTracker;
    private BufferInfo? _previous;
    private ConsoleScreenRow[] _tail = [];
    private long _scrollAlreadyCaptured;

    public ConsoleScreenBuffer(int processId)
    {
        FreeConsole();
        if (!AttachConsole((uint)processId))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось подключиться к консоли процесса.");
        try
        {
            // AttachConsole resets the helper's control handlers.
            if (!SetConsoleCtrlHandler(0, true))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось настроить обработку Ctrl+C.");
            _output = OpenOutput("CONOUT$", GenericRead, FileShare.ReadWrite, 0, FileMode.Open, 0, 0);
            if (_output.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось открыть буфер консоли.");
            _scrollTracker = ConsoleScrollTracker.TryCreate();
        }
        catch
        {
            _output?.Dispose();
            FreeConsole();
            throw;
        }
    }

    public ConsoleScreenRow[] ReadNewRows(CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var before = GetBufferInfo();
            var end = (int)before.Cursor.Y;
            var previous = _previous.GetValueOrDefault();
            var continuous = _previous.HasValue && previous.Size == before.Size && end >= previous.Cursor.Y;
            var advance = continuous ? end - previous.Cursor.Y : 0;

            var atBottom = end == before.Size.Y - 1;
            var viewportAdvance = before.Window.Top - previous.Window.Top;
            var scrollBatch = _scrollTracker?.ReadEvents(before.Size.Y - 1) ?? default;
            var reported = GetReportedScroll(viewportAdvance, scrollBatch,
                before.Cursor.X <= previous.Cursor.X,
                previous.Window.Bottom < previous.Cursor.Y);
            var inferred = atBottom ? Math.Max(0L, reported - _scrollAlreadyCaptured) : 0;
            var expected = (int)Math.Min(end, advance + inferred);
            var first = continuous ? Math.Max(0, end - expected - TailRows) : 0;

            // At the bottom the cursor can stay still while a multi-chunk read is scrolling.
            var lastRow = atBottom && end > 0 ? ReadRows(before.Size.X, end - 1, end, token) : null;
            var rows = ReadRows(before.Size.X, first, end, token);
            if (rows is null) continue;
            // Before the last buffer row, normal output advances the cursor. A rewrite of
            // an earlier row does not change the boundary, even when it changes the saved tail.
            var boundary = continuous && (!atBottom || scrollBatch.HistoryRedrawn)
                ? rows.Length - advance
                : FindBoundary(rows, first, continuous ? expected : null);
            if (boundary < 0)
            {
                // The writer got ahead of the notifications. The old tail can still be
                // higher in the buffer than the predicted read window.
                rows = ReadRows(before.Size.X, 0, end, token);
                if (rows is null) continue;
                boundary = FindBoundary(rows, 0, continuous ? expected : null);
            }

            var after = GetBufferInfo();
            var eventsAfter = _scrollTracker?.ReadEvents(after.Size.Y - 1) ?? default;
            var moreEvents = eventsAfter.EventVersion != scrollBatch.EventVersion;
            if (moreEvents || before.Size != after.Size || before.Cursor != after.Cursor
                || before.Window.Top != after.Window.Top || (lastRow is { Length: > 0 } && lastRow[0] != rows[^1]))
                continue;

            var added = rows[boundary..];
            // Content can prove that rows scrolled before the corresponding events arrive.
            // Credit those rows once so that late notifications do not replay them.
            _scrollAlreadyCaptured = continuous && atBottom && _scrollTracker is not null
                ? Math.Max(0L, _scrollAlreadyCaptured + added.Length - advance - reported) : 0;
            _scrollTracker?.ClearEvents();
            _previous = before;
            _tail = rows[Math.Max(0, rows.Length - TailRows)..];
            return added;
        }
        return [];
    }

    private static long GetReportedScroll(int viewportAdvance, ConsoleEventBatch events,
        bool cursorReturnedToColumnStart, bool previousViewportExcludedCursor)
    {
        if (events.ScrollCount == 0) return 0;

        var scroll = events.Scroll;
        if (viewportAdvance == 0 && events.LeadingLayout)
        {
            if (events.ScrollCount > 1 || events.BottomRegionRows == 0 || !cursorReturnedToColumnStart)
                scroll += events.FirstScrollDelta;
            else
                scroll = Math.Min(scroll, events.BottomRegionRows);
        }

        var reported = Math.Max(0L, scroll - Math.Max(0, viewportAdvance));
        // A viewport can leave the cursor and return several times between samples.
        // Classic conhost also reports the rows it actually wrote at the buffer edge.
        if ((events.LayoutChanged || previousViewportExcludedCursor) && events.BottomRegionRows > 0)
            reported = Math.Min(reported, events.BottomRegionRows);
        return reported;
    }

    private int FindBoundary(ReadOnlySpan<ConsoleScreenRow> rows, int first, int? expected)
    {
        if (_tail.Length == 0) return 0;
        if (expected is { } count && count <= rows.Length)
        {
            var boundary = rows.Length - count;
            var overlap = Math.Min(_tail.Length, boundary);
            if ((overlap == _tail.Length || first == 0)
                && _tail.AsSpan(_tail.Length - overlap).SequenceEqual(rows.Slice(boundary - overlap, overlap)))
                return boundary;
        }

        var index = rows.LastIndexOf(_tail.AsSpan());
        if (index >= 0)
        {
            // Repeated history can still contain the old tail after an in-place edit.
            // Infer an unreported scroll only when the text identifies a single boundary.
            if (expected == 0 && index > 0 && rows[..(index + _tail.Length - 1)].IndexOf(_tail.AsSpan()) >= 0)
                return rows.Length;
            return index + _tail.Length;
        }
        if (first != 0) return -1;

        // An in-place edit can remove the old tail entirely. Once the full buffer
        // has been checked, keep the boundary supplied by the cursor and scroll events.
        if (_scrollTracker is not null && expected is { } reported) return rows.Length - reported;

        // A burst can evict part or all of the saved tail from the screen buffer.
        for (var overlap = Math.Min(_tail.Length - 1, rows.Length); overlap > 0; overlap--)
            if (_tail.AsSpan(_tail.Length - overlap).SequenceEqual(rows[..overlap])) return overlap;
        return 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    private BufferInfo GetBufferInfo()
    {
        if (!GetConsoleScreenBufferInfo(_output, out var info))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось прочитать буфер консоли.");
        return info;
    }

    private unsafe ConsoleScreenRow[]? ReadRows(short width, int first, int end, CancellationToken token)
    {
        var rows = new ConsoleScreenRow[end - first];
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
                    var fullLength = length;
                    while (length > 0 && text[length - 1] == ' ') length--;
                    // shortcut: the last blank may be a printed space; use native wrap metadata if it becomes available.
                    var mayWrap = length > 0 && fullLength - length <= 1;
                    rows[start - first + row] = new ConsoleScreenRow(
                        new string(text, 0, mayWrap ? fullLength : length), mayWrap);
                }
            }
        }
        return rows;
    }

    public void Dispose()
    {
        _scrollTracker?.Dispose();
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

internal readonly record struct ConsoleScreenRow(string Text, bool MayWrap);
