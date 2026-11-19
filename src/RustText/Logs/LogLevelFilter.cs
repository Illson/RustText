namespace RustText.Demo.Logs;

internal readonly record struct FilterCursor(long Offset, LogLevel ContinuedLevel = LogLevel.Other);
internal sealed record FilterPage(List<(string Text, LogLevel Level, bool Continuation)> Rows, FilterCursor? Next);

internal static class LogLevelFilter
{
    internal static FilterPage Read(Func<long, CancellationToken, IEnumerable<LogRow>> read,
        FilterCursor start, LogLevel levels, CancellationToken token)
    {
        var rows = new List<(string, LogLevel, bool)>();
        var current = start.ContinuedLevel;
        var characters = 0;
        foreach (var row in read(start.Offset, token))
        {
            token.ThrowIfCancellationRequested();
            if (!row.StartsWithinLine) current = LogLevels.Classify(row.Text);
            if ((levels & current) == 0) continue;
            if (rows.Count >= 100 || characters >= LogDataReader.DefaultPageBytes)
                return new(rows, new FilterCursor(row.StartOffset, current));
            rows.Add((row.Text, current, row.StartsWithinLine));
            characters += row.Text.Length;
        }
        return new(rows, null);
    }

    internal static IEnumerable<LogRow> FileRows(LogDataReader reader, long offset, long length, CancellationToken token)
    {
        while (offset < length)
        {
            var page = reader.ReadPage(offset, maxPageBytes: 32 * 1024, cancellationToken: token, endOffset: length);
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
