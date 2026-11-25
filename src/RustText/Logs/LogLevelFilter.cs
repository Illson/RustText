namespace RustText.Demo.Logs;

internal readonly record struct FilterCursor(long Offset, LogLevel ContinuedLevel = LogLevel.Other,
    long? SourceLine = null, long RelativeLine = 0);
internal readonly record struct FilterRow(string Text, LogLevel Level, bool Continuation, long? SourceLine,
    long RelativeLine, long StartOffset, long EndOffset);
internal sealed record FilterPage(List<FilterRow> Rows, FilterCursor? Next, FilterCursor Tail = default);

internal static class LogLevelFilter
{
    internal const int PageRows = 100;

    internal static FilterPage Read(Func<long, CancellationToken, IEnumerable<LogRow>> read,
        FilterCursor start, LogLevel levels, CancellationToken token, List<FilterRow>? prefix = null,
        int maxRows = PageRows, int maxCharacters = LogDataReader.DefaultPageBytes)
    {
        var rows = prefix ?? [];
        var current = start.ContinuedLevel;
        var sourceLine = start.SourceLine;
        var relativeLine = start.RelativeLine;
        var isFirstRow = true;
        var characters = prefix?.Sum(row => row.Text.Length) ?? 0;
        var tail = start;
        foreach (var row in read(start.Offset, token))
        {
            token.ThrowIfCancellationRequested();
            if (!isFirstRow && !row.StartsWithinLine)
            {
                relativeLine++;
                if (sourceLine is not null) sourceLine++;
            }
            isFirstRow = false;
            if (!row.StartsWithinLine) current = LogLevels.Classify(row.Text);
            tail = new FilterCursor(row.StartOffset, current, sourceLine, relativeLine);
            if ((levels & current) == 0) continue;
            if (rows.Count >= maxRows || characters >= maxCharacters)
                return new(rows, tail);
            rows.Add(new FilterRow(row.Text, current, row.StartsWithinLine, sourceLine, relativeLine,
                row.StartOffset, row.EndOffset));
            characters += row.Text.Length;
        }
        return new(rows, null, tail);
    }

    internal static List<FilterRow> ReadBefore(LogDataReader reader, long lower, FilterCursor end,
        LogLevel levels, CancellationToken token)
    {
        var recent = new Queue<FilterRow>();
        while (end.Offset > lower)
        {
            var offset = Math.Max(lower, reader.GetPreviousPageOffset(end.Offset, token, fullBlock: true));
            if (offset >= end.Offset) break;
            // GetPreviousPageOffset can land inside a long record. Read its header before filtering any continuation.
            while (offset > lower && offset > reader.PreambleLength && !reader.IsNewlineBefore(offset))
            {
                var previous = Math.Max(lower, reader.GetPreviousPageOffset(offset, token, fullBlock: true));
                if (previous >= offset) return [];
                offset = previous;
            }

            IEnumerable<LogRow> Rows(long from, CancellationToken ct) => FileRows(reader, from, end.Offset, ct);
            var characters = 0;
            var cursor = new FilterCursor(offset);
            FilterPage chunk;
            while (true)
            {
                chunk = Read(Rows, cursor, levels, token);
                foreach (var row in chunk.Rows)
                {
                    while (recent.Count >= PageRows || (recent.Count != 0
                        && characters + row.Text.Length > LogDataReader.DefaultPageBytes))
                        characters -= recent.Dequeue().Text.Length;
                    recent.Enqueue(row);
                    characters += row.Text.Length;
                }
                if (chunk.Next is not { } next) break;
                cursor = next;
            }
            var distance = chunk.Tail.RelativeLine + (reader.IsNewlineBefore(end.Offset) ? 1 : 0);
            var relativeStart = end.RelativeLine - distance;
            long? sourceStart = end.SourceLine - distance;
            if (recent.Count > 0)
                return recent.Select(row => row with
                {
                    RelativeLine = relativeStart + row.RelativeLine,
                    SourceLine = sourceStart + row.RelativeLine
                }).ToList();
            end = new FilterCursor(offset, SourceLine: sourceStart, RelativeLine: relativeStart);
        }
        return [];
    }

    internal static IEnumerable<LogRow> FileRows(LogDataReader reader, long offset, long length, CancellationToken token)
    {
        using var scan = reader.BeginSequentialScan(32 * 1024);
        while (offset < length)
        {
            var page = scan.ReadPage(offset, token, endOffset: length);
            foreach (var row in page.Rows)
            {
                if (row.StartOffset >= length) yield break;
                yield return row;
            }
            if (!page.HasMore || page.NextOffset <= offset) yield break;
            offset = page.NextOffset;
        }
    }

    internal static IEnumerable<LogRow> EditorRows(string text, long offset, CancellationToken token)
    {
        var start = checked((int)offset);
        while (start < text.Length)
        {
            token.ThrowIfCancellationRequested();
            var length = text.AsSpan(start).IndexOfAny('\r', '\n');
            var end = length < 0 ? text.Length : start + length;
            var next = end;
            if (next < text.Length && text[next] == '\r') next++;
            if (next < text.Length && text[next] == '\n') next++;
            yield return new LogRow(start, next, text[start..end], false, false);
            start = next;
        }
    }
}
