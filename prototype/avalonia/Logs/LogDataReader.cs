using System.Text;
using Microsoft.Win32.SafeHandles;
using RustText.Demo;

namespace RustText.Demo.Logs;

internal sealed class LogDataReader
{
    private const int LookaheadBytes = 4;
    private const int MidpointScanBytes = 64 * 1024;
    private const int MaximumPageBytes = 1024 * 1024;
    private const int MaximumFrameBytes = 64 * 1024;

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
        int pageSize = 80,
        int maxFrameBytes = 16 * 1024,
        int maxPageBytes = 128 * 1024,
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
        var alignedOffset = AlignOffset(requestedOffset, observedLength);
        var readLength = (int)Math.Min((long)maxPageBytes + LookaheadBytes, observedLength - requestedOffset);
        var bytes = ReadWindow(_file.SafeFileHandle, requestedOffset, readLength, cancellationToken);
        var position = Math.Min(checked((int)(alignedOffset - requestedOffset)), bytes.Length);
        if (requestedOffset == 0)
        {
            position = (int)Math.Min(_bomLength, bytes.Length);
        }

        var pageEnd = Math.Min(bytes.Length, maxPageBytes);
        var rows = new List<LogRow>(Math.Min(pageSize, maxPageBytes / maxFrameBytes));
        var inputStartsWithinLine = requestedOffset > _bomLength
            && !IsNewlineBefore(_file.SafeFileHandle, requestedOffset);

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
            var newlineIndex = FindNewline(frame);
            var terminated = newlineIndex >= 0;
            var textByteCount = terminated ? newlineIndex : frameLimit;
            var consumedBytes = terminated ? newlineIndex + _codeUnitBytes : frameLimit;
            var wasTruncated = false;

            if (!terminated)
            {
                var followingIsLineEnd = IsLineEndingAt(bytes, position + frameLimit, bytes.Length);
                var hasFollowingContent = position + frameLimit < observedLength - requestedOffset
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
                }
            }

            if (terminated && textByteCount >= _codeUnitBytes && IsCarriageReturn(bytes.AsSpan(position + textByteCount - _codeUnitBytes, _codeUnitBytes)))
            {
                textByteCount -= _codeUnitBytes;
            }

            if (consumedBytes <= 0)
            {
                break;
            }

            var startsWithinLine = rowStart == (requestedOffset == 0 ? (int)_bomLength : checked((int)(alignedOffset - requestedOffset)))
                ? inputStartsWithinLine
                : !IsNewlineBefore(bytes, rowStart);
            var text = _decoder.GetString(bytes.AsSpan(position, textByteCount));
            var rowEnd = position + consumedBytes;
            if (!(startsWithinLine && terminated && text.Length == 0))
            {
                rows.Add(new LogRow(
                    requestedOffset + rowStart,
                    requestedOffset + rowEnd,
                    text,
                    startsWithinLine || wasTruncated,
                    wasTruncated));
            }

            position = rowEnd;
        }

        var nextOffset = requestedOffset + position;
        var currentLength = _file.Length;
        return new LogPage(
            rows,
            requestedOffset,
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

        var scanLength = (int)Math.Min(MidpointScanBytes, length - aligned);
        var scan = ReadWindow(_file.SafeFileHandle, aligned, scanLength, cancellationToken);
        var newlineIndex = FindNewline(scan);
        if (newlineIndex >= 0)
        {
            var afterLine = aligned + newlineIndex + _codeUnitBytes;
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

        var candidate = Math.Max(_bomLength, length - 128 * 1024);
        var aligned = AlignOffset(candidate, length);
        var scanLength = (int)Math.Min(MidpointScanBytes, length - aligned);
        var bytes = ReadWindow(_file.SafeFileHandle, aligned, scanLength, cancellationToken);
        var newlineIndex = FindNewline(bytes);
        if (newlineIndex >= 0)
        {
            var afterLine = aligned + newlineIndex + _codeUnitBytes;
            return afterLine < length ? afterLine : 0;
        }
        return aligned >= length ? 0 : aligned;
    }

    public static FileStream Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
    }

    private int FindNewline(ReadOnlySpan<byte> bytes)
    {
        if (_codeUnitBytes == 1)
        {
            return bytes.IndexOf((byte)'\n');
        }

        for (var index = 0; index + 1 < bytes.Length; index += 2)
        {
            if (IsLineFeed(bytes[index..(index + 2)]))
            {
                return index;
            }
        }
        return -1;
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
        return IsLineFeed(bytes.AsSpan(localOffset - _codeUnitBytes, _codeUnitBytes));
    }

    private bool IsNewlineBefore(SafeFileHandle handle, long offset)
    {
        if (offset < _codeUnitBytes)
        {
            return false;
        }
        Span<byte> bytes = stackalloc byte[2];
        var read = RandomAccess.Read(handle, bytes[.._codeUnitBytes], offset - _codeUnitBytes);
        return read == _codeUnitBytes && IsLineFeed(bytes[.._codeUnitBytes]);
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

    private bool IsLineEndingAt(byte[] bytes, int offset, int availableLength)
    {
        if (offset + _codeUnitBytes > availableLength)
        {
            return false;
        }
        if (IsLineFeed(bytes.AsSpan(offset, _codeUnitBytes)))
        {
            return true;
        }
        return IsCarriageReturn(bytes.AsSpan(offset, _codeUnitBytes))
            && offset + _codeUnitBytes * 2 <= availableLength
            && IsLineFeed(bytes.AsSpan(offset + _codeUnitBytes, _codeUnitBytes));
    }

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
