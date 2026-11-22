using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RustText.Demo.Editor;

namespace RustText.Demo.Logs;

internal sealed record LogSearchCursor(long Offset, int MatchIndex, bool SkipLine = false,
    long LineOffset = -1, string PendingText = "");
internal sealed record LogSearchHit(long Offset, long? LineNumber, string Preview, int MatchIndex, int MatchLength,
    int LineLengthBytes, string LineDigest, bool WasIncomplete);
internal sealed record LogSearchPage(IReadOnlyList<LogSearchHit> Hits, LogSearchCursor? NextCursor, bool HasMore, string? Message);

internal sealed partial class LogDataReader
{
    internal const int MaximumSearchLineBytes = 1024 * 1024;

    internal LogSearchPage Search(SearchOptions options, LogSearchCursor cursor, long end, long? lineNumber,
        int maxResults, long maxScanBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // There is no newline inside a logical log line. Without Multiline,
        // .NET can anchor a search such as \d{12}$ at the end of its input.
        var matcher = new TextMatcher(options, multiline: false);
        var needle = CreateSearchPrefilter(options);
        var results = new List<LogSearchHit>(Math.Min(maxResults, 32));
        var offset = Math.Max(cursor.Offset, _bomLength);
        end = Math.Min(end, _file.Length);
        if (offset >= end) return new(results, null, false, null);

        var budgetEnd = offset + Math.Min(maxScanBytes, end - offset);
        offset = AlignOffset(offset, end);
        var handle = _file.SafeFileHandle;
        var continued = offset > _bomLength && !IsNewlineBefore(handle, offset);
        var cursorLineStart = cursor.LineOffset >= 0 ? cursor.LineOffset : offset;
        var lineStart = cursorLineStart;
        var skipLine = cursor.SkipLine;
        StringBuilder? pending = cursor.PendingText.Length == 0 ? null : new(cursor.PendingText);
        string? notice = null;
        char[]? buffer = null;
        using var scan = BeginSequentialScan(readAheadEnd: budgetEnd);
        try
        {
            while (offset < end)
            {
                token.ThrowIfCancellationRequested();
                var pageBytes = (int)Math.Clamp(budgetEnd - offset, 4, DefaultPageBytes);
                var request = (int)Math.Min(pageBytes + LookaheadBytes, end - offset);
                var bytes = scan.ReadWindow(handle, offset, request, end, token);
                var pageEnd = Math.Min(bytes.Length, pageBytes);
                var position = 0;
                var canMoveTail = pageEnd == DefaultPageBytes && offset + pageEnd < budgetEnd;

                // Skip only complete lines, never a continuation or a line split
                // by the I/O window. A window cannot contain an over-limit line.
                if (!continued && !needle.IsEmpty && pageEnd >= 1024)
                {
                    var last = bytes[..pageEnd].LastIndexOfAny((byte)'\r', (byte)'\n');
                    if (last >= 0)
                    {
                        var skip = last + 1;
                        if (bytes[last] == '\r')
                        {
                            // Do not guess whether a CR beyond the last observed byte has an LF.
                            if (skip == bytes.Length && offset + skip < end) skip = 0;
                            else if (skip < bytes.Length && bytes[skip] == '\n') skip++;
                        }
                        if (skip > 0 && !needle.MayMatch(bytes[..skip]))
                        {
                            if (lineNumber.HasValue) lineNumber += CountByteLineBreaks(bytes[..skip]);
                            position = skip;
                        }
                    }
                }

                while (position < pageEnd)
                {
                    token.ThrowIfCancellationRequested();
                    var limit = Math.Min(DefaultFrameBytes, pageEnd - position);
                    limit -= limit % _codeUnitBytes;
                    if (limit == 0) break;
                    var frame = ReadFrame(bytes, position, limit, end - offset);
                    if (frame.ConsumedBytes == 0) break;
                    // Start the next read window at the last complete line. A
                    // short fragment at its tail then needs no decoded buffer.
                    if (canMoveTail && !continued && position > 0 && frame.Truncated && limit < DefaultFrameBytes) break;
                    var content = bytes.Slice(position, frame.TextBytes);
                    if (!continued)
                    {
                        lineStart = offset + position;
                        pending?.Clear();
                        skipLine = false;
                    }
                    position += frame.ConsumedBytes;
                    var next = offset + position;
                    var matchIndex = lineStart == cursorLineStart ? cursor.MatchIndex : 0;
                    if (!skipLine && next - lineStart > MaximumSearchLineBytes)
                    {
                        skipLine = true;
                        pending?.Clear();
                        notice = $"Пропущена строка длиннее лимита поиска в {MaximumSearchLineBytes:N0} байт.";
                    }
                    if (frame.Truncated)
                    {
                        continued = true;
                        if (!skipLine) (pending ??= new()).Append(DecodeSearchFrame(content, ref buffer));
                        if (next >= budgetEnd && next < end)
                        {
                            var resume = skipLine ? new LogSearchCursor(next, 0, SkipLine: true)
                                : new LogSearchCursor(next, matchIndex, LineOffset: lineStart, PendingText: pending?.ToString() ?? "");
                            return new(results, resume, true, notice);
                        }
                        continue;
                    }

                    if (!skipLine && (continued || needle.MayMatch(content)))
                    {
                        var text = DecodeSearchFrame(content, ref buffer);
                        if (pending is { Length: > 0 }) text = pending.Append(text).ToString().AsSpan();
                        var matches = matcher.Enumerate(text, Math.Clamp(matchIndex, 0, text.Length));
                        if (matches.MoveNext())
                        {
                            var digest = SearchLineDigest(text);
                            var incomplete = next == end && !IsNewlineBefore(handle, end);
                            var lineBytes = checked((int)(next - lineStart));
                            do
                            {
                                token.ThrowIfCancellationRequested();
                                var match = matches.Current;
                                results.Add(new(lineStart, lineNumber, ClipMatch(text, match.Index), match.Index,
                                    match.Length, lineBytes, digest, incomplete));
                                var moreOnLine = matches.MoveNext();
                                if (results.Count == maxResults)
                                {
                                    var resume = moreOnLine ? new LogSearchCursor(lineStart, matches.Current.Index)
                                        : next < end ? new LogSearchCursor(next, 0) : null;
                                    return new(results, resume, resume is not null, notice);
                                }
                                if (!moreOnLine) break;
                            } while (true);
                        }
                    }
                    continued = false;
                    lineNumber++;
                    if (next >= budgetEnd) break;
                }
                if (position == 0) break;
                offset += position;
                if (offset >= budgetEnd)
                {
                    var resume = offset < end ? new LogSearchCursor(offset, 0) : null;
                    return new(results, resume, resume is not null, notice);
                }
            }
            return new(results, null, false, notice);
        }
        finally
        {
            if (buffer is not null) ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private ReadOnlySpan<char> DecodeSearchFrame(ReadOnlySpan<byte> bytes, ref char[]? buffer)
    {
        if (bytes.IsEmpty) return [];
        var required = _decoder.GetMaxCharCount(bytes.Length);
        if (buffer is null || required > buffer.Length)
        {
            var next = ArrayPool<char>.Shared.Rent(Math.Max(4096, required));
            if (buffer is not null) ArrayPool<char>.Shared.Return(buffer);
            buffer = next;
        }
        return buffer.AsSpan(0, _decoder.GetChars(bytes, buffer));
    }

    internal static string SearchLineDigest(ReadOnlySpan<char> text)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(MemoryMarshal.AsBytes(text), digest);
        return Convert.ToHexString(digest);
    }

    private static string ClipMatch(ReadOnlySpan<char> text, int matchIndex)
    {
        const int maxLength = 160;
        if (text.Length <= maxLength) return text.ToString();
        var begin = Math.Max(0, matchIndex - 40);
        var prefix = begin > 0 ? "…" : "";
        var end = Math.Min(text.Length, begin + maxLength - prefix.Length);
        var suffix = end < text.Length ? "…" : "";
        return string.Concat(prefix, text[begin..(end - suffix.Length)], suffix);
    }

    private SearchPrefilter CreateSearchPrefilter(SearchOptions options)
    {
        if (Encoding is not (TextEncodingKind.Utf8 or TextEncodingKind.Utf8Bom)) return default;
        var literal = options.IsRegex ? RequiredRegexLiteral(options.Pattern) : options.Pattern.AsSpan();
        if (literal.IsEmpty || literal.Contains('\uFFFD')) return default;
        if (!options.MatchCase && !Ascii.IsValid(literal)) return default;

        var chars = literal;
        while (!chars.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(chars, out _, out var read) != OperationStatus.Done) return default;
            chars = chars[read..];
        }
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(literal)];
        System.Text.Encoding.UTF8.GetBytes(literal, bytes);
        return new(bytes, !options.MatchCase);
    }

    private readonly record struct SearchPrefilter(byte[]? Bytes, bool IgnoreCase)
    {
        public bool IsEmpty => Bytes is null;
        public bool MayMatch(ReadOnlySpan<byte> input)
        {
            if (Bytes is null) return true;
            if (!IgnoreCase) return input.IndexOf(Bytes) >= 0;
            // Unicode invariant folding is not ASCII folding (for example K/K).
            // Let the original regex decide on non-ASCII input.
            if (!Ascii.IsValid(input)) return true;
            var first = Bytes[0];
            var other = first is >= (byte)'a' and <= (byte)'z' ? (byte)(first - 32)
                : first is >= (byte)'A' and <= (byte)'Z' ? (byte)(first + 32) : first;
            while (input.Length >= Bytes.Length)
            {
                var index = input[..(input.Length - Bytes.Length + 1)].IndexOfAny(first, other);
                if (index < 0) break;
                if (Ascii.EqualsIgnoreCase(input.Slice(index, Bytes.Length), Bytes)) return true;
                input = input[(index + 1)..];
            }
            return false;
        }
    }

    // Prove a mandatory literal only in a small grammar: concatenated atoms,
    // anchors, selected shorthand escapes and simple quantifiers. Otherwise
    // defer to .NET: groups can alter case/whitespace rules, and classes,
    // alternation, backreferences and numeric escapes need a full parser.
    private static ReadOnlySpan<char> RequiredRegexLiteral(ReadOnlySpan<char> pattern)
    {
        var best = ReadOnlySpan<char>.Empty;
        var run = 0;
        for (var i = 0; i < pattern.Length;)
        {
            var atom = i++;
            var value = pattern[atom];
            var literal = value is >= ' ' and <= '~' && value is not ('.' or '^' or '$' or '\\');
            if (value is '(' or ')' or '[' or ']' or '{' or '}' or '|' or '*' or '+' or '?') return [];
            if (value == '\\' && (i == pattern.Length || !"dDwWsSbBAZzG".Contains(pattern[i++]))) return [];
            if (i < pattern.Length && pattern[i] is '?' or '+' or '*')
            {
                literal = false;
                i++;
                if (i < pattern.Length && pattern[i] == '?') i++;
            }
            run = literal ? run + 1 : 0;
            if (run > best.Length) best = pattern.Slice(i - run, run);
        }
        return best.Length >= 2 ? best : [];
    }
}
