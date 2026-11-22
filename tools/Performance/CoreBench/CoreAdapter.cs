using System.Text;
using RustText.Demo;
using RustText.Demo.Editor;
using RustText.Demo.Logs;

namespace RustText.Performance;

public static class CoreAdapter
{
    public static (long End, long Lines) Index(string path)
    {
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);
        var checkpoint = reader.ScanLineBreaks(new LogLineCheckpoint(0, 0), file.Length, null, CancellationToken.None);
        return (checkpoint.Offset, checkpoint.Lines);
    }

    // The fixtures have at most one match per line for each query. Use the
    // production scanner where available, without creating a LogView or index.
    public static (long[] Offsets, long End, bool Complete) Search(string path, string pattern, bool regex)
    {
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);
        var options = new SearchOptions(pattern, regex, true, false);
        long length = file.Length;
#if BUFFERED_LOG_SEARCH
        var result = reader.Search(options, new LogSearchCursor(0, 0), length, 1, 32, long.MaxValue, CancellationToken.None);
        if (result.Message is not null) throw new InvalidDataException(result.Message);
        return (result.Hits.Select(hit => hit.Offset).ToArray(), result.NextCursor?.Offset ?? length, !result.HasMore);
#else
        using var scan = reader.BeginSequentialScan();
        var matcher = TextOperations.BuildRegex(options);
        var offsets = new List<long>();
        var pending = new StringBuilder();
        long start = 0;
        long cursor = 0;
        while (cursor < length)
        {
            var page = scan.ReadPage(cursor);
            if (page.NextOffset <= cursor) throw new InvalidDataException("Log search did not advance.");
            foreach (var row in page.Rows)
            {
                if (!row.StartsWithinLine)
                {
                    start = row.StartOffset;
                    pending.Clear();
                }
                if (row.IsTruncated)
                {
                    pending.Append(row.Text);
                    continue;
                }
                string line = pending.Length == 0 ? row.Text : pending.Append(row.Text).ToString();
                if (matcher.IsMatch(line))
                {
                    offsets.Add(start);
                    if (offsets.Count == 32) return (offsets.ToArray(), row.EndOffset, row.EndOffset == length);
                }
                pending.Clear();
            }
            cursor = page.NextOffset;
        }
        return (offsets.ToArray(), cursor, cursor == length);
#endif
    }

    public static string FormatJson(string json) => TextOperations.FormatJson(json, "data.json");

    public static ((int Offset, int Length)[] Ranges, int Count, bool TimedOut) Find(string text, string query, int limit)
    {
        var matcher = CreateEditorMatcher(query);
        var result = TextOperations.Find(text, matcher, limit, CancellationToken.None);
        return (result.Hits.Select(hit => (hit.Offset, hit.Length)).ToArray(), result.Count, result.TimedOut);
    }

    public static (string Text, int Count) Replace(string text, string query, string replacement)
    {
        var matcher = CreateEditorMatcher(query);
        var result = TextOperations.ReplaceAll(text, matcher, replacement, false);
        return (result.Text, result.Count);
    }

#if TEXT_MATCHER
    private static TextMatcher CreateEditorMatcher(string query) => new(new SearchOptions(query, false, true, false));
#else
    private static System.Text.RegularExpressions.Regex CreateEditorMatcher(string query) =>
        TextOperations.BuildRegex(new SearchOptions(query, false, true, false));
#endif
}
