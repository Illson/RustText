using System.Text;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RustText.Demo;

namespace RustText.Demo.Logs;

internal sealed class LogDataReader
{
    private const int LookaheadBytes = 4;
    private const int MidpointScanBytes = 64 * 1024;
    private const int MaximumPageBytes = 1024 * 1024;
    private const int MaximumFrameBytes = 64 * 1024;
    private const int DefaultPageSize = 80;
    private const int DefaultFrameBytes = 16 * 1024;
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
        var head = ReadWindow(_file.SafeFileHandle, 0, count, CancellationToken.None);
        var tail = ReadWindow(_file.SafeFileHandle, witnessedSize - count, count, CancellationToken.None);
        if (head.Length != count || tail.Length != count)
        {
            throw new EndOfStreamException("Файл изменился при проверке закладки лога.");
        }

        var witness = new byte[count * 2];
        head.CopyTo(witness, 0);
        tail.CopyTo(witness, count);
        return SHA256.HashData(witness);
    }

    public bool IsNewlineBefore(long offset) => IsNewlineBefore(_file.SafeFileHandle, offset);

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

    public LogPage ReadPage(
        long offset,
        int pageSize = DefaultPageSize,
        int maxFrameBytes = DefaultFrameBytes,
        int maxPageBytes = DefaultPageBytes,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        if (maxFrameBytes is < 4 or > MaximumFrameBytes || maxPageBytes < maxFrameBytes || maxPageBytes > MaximumPageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPageBytes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var observedLength = _file.Length;
        var requestedOffset = Math.Clamp(offset, 0, observedLength);
        var alignedOffset = requestedOffset < observedLength
            ? AlignOffset(requestedOffset, observedLength)
            : requestedOffset;
        var readOffset = Math.Min(requestedOffset, alignedOffset);
        var readLength = (int)Math.Min((long)maxPageBytes + LookaheadBytes, observedLength - readOffset);
        var bytes = ReadWindow(_file.SafeFileHandle, readOffset, readLength, cancellationToken);
        var firstPosition = (int)Math.Min(Math.Max(_bomLength, alignedOffset) - readOffset, bytes.Length);
        var position = firstPosition;

        var pageEnd = Math.Min(bytes.Length, maxPageBytes);
        var rows = new List<LogRow>(Math.Min(pageSize, maxPageBytes / maxFrameBytes));
        var inputStartsWithinLine = alignedOffset > _bomLength
            && !IsNewlineBefore(_file.SafeFileHandle, alignedOffset);

        while (rows.Count < pageSize && position < pageEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = position;
            var frameLimit = Math.Min(maxFrameBytes, pageEnd - position);
            frameLimit -= frameLimit % _codeUnitBytes;
            if (frameLimit == 0)
            {
                break;
            }

            var frame = bytes.AsSpan(position, frameLimit);
            var lineEnding = FindLineEnding(frame);
            var terminated = lineEnding.Index >= 0;
            var textByteCount = terminated ? lineEnding.Index : frameLimit;
            var consumedBytes = terminated ? lineEnding.Index + lineEnding.Length : frameLimit;
            var wasTruncated = false;

            // A CR at the final code unit of a frame is already a valid
            // terminator, but when its LF lies in lookahead, consume both so
            // CRLF is never split across pages.
            if (terminated && lineEnding.Length == _codeUnitBytes
                && IsCarriageReturn(frame.Slice(lineEnding.Index, _codeUnitBytes))
                && TryGetLineEndingAt(bytes, position + frameLimit, bytes.Length, out var followingEnding)
                && followingEnding == _codeUnitBytes
                && IsLineFeed(bytes.AsSpan(position + frameLimit, _codeUnitBytes)))
            {
                consumedBytes += _codeUnitBytes;
            }

            if (!terminated)
            {
                var followingIsLineEnd = TryGetLineEndingAt(bytes, position + frameLimit, bytes.Length, out var followingEndingBytes);
                var hasFollowingContent = position + frameLimit < observedLength - readOffset
                    && !followingIsLineEnd;
                if (hasFollowingContent)
                {
                    var safeByteCount = _codeUnitBytes == 1
                        ? CompleteUtf8PrefixLength(frame)
                        : CompleteUtf16PrefixLength(frame);
                    textByteCount = safeByteCount;
                    consumedBytes = safeByteCount;
                    wasTruncated = true;
                }
                else if (followingIsLineEnd)
                {
                    terminated = true;
                    consumedBytes += followingEndingBytes;
                }
            }

            if (consumedBytes <= 0)
            {
                break;
            }

            var startsWithinLine = rowStart == firstPosition
                ? inputStartsWithinLine
                : !IsNewlineBefore(bytes, rowStart);
            var text = _decoder.GetString(bytes.AsSpan(position, textByteCount));
            var rowEnd = position + consumedBytes;
            if (!(startsWithinLine && terminated && text.Length == 0))
            {
                rows.Add(new LogRow(
                    readOffset + rowStart,
                    readOffset + rowEnd,
                    text,
                    startsWithinLine || wasTruncated,
                    wasTruncated));
            }

            position = rowEnd;
        }

        var nextOffset = readOffset + position;
        var currentLength = _file.Length;
        return new LogPage(
            rows,
            readOffset,
            nextOffset,
            observedLength,
            nextOffset < currentLength,
            currentLength < observedLength);
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = _file.Length;
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
            return afterLine < length ? afterLine : 0;
        }

        return length - aligned <= MidpointScanBytes ? 0 : aligned;
    }

    public long GetEndOffset(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = _file.Length;
        if (length <= _bomLength + 1)
        {
            return 0;
        }

        var candidate = Math.Max(_bomLength, length - DefaultPageBytes);
        var aligned = AlignOffset(candidate, length);
        var scanLength = (int)Math.Min(DefaultPageBytes, length - aligned);
        var bytes = ReadWindow(_file.SafeFileHandle, aligned, scanLength, cancellationToken);

        var pageStart = bytes.Length;
        var searchIndex = bytes.Length - _codeUnitBytes;
        if (searchIndex >= 0 && IsLineEndingCodeUnit(bytes.AsSpan(searchIndex, _codeUnitBytes)))
        {
            if (IsLineFeed(bytes.AsSpan(searchIndex, _codeUnitBytes))
                && searchIndex >= _codeUnitBytes
                && IsCarriageReturn(bytes.AsSpan(searchIndex - _codeUnitBytes, _codeUnitBytes)))
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
            if (!IsLineEndingCodeUnit(bytes.AsSpan(index, _codeUnitBytes)))
            {
                continue;
            }

            if (IsLineFeed(bytes.AsSpan(index, _codeUnitBytes)) && index >= _codeUnitBytes
                && IsCarriageReturn(bytes.AsSpan(index - _codeUnitBytes, _codeUnitBytes)))
            {
                continue;
            }

            var endingBytes = IsCarriageReturn(bytes.AsSpan(index, _codeUnitBytes))
                && index + _codeUnitBytes < bytes.Length
                && IsLineFeed(bytes.AsSpan(index + _codeUnitBytes, _codeUnitBytes))
                    ? _codeUnitBytes * 2
                    : _codeUnitBytes;
            var lineStart = index + endingBytes;
            renderedRows += TailFrameCount(pageStart - lineStart);
            if (renderedRows > DefaultPageSize)
            {
                return aligned + pageStart;
            }

            pageStart = lineStart;
            index -= endingBytes - _codeUnitBytes;
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
        for (var index = 0; index + _codeUnitBytes <= bytes.Length; index += _codeUnitBytes)
        {
            var codeUnit = bytes.Slice(index, _codeUnitBytes);
            if (IsLineFeed(codeUnit))
            {
                return (index, _codeUnitBytes);
            }
            if (IsCarriageReturn(codeUnit))
            {
                var length = index + _codeUnitBytes * 2 <= bytes.Length
                    && IsLineFeed(bytes.Slice(index + _codeUnitBytes, _codeUnitBytes))
                        ? _codeUnitBytes * 2
                        : _codeUnitBytes;
                return (index, length);
            }
        }
        return (-1, 0);
    }

    private bool TryGetLineEndingAt(byte[] bytes, int offset, int availableLength, out int endingLength)
    {
        endingLength = 0;
        if (offset < 0 || offset + _codeUnitBytes > availableLength)
        {
            return false;
        }

        var codeUnit = bytes.AsSpan(offset, _codeUnitBytes);
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
        if (offset + _codeUnitBytes * 2 <= availableLength
            && IsLineFeed(bytes.AsSpan(offset + _codeUnitBytes, _codeUnitBytes)))
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
        if (offset <= 0 || offset >= length)
        {
            return offset;
        }

        Span<byte> bytes = stackalloc byte[4];
        var count = RandomAccess.Read(_file.SafeFileHandle, bytes, offset);
        if (count == 0 || !IsUtf8Continuation(bytes[0]))
        {
            return offset;
        }

        var aligned = offset + 1;
        while (aligned < length && aligned - offset < 4)
        {
            if (RandomAccess.Read(_file.SafeFileHandle, bytes[..1], aligned) == 0 || !IsUtf8Continuation(bytes[0]))
            {
                return aligned;
            }
            aligned++;
        }
        return aligned;
    }

    private bool IsNewlineBefore(byte[] bytes, int localOffset)
    {
        if (localOffset < _codeUnitBytes)
        {
            return false;
        }
        var previous = bytes.AsSpan(localOffset - _codeUnitBytes, _codeUnitBytes);
        if (IsLineFeed(previous))
        {
            return true;
        }
        return IsCarriageReturn(previous)
            && (localOffset + _codeUnitBytes > bytes.Length
                || !IsLineFeed(bytes.AsSpan(localOffset, _codeUnitBytes)));
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
        var totalRead = 0;
        while (totalRead < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, bytes.AsSpan(totalRead), offset + totalRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        if (totalRead != bytes.Length)
        {
            Array.Resize(ref bytes, totalRead);
        }
        return bytes;
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
    bool IsTruncated);
