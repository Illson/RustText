using System.Diagnostics;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RustText.Demo.Logs;

namespace RustText.Demo.Editor;

internal sealed record SearchOptions(string Pattern, bool IsRegex, bool MatchCase, bool WholeWord);
internal sealed record SearchHit(int Offset, int Length, int Line, string Preview);
internal sealed record SearchSummary(IReadOnlyList<SearchHit> Hits, int Count, bool TimedOut, IReadOnlyList<CaretRange>? Carets);
internal sealed record JsonProblem(int Offset, int Line, int Column, string Message);
internal sealed record ListComparison(string Common, string LeftOnly, string RightOnly);
internal sealed record ReplaceAllResult(string Text, int Count, ImmutableList<LineEndingKind>? MixedEndings = null);

internal static class TextOperations
{
    internal const int JsonByteLimit = 64 * 1024 * 1024;
    internal const long AutomaticSearchMaxBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan SearchBudget = TimeSpan.FromMilliseconds(400);

    public static Regex BuildRegex(SearchOptions options)
    {
        if (options.Pattern.Length == 0)
        {
            throw new ArgumentException("Введите текст для поиска.");
        }

        var pattern = options.IsRegex ? options.Pattern : Regex.Escape(options.Pattern);
        if (options.WholeWord)
        {
            pattern = @"(?<![\p{L}\p{N}\p{M}\p{Pc}])(?:" + pattern + @")(?![\p{L}\p{N}\p{M}\p{Pc}])";
        }

        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!options.MatchCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, flags, SearchBudget);
    }

    public static SearchSummary Find(string text, Regex regex, int resultLimit, CancellationToken cancellationToken, int caretLimit = 0)
    {
        var hits = new List<SearchHit>(Math.Min(resultLimit, 100));
        var carets = caretLimit > 0 ? new List<CaretRange>() : null;
        var count = 0;
        var line = 1;
        var lineStart = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            foreach (var match in regex.EnumerateMatches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= SearchBudget)
                {
                    return new SearchSummary(hits, count, true, carets);
                }

                count++;
                if (carets is not null && carets.Count < caretLimit)
                    carets.Add(new CaretRange(match.Index, match.Index + match.Length));
                if (hits.Count >= resultLimit)
                {
                    continue;
                }

                var newline = text.AsSpan(lineStart, match.Index - lineStart).IndexOf('\n');
                while (newline >= 0)
                {
                    line++;
                    lineStart += newline + 1;
                    newline = text.AsSpan(lineStart, match.Index - lineStart).IndexOf('\n');
                }

                var lineEnd = text.IndexOf('\n', match.Index);
                var begin = Math.Max(lineStart, match.Index - 40);
                var end = Math.Min(lineEnd < 0 ? text.Length : lineEnd, begin + 180);
                hits.Add(new SearchHit(match.Index, match.Length, line, text[begin..end]));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new SearchSummary(hits, count, true, carets);
        }

        return new SearchSummary(hits, count, false, carets);
    }

    public static Match? FindNext(string text, Regex regex, int offset, bool backward)
    {
        if (!backward)
        {
            var next = regex.Match(text, Math.Clamp(offset, 0, text.Length));
            return next.Success ? next : regex.Match(text) is { Success: true } wrapped ? wrapped : null;
        }

        var started = Stopwatch.GetTimestamp();
        Match? previous = null;
        Match? last = null;
        for (var match = regex.Match(text); match.Success; match = match.NextMatch())
        {
            CheckBudget(started);
            if (match.Index < offset)
            {
                previous = match;
            }
            else if (previous is not null)
            {
                return previous;
            }

            last = match;
        }

        return previous ?? last;
    }

    public static ReplaceAllResult ReplaceAll(string text, Regex regex, string replacement, bool expandGroups,
        CancellationToken cancellationToken = default, ImmutableList<LineEndingKind>? originalEndings = null)
    {
        var started = Stopwatch.GetTimestamp();
        var builder = new StringBuilder(text.Length);
        var endings = originalEndings is null ? null : ImmutableList.CreateBuilder<LineEndingKind>();
        var endingIndex = 0;
        var count = 0;
        var previousEnd = 0;
        for (var match = regex.Match(text); match.Success; match = match.NextMatch())
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckBudget(started);
            var unchanged = text.AsSpan(previousEnd, match.Index - previousEnd);
            CopySegmentEndings(unchanged);
            builder.Append(unchanged);
            var inserted = expandGroups ? match.Result(replacement) : replacement;
            if (endings is not null)
            {
                var matched = text.AsSpan(match.Index, match.Length);
                var removedBreaks = CountLineBreaks(matched, started, cancellationToken);
                var insertedBreaks = CountLineBreaks(inserted.AsSpan(), started, cancellationToken);
                var retainedBreaks = Math.Min(removedBreaks, insertedBreaks);
                CopySourceEndings(retainedBreaks);
                for (var index = retainedBreaks; index < insertedBreaks; index++) endings.Add(LineEndingKind.Lf);
                endingIndex += removedBreaks - retainedBreaks;
            }
            builder.Append(inserted);
            previousEnd = match.Index + match.Length;
            count++;
        }

        if (count == 0)
        {
            return new ReplaceAllResult(text, 0, originalEndings);
        }

        var tail = text.AsSpan(previousEnd);
        CopySegmentEndings(tail);
        builder.Append(tail);
        return new ReplaceAllResult(builder.ToString(), count, endings?.ToImmutable());

        void CopySegmentEndings(ReadOnlySpan<char> segment)
        {
            if (endings is null) return;
            CopySourceEndings(CountLineBreaks(segment, started, cancellationToken));
        }

        void CopySourceEndings(int breaks)
        {
            if (endings is null) return;
            var available = Math.Min(breaks, Math.Max(0, originalEndings!.Count - endingIndex));
            if (available > 0) endings.AddRange(originalEndings.GetRange(endingIndex, available));
            for (var index = available; index < breaks; index++) endings.Add(LineEndingKind.Lf);
            endingIndex += breaks;
        }
    }

    private static int CountLineBreaks(ReadOnlySpan<char> text, long started, CancellationToken cancellationToken)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckBudget(started);
            }
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                count++;
            }
            else if (text[index] == '\n') count++;
        }
        return count;
    }

    private static void CheckBudget(long started)
    {
        if (Stopwatch.GetElapsedTime(started) >= SearchBudget)
        {
            throw new TimeoutException("Лимит времени операции истёк.");
        }
    }

    public static bool IsJsonPath(string? path) =>
        string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase) || IsJsonLinesPath(path);

    public static bool LooksLikeJson(string text, string? path)
    {
        if (IsJsonPath(path)) return true;
        if (!LogLevels.IsLogPath(path))
            return text.AsSpan().TrimStart().TrimStart('\uFEFF').TrimStart() is ['{' or '[', ..];

        var input = JsonInput(text);
        if (!UsesJsonLines(input, path)) return false;
        var consumed = ReadJsonLine(input, out _);
        _ = ReadJsonLine(input[consumed..], out var nextRecord);
        return IsJsonContainerPrefix(nextRecord.Span);
    }

    internal static bool IsJsonContainerPrefix(ReadOnlySpan<char> record)
    {
        record = record.TrimStart().TrimStart('\uFEFF').TrimStart();
        if (record.IsEmpty) return false;
        var opener = record[0];
        if (opener is not ('{' or '[')) return false;

        var value = record[1..].TrimStart();
        if (value.IsEmpty) return true;
        return opener == '{'
            ? value[0] is '"' or '}'
            : value[0] is '"' or '{' or '[' or ']' or '-' or >= '0' and <= '9' or 't' or 'f' or 'n';
    }

    public static JsonProblem? ValidateJson(string text, string? path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = JsonInput(text);
        CheckJsonSize(input);
        var start = text.Length - input.Length;
        if (!UsesJsonLines(input, path))
        {
            return ValidateJsonValue(input, start, 1);
        }

        for (var line = 1; !input.IsEmpty; line++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var consumed = ReadJsonLine(input, out var record);
            if (ValidateJsonValue(record, start, line) is { } problem)
            {
                return problem;
            }

            input = input[consumed..];
            start += consumed;
        }

        return null;
    }

    private static JsonProblem? ValidateJsonValue(ReadOnlyMemory<char> input, int start, int lineNumber)
    {
        try
        {
            using var parsed = ParseJson(input);
            return null;
        }
        catch (JsonException exception)
        {
            var line = (int)(exception.LineNumber ?? 0);
            var bytes = exception.BytePositionInLine ?? 0;
            var rest = input.Span;
            for (var i = 0; i < line; i++)
            {
                var separator = rest.IndexOf('\n');
                if (separator < 0)
                {
                    break;
                }

                start += separator + 1;
                rest = rest[(separator + 1)..];
            }

            var column = 0;
            foreach (var rune in rest.EnumerateRunes())
            {
                if (rune.Value == '\n' || bytes < rune.Utf8SequenceLength)
                {
                    break;
                }

                bytes -= rune.Utf8SequenceLength;
                column += rune.Utf16SequenceLength;
            }

            return new JsonProblem(start + column, lineNumber + line, column + 1, exception.Message);
        }
    }

    public static string FormatJson(string text, string? path, string? containingText = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = JsonInput(text);
        CheckJsonSize(input);
        var jsonLines = UsesJsonLines(containingText is null ? input : JsonInput(containingText), path);
        using var memory = new MemoryStream();
        using var writer = new Utf8JsonWriter(memory, new JsonWriterOptions
        {
            Indented = !jsonLines,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 128
        });

        if (jsonLines)
        {
            while (!input.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var consumed = ReadJsonLine(input, out var record);
                using var document = ParseJson(record);
                document.WriteTo(writer);
                writer.Flush();
                foreach (var separator in input.Span[record.Length..consumed])
                {
                    memory.WriteByte((byte)separator);
                }

                CheckFormattedJsonSize(memory.Length);
                writer.Reset();
                input = input[consumed..];
            }
        }
        else
        {
            using var document = ParseJson(input);
            document.WriteTo(writer);
            writer.Flush();
            CheckFormattedJsonSize(memory.Length);
        }

        return Encoding.UTF8.GetString(memory.GetBuffer().AsSpan(0, (int)memory.Length));
    }

    private static bool IsJsonLinesPath(string? path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ndjson", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesJsonLines(ReadOnlyMemory<char> input, string? path)
    {
        if (IsJsonLinesPath(path))
        {
            return true;
        }

        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var consumed = ReadJsonLine(input, out var first);
        return first.Span.Trim() is ['{' or '[', .., '}' or ']']
            && !input.Span[consumed..].IsWhiteSpace()
            && Encoding.UTF8.GetByteCount(first.Span) <= JsonByteLimit
            && ValidateJsonValue(first, 0, 1) is null;
    }

    private static int ReadJsonLine(ReadOnlyMemory<char> input, out ReadOnlyMemory<char> record)
    {
        var span = input.Span;
        var separator = span.IndexOfAny('\r', '\n');
        if (separator < 0)
        {
            record = input;
            return input.Length;
        }

        record = input[..separator];
        return separator + (span[separator..] is ['\r', '\n', ..] ? 2 : 1);
    }

    private static ReadOnlyMemory<char> JsonInput(string text) =>
        text.AsMemory(text.Length - text.AsSpan().TrimStart('\uFEFF').Length);

    private static JsonDocument ParseJson(ReadOnlyMemory<char> input) =>
        JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 128 });

    private static void CheckJsonSize(ReadOnlyMemory<char> input)
    {
        if (Encoding.UTF8.GetByteCount(input.Span) > JsonByteLimit)
        {
            throw new InvalidDataException("JSON превышает 64 МиБ.");
        }
    }

    private static void CheckFormattedJsonSize(long length)
    {
        if (length > JsonByteLimit)
        {
            throw new InvalidDataException("Форматированный JSON превышает 64 МиБ.");
        }
    }

    public static ListComparison CompareLists(string left, string right)
    {
        var a = Lines(left);
        var b = Lines(right);
        var inA = a.ToHashSet(StringComparer.Ordinal);
        var inB = b.ToHashSet(StringComparer.Ordinal);
        return new ListComparison(
            string.Join('\n', a.Where(inB.Contains)),
            string.Join('\n', a.Where(line => !inB.Contains(line))),
            string.Join('\n', b.Where(line => !inA.Contains(line))));
    }

    private static string[] Lines(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (set.Add(line))
            {
                lines.Add(line);
            }
        }

        return lines.ToArray();
    }
}
