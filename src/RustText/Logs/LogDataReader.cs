using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RustText.Demo;

namespace RustText.Demo.Logs;

internal sealed partial class LogDataReader
{
    internal const int MinimapColumns = 32;
    private const int LookaheadBytes = 4;
    private const int MidpointScanBytes = 64 * 1024;
    internal const int MaximumPageBytes = 1024 * 1024;
    private const int MaximumFrameBytes = 64 * 1024;
    internal const int DefaultPageSize = 80;
    internal const int MaximumPageSize = 4 * DefaultPageSize;
    internal const int DefaultFrameBytes = 16 * 1024;
    private const int IndexBufferBytes = 256 * 1024;
    internal const int DefaultPageBytes = 128 * 1024;

    private readonly FileStream _file;
    private readonly Encoding _decoder;
    private readonly int _codeUnitBytes;
    private readonly long _bomLength;

    public LogDataReader(FileStream file, TextEncodingKind encoding)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        Encoding = encoding;
        (_decoder, _codeUnitBytes) = CreateEncoding(encoding);
        _bomLength = DetectBomLength(file, encoding);
    }

    public TextEncodingKind Encoding { get; }
    public long PreambleLength => _bomLength;

    public byte[] ComputeBookmarkDigest(long witnessedSize)
    {
        var length = _file.Length;
        if (witnessedSize < 0 || witnessedSize > length)
        {
            throw new ArgumentOutOfRangeException(nameof(witnessedSize));
        }

        var count = (int)Math.Min(witnessedSize, 256);
        Span<byte> witness = stackalloc byte[count * 2];
        var handle = _file.SafeFileHandle;
        if (ReadWindowInto(handle, 0, witness[..count], CancellationToken.None) != count
            || ReadWindowInto(handle, witnessedSize - count, witness[count..], CancellationToken.None) != count)
        {
            throw new EndOfStreamException("Файл изменился при проверке закладки лога.");
        }

        return SHA256.HashData(witness);
    }

    public string? ReadTerminatedLineText(long offset, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, MaximumPageBytes);
        var bytes = ReadWindow(_file.SafeFileHandle, offset, byteCount, CancellationToken.None);
        var ending = FindLineEnding(bytes);
        return bytes.Length == byteCount && ending.Index >= 0 && ending.Index + ending.Length == byteCount
            ? _decoder.GetString(bytes, 0, ending.Index) : null;
    }

    public bool IsNewlineBefore(long offset) => IsNewlineBefore(_file.SafeFileHandle, offset);

    public LogLineCheckpoint ScanLineBreaks(LogLineCheckpoint start, long end,
        Action<LogLineCheckpoint>? checkpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var length = _file.Length;
        if (length < _bomLength) return new(length, 0);
        var offset = Math.Clamp(start.Offset, _bomLength, length);
        if (_codeUnitBytes == 2) offset -= (offset - _bomLength) % 2;
        end = Math.Clamp(end, offset, length);
        var lines = start.Lines;
        var handle = _file.SafeFileHandle;
        var previousCr = false;
        if (offset >= _bomLength + _codeUnitBytes)
        {
            Span<byte> preceding = stackalloc byte[2];
            var unit = preceding[.._codeUnitBytes];
            previousCr = RandomAccess.Read(handle, unit, offset - _codeUnitBytes) == _codeUnitBytes && IsCarriageReturn(unit);
        }
        if (offset == end) return new(offset, lines);

        var buffer = ArrayPool<byte>.Shared.Rent(IndexBufferBytes);
        try
        {
            while (offset < end)
            {
                var request = (int)Math.Min(IndexBufferBytes, end - offset);
                var read = ReadWindowInto(handle, offset, buffer.AsSpan(0, request), token);
                if (read == 0) break;

                var bytes = buffer.AsSpan(0, read);
                if (_codeUnitBytes == 1)
                {
                    lines += CountByteLineBreaks(bytes, previousCr);
                    previousCr = bytes[^1] == '\r';
                }
                else
                {
                    for (var i = 0; i + 2 <= bytes.Length; i += 2)
                    {
                        var unit = bytes.Slice(i, 2);
                        var cr = IsCarriageReturn(unit);
                        if (cr || (!previousCr && IsLineFeed(unit))) lines++;
                        previousCr = cr;
                    }
                }

                offset += read;
                checkpoint?.Invoke(new(offset, lines));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new(offset, lines);
    }

    // Each CR counts once. An LF counts only when the preceding byte is not CR.
    // Count in byte lanes and widen the sums before a lane can overflow.
    private static int CountByteLineBreaks(ReadOnlySpan<byte> bytes, bool previousCr = false)
    {
        if (bytes.IsEmpty) return 0;
        var lines = 0;
        var i = 0;
        if (Sse2.IsSupported && bytes.Length > Vector128<byte>.Count)
        {
            // Count the first byte using the incoming CR state. Each full
            // vector after that can read its predecessors one byte behind it.
            if (bytes[0] == '\r' || (!previousCr && bytes[0] == '\n')) lines++;
            i = 1;
            ref var start = ref MemoryMarshal.GetReference(bytes);
            var width = Vector128<byte>.Count;
            var crs = Vector128.Create((byte)'\r');
            var lfs = Vector128.Create((byte)'\n');
            while (i <= bytes.Length - width)
            {
                var stop = i + Math.Min((bytes.Length - i) / width, 128) * width;
                var counts = Vector128<byte>.Zero;
                for (; i < stop; i += width)
                {
                    var current = Vector128.LoadUnsafe(ref start, (nuint)i);
                    var prior = Vector128.LoadUnsafe(ref start, (nuint)(i - 1));
                    var cr = Vector128.Equals(current, crs);
                    var lf = Vector128.Equals(current, lfs);
                    var followsCr = Vector128.Equals(prior, crs);
                    counts -= cr | Vector128.AndNot(lf, followsCr);
                }
                var sums = Sse2.SumAbsoluteDifferences(counts, Vector128<byte>.Zero).AsUInt64();
                lines += (int)(sums.GetElement(0) + sums.GetElement(1));
            }
            previousCr = bytes[i - 1] == '\r';
        }
        for (; i < bytes.Length; i++)
        {
            var cr = bytes[i] == '\r';
            if (cr || (!previousCr && bytes[i] == '\n')) lines++;
            previousCr = cr;
        }
        return lines;
    }

    public static TextEncodingKind DetectEncoding(FileStream file, TextEncodingKind? requested)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (requested.HasValue)
        {
            return requested.Value;
        }

        Span<byte> prefix = stackalloc byte[4];
        var read = RandomAccess.Read(file.SafeFileHandle, prefix, 0);
        if (read >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return TextEncodingKind.Utf16LittleEndian;
        }
        if (read >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return TextEncodingKind.Utf16BigEndian;
        }
        if (read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            return TextEncodingKind.Utf8Bom;
        }
        return TextEncodingKind.Utf8;
    }

    internal byte[] ReadMinimapWidths(long start, long fileLength, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        start = Math.Max(start, _bomLength);
        if (start >= fileLength) return [];
        if (_codeUnitBytes == 2) start += (start - _bomLength) % 2;
        var units = (fileLength - start) / _codeUnitBytes;
        if (units == 0) return [];

        const int maxSamples = 32;
        var widths = new byte[(int)Math.Min(units, maxSamples)];
        var intervals = Math.Max(1, widths.Length - 1);
        // Four bytes per displayed character, plus room to skip an initial partial UTF-8 scalar.
        Span<byte> buffer = stackalloc byte[MinimapColumns * 4 + 3];
        var handle = _file.SafeFileHandle;
        for (var i = 0; i < widths.Length; i++)
        {
            var unit = (long)((Int128)i * (units - 1) / intervals);
            var offset = start + unit * _codeUnitBytes;
            var count = (int)Math.Min(buffer.Length, fileLength - offset);
            var read = ReadWindowInto(handle, offset, buffer[..count], token);
            ReadOnlySpan<byte> bytes = buffer[..(read - read % _codeUnitBytes)];
            if ((Encoding is TextEncodingKind.Utf8 or TextEncodingKind.Utf8Bom) && offset > _bomLength)
            {
                while (!bytes.IsEmpty && IsUtf8Continuation(bytes[0])) bytes = bytes[1..];
            }
            var end = FindLineEnding(bytes).Index;
            if (end >= 0) bytes = bytes[..end];
            widths[i] = (byte)Math.Min(MinimapColumns, _decoder.GetCharCount(bytes));
        }
        return widths;
    }

    public LogPage ReadPage(
        long offset,
        int pageSize = DefaultPageSize,
        int maxFrameBytes = DefaultFrameBytes,
        int maxPageBytes = DefaultPageBytes,
        CancellationToken cancellationToken = default,
        long endOffset = long.MaxValue) =>
        ReadPage(offset, pageSize, maxFrameBytes, maxPageBytes, cancellationToken, endOffset, null);

    public SequentialScan BeginSequentialScan(int maxPageBytes = DefaultPageBytes, long readAheadEnd = long.MaxValue)
    {
        if (maxPageBytes < DefaultFrameBytes || maxPageBytes > DefaultPageBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPageBytes));
        return new(this, maxPageBytes, readAheadEnd);
    }

    private LogPage ReadPage(long offset, int pageSize, int maxFrameBytes, int maxPageBytes,
        CancellationToken cancellationToken, long endOffset, SequentialScan? scan)
    {
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        if (maxFrameBytes is < 4 or > MaximumFrameBytes || maxPageBytes < maxFrameBytes || maxPageBytes > MaximumPageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPageBytes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var observedLength = Math.Min(_file.Length, endOffset);
        var requestedOffset = Math.Clamp(offset, 0, observedLength);
        var alignedOffset = requestedOffset < observedLength
            ? scan?.IsExpectedOffset(requestedOffset) == true
                ? requestedOffset
                : AlignOffset(requestedOffset, observedLength)
            : requestedOffset;
        var readOffset = Math.Min(requestedOffset, alignedOffset);
        var readLength = (int)Math.Min((long)maxPageBytes + LookaheadBytes, observedLength - readOffset);
        var buffer = scan is null ? ArrayPool<byte>.Shared.Rent(readLength) : null;
        try
        {
            var handle = _file.SafeFileHandle;
            ReadOnlySpan<byte> bytes;
            if (scan is null)
            {
                var read = ReadWindowInto(handle, readOffset, buffer.AsSpan(0, readLength), cancellationToken);
                bytes = buffer.AsSpan(0, read);
            }
            else
            {
                bytes = scan.ReadWindow(handle, readOffset, readLength, observedLength, cancellationToken);
            }
            var firstPosition = (int)Math.Min(Math.Max(_bomLength, alignedOffset) - readOffset, bytes.Length);
            var position = firstPosition;

            var pageEnd = Math.Min(bytes.Length, maxPageBytes);
            var rows = new List<LogRow>(pageSize);
            var inputStartsWithinLine = alignedOffset > _bomLength
                && (scan?.TryGetExpectedStartsWithinLine(requestedOffset, out var expectedStartsWithinLine) == true
                    ? expectedStartsWithinLine
                    : !IsNewlineBefore(handle, alignedOffset));

            while (rows.Count < pageSize && position < pageEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowStart = position;
                var frameLimit = Math.Min(maxFrameBytes, pageEnd - position);
                var frame = ReadFrame(bytes, position, frameLimit, observedLength - readOffset);
                if (frame.ConsumedBytes == 0) break;

                var startsWithinLine = rowStart == firstPosition
                    ? inputStartsWithinLine
                    : !IsNewlineBefore(bytes, rowStart);
                var text = _decoder.GetString(bytes.Slice(position, frame.TextBytes));
                var rowEnd = position + frame.ConsumedBytes;
                if (!(startsWithinLine && frame.Terminated && text.Length == 0))
                {
                    rows.Add(new LogRow(
                        readOffset + rowStart,
                        readOffset + rowEnd,
                        text,
                        startsWithinLine || frame.Truncated,
                        frame.Truncated,
                        startsWithinLine));
                }

                position = rowEnd;
            }

            var nextOffset = readOffset + position;
            var currentLength = Math.Min(_file.Length, endOffset);
            return new LogPage(
                rows,
                readOffset,
                nextOffset,
                observedLength,
                nextOffset < currentLength,
                currentLength < observedLength);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private readonly record struct Frame(int TextBytes, int ConsumedBytes, bool Terminated, bool Truncated);

    private Frame ReadFrame(ReadOnlySpan<byte> bytes, int position, int limit, long bytesRemaining)
    {
        // A live UTF-16 file can end between the two bytes of a code unit.
        // Decode the dangling byte at snapshot EOF, but not at an I/O boundary.
        if (position + limit < bytesRemaining) limit -= limit % _codeUnitBytes;
        if (limit == 0) return default;
        var frame = bytes.Slice(position, limit);
        var ending = FindLineEnding(frame);
        if (ending.Index >= 0)
        {
            var consumed = ending.Index + ending.Length;
            // A CR in the final code unit can have its LF in lookahead.
            if (consumed == limit && ending.Length == _codeUnitBytes
                && IsCarriageReturn(frame[ending.Index..])
                && IsLineFeed(bytes[(position + limit)..]))
                consumed += _codeUnitBytes;
            return new(ending.Index, consumed, true, false);
        }
        if (TryGetLineEndingAt(bytes, position + limit, out var followingEnding))
            return new(limit, limit + followingEnding, true, false);
        if (position + limit >= bytesRemaining) return new(limit, limit, false, false);

        var safe = _codeUnitBytes == 2 ? CompleteUtf16PrefixLength(frame)
            : Encoding == TextEncodingKind.Windows1251 ? limit : CompleteUtf8PrefixLength(frame);
        return new(safe, safe, false, true);
    }

    internal sealed class SequentialScan(LogDataReader reader, int pageLimit, long readAheadEnd) : IDisposable
    {
        // Keep the next page contiguous while retaining unread bytes from the current page.
        private const int Capacity = 2 * DefaultPageBytes;
        private byte[]? _buffer;
        private long _offset;
        private long _observedLength;
        private int _length;
        private bool _reuseWindow;
        private bool _disposed;
        private bool _hasExpectedOffset;
        private long _expectedOffset;
        private bool _expectedStartsWithinLine;

        public LogPage ReadPage(long offset, CancellationToken cancellationToken = default,
            long endOffset = long.MaxValue, int maxPageBytes = DefaultPageBytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            maxPageBytes = Math.Min(pageLimit, maxPageBytes);
            var page = reader.ReadPage(offset, DefaultPageSize, Math.Min(DefaultFrameBytes, maxPageBytes), maxPageBytes,
                cancellationToken, endOffset, this);
            _expectedOffset = page.NextOffset;
            _hasExpectedOffset = true;
            _expectedStartsWithinLine = page.Rows.Count > 0 && page.Rows[^1].IsTruncated;
            // A page limited by bytes already used most of its window. Only retain
            // a window when the preceding page stopped at the row limit early.
            _reuseWindow = page.Rows.Count == DefaultPageSize
                && page.NextOffset - page.StartOffset <= maxPageBytes / 2;
            if (!_reuseWindow)
            {
                _length = 0;
            }
            return page;
        }

        internal bool IsExpectedOffset(long offset) => _hasExpectedOffset && offset == _expectedOffset;

        internal bool TryGetExpectedStartsWithinLine(long offset, out bool startsWithinLine)
        {
            startsWithinLine = _expectedStartsWithinLine;
            return IsExpectedOffset(offset);
        }

        internal ReadOnlySpan<byte> ReadWindow(SafeFileHandle handle, long offset, int length, long observedLength,
            CancellationToken cancellationToken)
        {
            var buffer = _buffer ??= ArrayPool<byte>.Shared.Rent(Capacity);
            // Keep the delimiter lookahead while respecting the scan budget and the snapshot boundary.
            var limit = Math.Min(observedLength, Math.Max(offset + length, readAheadEnd));
            if (offset < _offset || offset >= _offset + _length - LookaheadBytes || observedLength < _observedLength)
            {
                _offset = offset;
                _length = 0;
                var initialLength = (int)Math.Min(Capacity, limit - offset);
                _length = ReadWindowInto(handle, offset, buffer.AsSpan(0, initialLength), cancellationToken);
            }
            else if (offset + length > _offset + _length)
            {
                if (offset + length > _offset + Capacity)
                {
                    var consumed = (int)(offset - _offset);
                    _length -= consumed;
                    buffer.AsSpan(consumed, _length).CopyTo(buffer);
                    _offset = offset;
                }

                var end = _offset + _length;
                var count = (int)Math.Min(Capacity - _length, limit - end);
                _length += ReadWindowInto(handle, end, buffer.AsSpan(_length, count), cancellationToken);
            }

            _observedLength = observedLength;
            var start = (int)(offset - _offset);
            return buffer.AsSpan(start, Math.Min(length, _length - start));
        }

        public void Dispose()
        {
            _disposed = true;
            if (_buffer is { } buffer)
            {
                _buffer = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public long GetMidpointOffset(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = _file.Length;
        if (length == 0)
        {
            return 0;
        }

        return AlignToNextLine(length / 2, cancellationToken);
    }

    public long AlignToNextLine(long offset, CancellationToken cancellationToken = default)
        => AlignToNextLine(offset, _file.Length, cancellationToken);

    public long AlignToNextLine(long offset, long endOffset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = Math.Clamp(endOffset, 0, _file.Length);
        if (length == 0)
        {
            return 0;
        }

        var aligned = AlignOffset(Math.Clamp(offset, 0, length), length);
        if (aligned >= length)
        {
            return length;
        }
        if (aligned <= _bomLength || IsNewlineBefore(_file.SafeFileHandle, aligned))
        {
            return aligned;
        }

        var scanLength = (int)Math.Min((long)MidpointScanBytes + LookaheadBytes, length - aligned);
        var scan = ReadWindow(_file.SafeFileHandle, aligned, scanLength, cancellationToken);
        var lineEnding = FindLineEnding(scan);
        if (lineEnding.Index >= 0)
        {
            var afterLine = aligned + lineEnding.Index + lineEnding.Length;
            return afterLine < length ? afterLine : GetPreviousPageOffset(length, cancellationToken);
        }

        return aligned;
    }

    public long GetEndOffset(CancellationToken cancellationToken = default) =>
        GetPreviousPageOffset(_file.Length, cancellationToken);

    public long GetPreviousPageOffset(long endOffset, CancellationToken cancellationToken = default, bool fullBlock = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = Math.Clamp(endOffset, 0, _file.Length);
        if (length <= _bomLength + 1)
        {
            return 0;
        }

        var candidate = Math.Max(_bomLength, length - DefaultPageBytes);
        var aligned = AlignOffset(candidate, length);
        var scanLength = (int)Math.Min(DefaultPageBytes, length - aligned);
        var buffer = ArrayPool<byte>.Shared.Rent(scanLength);
        try
        {
            var handle = _file.SafeFileHandle;
            var read = ReadWindowInto(handle, aligned, buffer.AsSpan(0, scanLength), cancellationToken);
            var bytes = buffer.AsSpan(0, read);
            if (!fullBlock) return FindPreviousPageStart(bytes, aligned, cancellationToken);
            if (aligned <= _bomLength || IsNewlineBefore(handle, aligned)) return aligned;
            var ending = FindLineEnding(bytes);
            var after = ending.Index + ending.Length;
            return ending.Index >= 0 && after < read ? aligned + after : aligned;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private long FindPreviousPageStart(ReadOnlySpan<byte> bytes, long aligned, CancellationToken cancellationToken)
    {
        var pageStart = bytes.Length;
        var trailingBytes = bytes.Length % _codeUnitBytes;
        var searchIndex = bytes.Length - trailingBytes - _codeUnitBytes;
        if (trailingBytes == 0 && searchIndex >= 0 && IsLineEndingCodeUnit(bytes.Slice(searchIndex, _codeUnitBytes)))
        {
            if (IsLineFeed(bytes.Slice(searchIndex, _codeUnitBytes))
                && searchIndex >= _codeUnitBytes
                && IsCarriageReturn(bytes.Slice(searchIndex - _codeUnitBytes, _codeUnitBytes)))
            {
                searchIndex -= _codeUnitBytes * 2;
            }
            else
            {
                searchIndex -= _codeUnitBytes;
            }
        }

        var renderedRows = 0;
        for (var index = searchIndex; index >= 0; index -= _codeUnitBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_codeUnitBytes == 1)
            {
                index = bytes[..(index + 1)].LastIndexOfAny((byte)'\r', (byte)'\n');
                if (index < 0) break;
            }
            else if (!IsLineEndingCodeUnit(bytes.Slice(index, _codeUnitBytes)))
            {
                continue;
            }

            if (IsLineFeed(bytes.Slice(index, _codeUnitBytes)) && index >= _codeUnitBytes
                && IsCarriageReturn(bytes.Slice(index - _codeUnitBytes, _codeUnitBytes)))
            {
                continue;
            }

            var endingBytes = IsCarriageReturn(bytes.Slice(index, _codeUnitBytes))
                && index + _codeUnitBytes * 2 <= bytes.Length
                && IsLineFeed(bytes.Slice(index + _codeUnitBytes, _codeUnitBytes))
                    ? _codeUnitBytes * 2
                    : _codeUnitBytes;
            var lineStart = index + endingBytes;
            renderedRows += TailFrameCount(pageStart - lineStart);
            if (renderedRows > DefaultPageSize)
            {
                return aligned + pageStart;
            }

            pageStart = lineStart;
            if (renderedRows == DefaultPageSize)
            {
                return aligned + pageStart;
            }
        }

        if (pageStart < bytes.Length && (renderedRows + TailFrameCount(pageStart) > DefaultPageSize
            || (aligned > _bomLength && !IsNewlineBefore(_file.SafeFileHandle, aligned))))
        {
            return aligned + pageStart;
        }

        return aligned;
    }

    // A frame can end up to three bytes early to preserve a UTF code point.
    private static int TailFrameCount(int bytes) => Math.Max(1, (bytes + DefaultFrameBytes - 4) / (DefaultFrameBytes - 3));

    public static FileStream Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
    }

    private (int Index, int Length) FindLineEnding(ReadOnlySpan<byte> bytes)
    {
        if (_codeUnitBytes == 1)
        {
            var index = bytes.IndexOfAny((byte)'\r', (byte)'\n');
            if (index < 0) return (-1, 0);
            return (index, bytes[index..].StartsWith("\r\n"u8) ? 2 : 1);
        }

        for (var index = 0; index + _codeUnitBytes <= bytes.Length; index += _codeUnitBytes)
        {
            if (TryGetLineEndingAt(bytes, index, out var length)) return (index, length);
        }
        return (-1, 0);
    }

    private bool TryGetLineEndingAt(ReadOnlySpan<byte> bytes, int offset, out int endingLength)
    {
        endingLength = 0;
        if (offset < 0 || offset + _codeUnitBytes > bytes.Length)
        {
            return false;
        }

        var codeUnit = bytes.Slice(offset, _codeUnitBytes);
        if (IsLineFeed(codeUnit))
        {
            endingLength = _codeUnitBytes;
            return true;
        }
        if (!IsCarriageReturn(codeUnit))
        {
            return false;
        }

        endingLength = _codeUnitBytes;
        if (offset + _codeUnitBytes * 2 <= bytes.Length
            && IsLineFeed(bytes.Slice(offset + _codeUnitBytes, _codeUnitBytes)))
        {
            endingLength += _codeUnitBytes;
        }
        return true;
    }

    private int CompleteUtf8PrefixLength(ReadOnlySpan<byte> bytes)
    {
        var index = bytes.Length - 1;
        var continuationCount = 0;
        while (index >= 0 && IsUtf8Continuation(bytes[index]))
        {
            continuationCount++;
            index--;
        }
        if (index < 0)
        {
            return bytes.Length;
        }

        var expectedLength = bytes[index] switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1
        };
        return expectedLength > continuationCount + 1 ? index : bytes.Length;
    }

    private int CompleteUtf16PrefixLength(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.Length - bytes.Length % 2;
        if (length < 2)
        {
            return length;
        }

        var lastCodeUnit = Encoding == TextEncodingKind.Utf16LittleEndian
            ? (char)(bytes[length - 2] | bytes[length - 1] << 8)
            : (char)(bytes[length - 1] | bytes[length - 2] << 8);
        if (char.IsHighSurrogate(lastCodeUnit))
        {
            return length - 2;
        }
        return length;
    }

    private long AlignOffset(long offset, long length)
    {
        if (_codeUnitBytes == 2)
        {
            var relativeOffset = Math.Max(0, offset - _bomLength);
            return Math.Min(length, _bomLength + relativeOffset - relativeOffset % 2);
        }
        if (offset <= _bomLength || offset >= length || Encoding == TextEncodingKind.Windows1251)
        {
            return offset;
        }

        // A stray continuation byte is itself displayed as U+FFFD. Skip bytes
        // only if the requested position falls inside an actual UTF-8 sequence.
        var before = (int)Math.Min(3, offset - _bomLength);
        var after = (int)Math.Min(3, length - offset);
        Span<byte> bytes = stackalloc byte[6];
        var read = ReadWindowInto(_file.SafeFileHandle, offset - before, bytes[..(before + after)], default);
        if (read <= before || !IsUtf8Continuation(bytes[before])) return offset;
        var lead = before;
        while (lead > 0 && IsUtf8Continuation(bytes[lead])) lead--;
        return Rune.DecodeFromUtf8(bytes[lead..read], out _, out var consumed) == OperationStatus.Done
            && lead + consumed > before ? offset + (lead + consumed - before) : offset;
    }

    private bool IsNewlineBefore(ReadOnlySpan<byte> bytes, int localOffset)
    {
        if (localOffset < _codeUnitBytes)
        {
            return false;
        }
        var previous = bytes.Slice(localOffset - _codeUnitBytes, _codeUnitBytes);
        if (IsLineFeed(previous))
        {
            return true;
        }
        return IsCarriageReturn(previous)
            && (localOffset + _codeUnitBytes > bytes.Length
                || !IsLineFeed(bytes.Slice(localOffset, _codeUnitBytes)));
    }

    private bool IsNewlineBefore(SafeFileHandle handle, long offset)
    {
        if (offset < _codeUnitBytes)
        {
            return false;
        }
        Span<byte> bytes = stackalloc byte[2];
        var read = RandomAccess.Read(handle, bytes[.._codeUnitBytes], offset - _codeUnitBytes);
        if (read != _codeUnitBytes)
        {
            return false;
        }
        var previous = bytes[.._codeUnitBytes];
        if (IsLineFeed(previous))
        {
            return true;
        }
        if (!IsCarriageReturn(previous))
        {
            return false;
        }

        read = RandomAccess.Read(handle, bytes[.._codeUnitBytes], offset);
        return read != _codeUnitBytes || !IsLineFeed(bytes[.._codeUnitBytes]);
    }

    private bool IsLineFeed(ReadOnlySpan<byte> bytes) => _codeUnitBytes == 1
        ? bytes.Length > 0 && bytes[0] == (byte)'\n'
        : Encoding == TextEncodingKind.Utf16LittleEndian
            ? bytes.Length >= 2 && bytes[0] == (byte)'\n' && bytes[1] == 0
            : bytes.Length >= 2 && bytes[0] == 0 && bytes[1] == (byte)'\n';

    private bool IsCarriageReturn(ReadOnlySpan<byte> bytes) => _codeUnitBytes == 1
        ? bytes.Length > 0 && bytes[0] == (byte)'\r'
        : Encoding == TextEncodingKind.Utf16LittleEndian
            ? bytes.Length >= 2 && bytes[0] == (byte)'\r' && bytes[1] == 0
            : bytes.Length >= 2 && bytes[0] == 0 && bytes[1] == (byte)'\r';

    private bool IsLineEndingCodeUnit(ReadOnlySpan<byte> bytes) => IsLineFeed(bytes) || IsCarriageReturn(bytes);

    private static bool IsUtf8Continuation(byte value) => value is >= 0x80 and <= 0xBF;

    private static byte[] ReadWindow(SafeFileHandle handle, long offset, int length, CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        var read = ReadWindowInto(handle, offset, bytes, cancellationToken);
        if (read != bytes.Length)
        {
            Array.Resize(ref bytes, read);
        }
        return bytes;
    }

    private static int ReadWindowInto(SafeFileHandle handle, long offset, Span<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, buffer[totalRead..], offset + totalRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }

    private static (Encoding Encoding, int CodeUnitBytes) CreateEncoding(TextEncodingKind encoding)
    {
        if (encoding == TextEncodingKind.Windows1251)
        {
            System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (System.Text.Encoding.GetEncoding(1251, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback), 1);
        }

        return encoding switch
        {
            TextEncodingKind.Utf16LittleEndian => (new UnicodeEncoding(false, false, false), 2),
            TextEncodingKind.Utf16BigEndian => (new UnicodeEncoding(true, false, false), 2),
            _ => (new UTF8Encoding(false, false), 1)
        };
    }

    private static long DetectBomLength(FileStream file, TextEncodingKind encoding)
    {
        Span<byte> prefix = stackalloc byte[3];
        var read = RandomAccess.Read(file.SafeFileHandle, prefix, 0);
        return encoding switch
        {
            TextEncodingKind.Utf8Bom when read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF => 3,
            TextEncodingKind.Utf16LittleEndian when read >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE => 2,
            TextEncodingKind.Utf16BigEndian when read >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF => 2,
            _ => 0
        };
    }
}

internal sealed record LogPage(
    IReadOnlyList<LogRow> Rows,
    long StartOffset,
    long NextOffset,
    long FileLength,
    bool HasMore,
    bool WasTruncated);

internal sealed record LogRow(
    long StartOffset,
    long EndOffset,
    string Text,
    bool ContinuesLine,
    bool IsTruncated,
    bool StartsWithinLine = false);

internal readonly record struct LogLineCheckpoint(long Offset, long Lines);
