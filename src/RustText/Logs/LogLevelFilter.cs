namespace RustText.Demo.Logs;

internal readonly record struct FilterCursor(long Offset, LogLevel ContinuedLevel = LogLevel.Other,
    long? SourceLine = null, long RelativeLine = 0);
internal readonly record struct FilterRow(string Text, LogLevel Level, bool Continuation, long? SourceLine,
    long RelativeLine, long StartOffset, long EndOffset);
internal sealed record FilterPage(List<FilterRow> Rows, FilterCursor? Next, FilterCursor Tail = default);

internal static class LogLevelFilter
{
    internal static FilterPage Read(Func<long, CancellationToken, IEnumerable<LogRow>> read,
        FilterCursor start, LogLevel levels, CancellationToken token, List<FilterRow>? prefix = null)
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
            if (rows.Count >= 100 || characters >= LogDataReader.DefaultPageBytes)
                return new(rows, tail);
            rows.Add(new FilterRow(row.Text, current, row.StartsWithinLine, sourceLine, relativeLine,
                row.StartOffset, row.EndOffset));
            characters += row.Text.Length;
        }
        return new(rows, null, tail);
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
