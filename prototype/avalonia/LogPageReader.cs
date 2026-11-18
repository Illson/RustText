using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RustText.Demo;

internal static class LogPageReader
{
    private const int ReadBufferBytes = 64 * 1024;
    private const int MaximumPageSize = 100;
    private const int MaximumFrameBytes = 64 * 1024;
    private const int MaximumPageBytes = 1024 * 1024;
    private const int MidpointScanBytes = 64 * 1024;
    private const int LookaheadBytes = 4;

    private static readonly UTF8Encoding Utf8 = new(false, false);

    public static FileStream Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferBytes,
            FileOptions.RandomAccess);
    }

    public static LogPage ReadPage(
        FileStream file,
        long offset = 0,
        int pageSize = 80,
        int maxFrameBytes = 16 * 1024,
        int maxPageBytes = 128 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.CanRead || !file.CanSeek)
        {
            throw new ArgumentException("The log stream must be readable and seekable.", nameof(file));
        }
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page size must be between 1 and {MaximumPageSize} rows.");
        }
        if (maxFrameBytes is < 4 or > MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes), $"Frame size must be between 4 and {MaximumFrameBytes} bytes.");
        }
        if (maxPageBytes is < 4 or > MaximumPageBytes || maxPageBytes < maxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPageBytes), $"Page byte limit must be at least the frame size and no more than {MaximumPageBytes} bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var observedLength = file.Length;
        var requestedOffset = Math.Clamp(offset, 0, observedLength);
        var alignedOffset = AlignToUtf8Start(file.SafeFileHandle, requestedOffset, observedLength);
        var maximumReadLength = Math.Min((long)maxPageBytes + LookaheadBytes, observedLength - requestedOffset);
        var bytes = ReadWindow(file.SafeFileHandle, requestedOffset, (int)maximumReadLength, cancellationToken);
        var dataStart = Math.Min(checked((int)(alignedOffset - requestedOffset)), bytes.Length);
        var isBomAtStart = requestedOffset == 0
            && bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF;
        if (isBomAtStart)
        {
            dataStart = 3;
        }

        var pageEnd = Math.Min(bytes.Length, maxPageBytes);
        var inputStartsWithinLine = requestedOffset > 0
            && ReadByteAt(file.SafeFileHandle, requestedOffset - 1) != (byte)'\n';
        var rows = new List<LogRow>(Math.Min(pageSize, maxPageBytes / maxFrameBytes));
        var position = dataStart;

        while (rows.Count < pageSize && position < pageEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = position;
            var rowStartsWithinLine = position == dataStart
                ? inputStartsWithinLine
                : bytes[position - 1] != (byte)'\n';
            var frameLimit = Math.Min(maxFrameBytes, pageEnd - position);
            var frame = bytes.AsSpan(position, frameLimit);
            var newlineIndex = frame.IndexOf((byte)'\n');
            var terminated = newlineIndex >= 0;
            var textByteCount = terminated ? newlineIndex : frameLimit;
            var consumedBytes = terminated ? newlineIndex + 1 : frameLimit;
            var wasTruncated = false;

            if (!terminated)
            {
                var followingIsLineEnd = IsLineEndingAt(bytes, position + frameLimit, bytes.Length);
                var hasFollowingContent = position + frameLimit < observedLength - requestedOffset
                    && !followingIsLineEnd;
                if (hasFollowingContent)
                {
                    var safeByteCount = GetCompleteUtf8PrefixLength(frame);
                    textByteCount = safeByteCount;
                    consumedBytes = safeByteCount;
                    wasTruncated = true;
                }
                else if (followingIsLineEnd)
                {
                    terminated = bytes[position + frameLimit] == (byte)'\n';
                }
            }

            if (terminated && textByteCount > 0 && bytes[position + textByteCount - 1] == (byte)'\r')
            {
                textByteCount--;
            }

            if (consumedBytes <= 0)
            {
                break;
            }

            var text = Utf8.GetString(bytes, position, textByteCount);
            var rowEnd = position + consumedBytes;
            if (!(rowStartsWithinLine && terminated && text.Length == 0))
            {
                rows.Add(new LogRow(
                    requestedOffset + rowStart,
                    requestedOffset + rowEnd,
                    text,
                    rowStartsWithinLine || wasTruncated,
                    wasTruncated));
            }

            position = rowEnd;
            if (!terminated && consumedBytes < frameLimit)
            {
                break;
            }
        }

        var nextOffset = requestedOffset + position;
        var currentLength = file.Length;
        return new LogPage(
            rows,
            requestedOffset,
            nextOffset,
            observedLength,
            nextOffset < currentLength,
            currentLength < observedLength);
    }

    public static long GetMidpointOffset(FileStream file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.CanRead || !file.CanSeek)
        {
            throw new ArgumentException("The log stream must be readable and seekable.", nameof(file));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var length = file.Length;
        if (length == 0)
        {
            return 0;
        }

        var midpoint = length / 2;
        if (midpoint > 0 && ReadByteAt(file.SafeFileHandle, midpoint - 1) == (byte)'\n')
        {
            return midpoint;
        }

        var scanLength = (int)Math.Min(MidpointScanBytes, length - midpoint);
        var scan = ReadWindow(file.SafeFileHandle, midpoint, scanLength, cancellationToken);
        var newlineIndex = scan.AsSpan().IndexOf((byte)'\n');
        if (newlineIndex >= 0)
        {
            var nextLine = midpoint + newlineIndex + 1;
            if (nextLine < length)
            {
                return nextLine;
            }
        }

        if (length - midpoint <= MidpointScanBytes)
        {
            return 0;
        }

        var aligned = AlignToUtf8Start(file.SafeFileHandle, midpoint, length);
        return aligned >= length ? 0 : aligned;
    }

    private static byte[] ReadWindow(
        SafeFileHandle handle,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, buffer.AsSpan(totalRead), offset + totalRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        if (totalRead != buffer.Length)
        {
            Array.Resize(ref buffer, totalRead);
        }
        return buffer;
    }

    private static int GetCompleteUtf8PrefixLength(ReadOnlySpan<byte> bytes)
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

        var expectedLength = Utf8SequenceLength(bytes[index]);
        var actualLength = continuationCount + 1;
        return expectedLength > actualLength ? index : bytes.Length;
    }

    private static int Utf8SequenceLength(byte value) => value switch
    {
        >= 0xC2 and <= 0xDF => 2,
        >= 0xE0 and <= 0xEF => 3,
        >= 0xF0 and <= 0xF4 => 4,
        _ => 1
    };

    private static bool IsUtf8Continuation(byte value) => value is >= 0x80 and <= 0xBF;

    private static long AlignToUtf8Start(SafeFileHandle handle, long offset, long limit)
    {
        if (offset <= 0 || offset >= limit)
        {
            return offset;
        }

        Span<byte> bytes = stackalloc byte[4];
        var count = RandomAccess.Read(handle, bytes, offset);
        if (count == 0 || !IsUtf8Continuation(bytes[0]))
        {
            return offset;
        }

        var aligned = offset + 1;
        while (aligned < limit && aligned - offset < 4)
        {
            var read = RandomAccess.Read(handle, bytes[..1], aligned);
            if (read == 0 || !IsUtf8Continuation(bytes[0]))
            {
                return aligned;
            }
            aligned++;
        }
        return aligned;
    }

    private static byte ReadByteAt(SafeFileHandle handle, long offset)
    {
        Span<byte> value = stackalloc byte[1];
        return RandomAccess.Read(handle, value, offset) == 0 ? (byte)0 : value[0];
    }

    private static bool IsLineEndingAt(byte[] bytes, int offset, int availableLength)
    {
        if (offset >= availableLength)
        {
            return false;
        }
        if (bytes[offset] == (byte)'\n')
        {
            return true;
        }
        return bytes[offset] == (byte)'\r'
            && offset + 1 < availableLength
            && bytes[offset + 1] == (byte)'\n';
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
    bool IsTruncated);
