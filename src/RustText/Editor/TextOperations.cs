using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustText.Demo.Editor;

internal sealed record SearchOptions(string Pattern, bool IsRegex, bool MatchCase, bool WholeWord);
internal sealed record SearchHit(int Offset, int Length, int Line, string Preview);
internal sealed record SearchSummary(IReadOnlyList<SearchHit> Hits, int Count, bool TimedOut, IReadOnlyList<CaretRange>? Carets);
internal sealed record JsonProblem(int Offset, int Line, int Column, string Message);
internal sealed record ListComparison(string Common, string LeftOnly, string RightOnly);

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

    public static (string Text, int Count) ReplaceAll(string text, Regex regex, string replacement, bool expandGroups)
    {
        var started = Stopwatch.GetTimestamp();
        var builder = new StringBuilder(text.Length);
        var count = 0;
        var previousEnd = 0;
        for (var match = regex.Match(text); match.Success; match = match.NextMatch())
        {
            CheckBudget(started);
            builder.Append(text.AsSpan(previousEnd, match.Index - previousEnd));
            builder.Append(expandGroups ? match.Result(replacement) : replacement);
            previousEnd = match.Index + match.Length;
            count++;
        }

        if (count == 0)
        {
            return (text, 0);
        }

        builder.Append(text.AsSpan(previousEnd));
        return (builder.ToString(), count);
    }

    private static void CheckBudget(long started)
    {
        if (Stopwatch.GetElapsedTime(started) >= SearchBudget)
        {
            throw new TimeoutException("Лимит времени операции истёк.");
        }
    }

    public static bool LooksLikeJson(string text, string? path) =>
        string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
        || text.AsSpan().TrimStart().TrimStart('\uFEFF').TrimStart() is ['{' or '[', ..];

    public static JsonProblem? ValidateJson(string text)
    {
        try
        {
            using var parsed = ParseJson(text);
            return null;
        }
        catch (JsonException exception)
        {
            var line = (int)(exception.LineNumber ?? 0);
            var bytes = exception.BytePositionInLine ?? 0;
            var start = text.StartsWith('\uFEFF') ? 1 : 0;
            var rest = text.AsSpan(start);
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

            return new JsonProblem(start + column, line + 1, column + 1, exception.Message);
        }
    }

    public static string FormatJson(string text)
    {
        using var document = ParseJson(text);
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 128
        }))
        {
            document.WriteTo(writer);
        }

        if (memory.Length > JsonByteLimit)
        {
            throw new InvalidDataException("Форматированный JSON превышает 64 МиБ.");
        }

        return Encoding.UTF8.GetString(memory.GetBuffer().AsSpan(0, (int)memory.Length));
    }

    private static JsonDocument ParseJson(string text)
    {
        var input = text.AsSpan().TrimStart('\uFEFF');
        if (Encoding.UTF8.GetByteCount(input) > JsonByteLimit)
        {
            throw new InvalidDataException("JSON превышает 64 МиБ.");
        }

        return JsonDocument.Parse(input.ToString(), new JsonDocumentOptions { MaxDepth = 128 });
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
