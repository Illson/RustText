using System.Diagnostics;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RustText.Demo.Logs;

namespace RustText.Demo.Editor;

internal sealed record SearchOptions(string Pattern, bool IsRegex, bool MatchCase, bool WholeWord);
internal readonly record struct SearchHit(int Offset, int Length, int Line, string Preview);
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

    public static SearchSummary Find(string text, TextMatcher matcher, int resultLimit, CancellationToken cancellationToken, int caretLimit = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hits = new List<SearchHit>(Math.Min(resultLimit, 100));
        var carets = caretLimit > 0 ? new List<CaretRange>() : null;
        var count = 0;
        var line = 1;
        var lineStart = 0;
        var lineEnd = -1;
        var started = Stopwatch.GetTimestamp();
        try
        {
            foreach (var match in matcher.Enumerate(text))
            {
                if ((count & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Stopwatch.GetElapsedTime(started) >= SearchBudget)
                        return new SearchSummary(hits, count, true, carets);
                }

                count++;
                if (carets is not null && carets.Count < caretLimit)
                    carets.Add(new CaretRange(match.Index, match.Index + match.Length));
                if (hits.Count >= resultLimit)
                {
                    continue;
                }

                if (lineEnd < 0) lineEnd = EndOfLine(0);
                while (lineEnd < match.Index)
                {
                    line++;
                    lineStart = lineEnd + 1;
                    lineEnd = EndOfLine(lineStart);
                }

                var begin = Math.Max(lineStart, match.Index - 40);
                var end = Math.Min(lineEnd, begin + 180);
                hits.Add(new SearchHit(match.Index, match.Length, line, text[begin..end]));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new SearchSummary(hits, count, true, carets);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SearchSummary(hits, count, Stopwatch.GetElapsedTime(started) >= SearchBudget, carets);

        int EndOfLine(int start) => text.IndexOf('\n', start) is var end && end >= 0 ? end : text.Length;
    }

    public static TextMatch? FindNext(string text, TextMatcher matcher, int offset, bool backward)
    {
        if (!backward)
        {
            var next = matcher.Enumerate(text, Math.Clamp(offset, 0, text.Length));
            if (next.MoveNext()) return next.Current;
            next = matcher.Enumerate(text);
            return next.MoveNext() ? next.Current : null;
        }

        var started = Stopwatch.GetTimestamp();
        TextMatch? previous = null;
        TextMatch? last = null;
        var count = 0;
        foreach (var match in matcher.Enumerate(text))
        {
            if ((count++ & 63) == 0) CheckBudget(started);
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

    public static ReplaceAllResult ReplaceAll(string text, TextMatcher matcher, string replacement, bool expandGroups,
        CancellationToken cancellationToken = default, ImmutableList<LineEndingKind>? originalEndings = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        if (matcher.Literal is not null && originalEndings is null && !expandGroups)
            return ReplaceOrdinal(text, matcher, replacement, started, cancellationToken);

        StringBuilder? builder = null;
        ReplacementLineEndings? endings = null;
        var count = 0;
        var previousEnd = 0;
        if (expandGroups)
        {
            for (var match = matcher.Expression!.Match(text); match.Success; match = match.NextMatch())
            {
                var inserted = match.Result(replacement);
                Append(match.Index, match.Length, inserted);
                endings?.AppendReplacement(match, inserted);
            }
        }
        else
        {
            foreach (var match in matcher.Enumerate(text))
            {
                Append(match.Index, match.Length, replacement);
                endings?.AppendLiteralReplacement(match.Index, match.Length, replacement);
            }
        }

        if (builder is null) return new ReplaceAllResult(text, 0, originalEndings);
        builder.Append(text.AsSpan(previousEnd));
        endings?.AppendSource(previousEnd, text.Length);
        return new ReplaceAllResult(builder.ToString(), count, endings?.Result.ToImmutable());

        void Append(int index, int length, string inserted)
        {
            CheckOperation();
            builder ??= new StringBuilder(text.Length);
            if (originalEndings is not null)
                endings ??= new ReplacementLineEndings(text, originalEndings, matcher.Expression, replacement, expandGroups, CheckOperation);
            builder.Append(text.AsSpan(previousEnd, index - previousEnd));
            builder.Append(inserted);
            endings?.AppendSource(previousEnd, index);
            previousEnd = index + length;
            count++;
        }

        void CheckOperation()
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckBudget(started);
        }
    }

    private static ReplaceAllResult ReplaceOrdinal(string text, TextMatcher matcher, string replacement, long started,
        CancellationToken token)
    {
        var count = 0;
        foreach (var _ in matcher.Enumerate(text))
        {
            if ((count++ & 63) != 0) continue;
            token.ThrowIfCancellationRequested();
            CheckBudget(started);
        }
        token.ThrowIfCancellationRequested();
        if (count == 0) return new(text, 0);

        var length = checked((int)(text.Length + (long)count * (replacement.Length - matcher.Literal!.Length)));
        var result = string.Create(length, (text, matcher, replacement, started, token), static (destination, state) =>
        {
            var previousEnd = 0;
            var written = 0;
            foreach (var match in state.matcher.Enumerate(state.text))
            {
                if ((written++ & 63) == 0)
                {
                    state.token.ThrowIfCancellationRequested();
                    CheckBudget(state.started);
                }
                var unchanged = state.text.AsSpan(previousEnd, match.Index - previousEnd);
                unchanged.CopyTo(destination);
                state.replacement.AsSpan().CopyTo(destination[unchanged.Length..]);
                destination = destination[(unchanged.Length + state.replacement.Length)..];
                previousEnd = match.Index + match.Length;
            }
            state.text.AsSpan(previousEnd).CopyTo(destination);
        });
        token.ThrowIfCancellationRequested();
        CheckBudget(started);
        return new(result, count);
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

    internal static bool IsCfgPath(string? path) =>
        string.Equals(Path.GetExtension(path), ".cfg", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeJson(string text, string? path)
    {
        if (IsCfgPath(path)) return false;
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
        var utf8Length = CheckJsonSize(input);
        var start = text.Length - input.Length;
        if (!UsesJsonLines(input, path))
        {
            return ValidateJsonValue(input, start, 1, cancellationToken, utf8Length);
        }

        for (var line = 1; !input.IsEmpty; line++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var consumed = ReadJsonLine(input, out var record);
            if (ValidateJsonValue(record, start, line, cancellationToken) is { } problem)
            {
                return problem;
            }

            input = input[consumed..];
            start += consumed;
        }

        return null;
    }

    private static JsonProblem? ValidateJsonValue(ReadOnlyMemory<char> input, int start, int lineNumber,
        CancellationToken cancellationToken = default, int utf8Length = -1)
    {
        try
        {
            JsonFormatting.Validate(input.Span, utf8Length, cancellationToken);
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
        var utf8Length = CheckJsonSize(input);
        var jsonLines = UsesJsonLines(containingText is null ? input : JsonInput(containingText), path);
        if (!jsonLines && JsonFormatting.TryFormat(input.Span, utf8Length, cancellationToken) is { } formatted)
            return formatted;

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

    internal static bool IsJsonLinesPath(string? path)
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

    private static int CheckJsonSize(ReadOnlyMemory<char> input)
    {
        var bytes = Encoding.UTF8.GetByteCount(input.Span);
        if (bytes > JsonByteLimit)
        {
            throw new InvalidDataException("JSON превышает 64 МиБ.");
        }
        return bytes;
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
