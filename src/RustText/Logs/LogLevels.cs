using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Logs;

[Flags]
internal enum LogLevel
{
    Other = 1, Trace = 2, Debug = 4, Info = 8, Warning = 16, Error = 32, Fatal = 64,
    All = Other | Trace | Debug | Info | Warning | Error | Fatal
}

internal static partial class LogLevels
{
    // Prefer an explicit structured field over severity names in the message.
    [GeneratedRegex("""(?:^|[\s{,|])["']?(?:@l|level|loglevel|log_level|severity|severityText|log\.level)["']?\s*[:=]\s*["']?(?<level>[A-Za-z]+)\b""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StructuredLevel();

    [GeneratedRegex("""(?:^\s*(?:(?:\d{4}[-/.]\d\d[-/.]\d\d[T ]+)?\d\d:\d\d:\d\d[\d.,Z:+-]*\s+)?(?:\[[^\]\r\n]{1,64}\]\s*){0,4}|\[\s*(?:\d\d:\d\d:\d\d[\d.,]*\s+)?|\|\s*)(?<level>trc|trce|trace|vrb|verbose|dbg|dbug|debug|inf|info|information|notice|wrn|warn|warning|err|error|fail|ftl|fatal|crit|critical|panic)(?=$|[\s\]|):,;-])""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLevel();

    [GeneratedRegex(@"^\s*\[?\d{4}[-/.]\d\d[-/.]\d\d[T ]\d\d:\d\d:\d\d\b", RegexOptions.CultureInvariant)]
    private static partial Regex DatedHeader();

    internal static bool IsLogPath(string? path) =>
        string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase);

    internal static bool StartsRecord(string text) => DatedHeader().IsMatch(text)
        || HeaderLevel().Match(text, 0, Math.Min(text.Length, 256)) is { Success: true, Index: 0 };

    internal static bool HasLogRecords(string path, TextEncodingKind? encoding, CancellationToken token)
    {
        const int sampleBytes = 16 * 1024;
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, LogDataReader.DetectEncoding(file, encoding));
        var sample = reader.ReadPage(0, pageSize: 32, maxFrameBytes: sampleBytes, maxPageBytes: sampleBytes, cancellationToken: token);
        if (sample.Rows.Any(row => row.Text.Contains('\0'))) return false;

        var nonEmpty = 0;
        var recognized = 0;
        foreach (var row in sample.Rows)
        {
            if (row.ContinuesLine || string.IsNullOrWhiteSpace(row.Text)) continue;
            nonEmpty++;
            var text = row.Text.TrimStart();
            var level = Classify(text);
            if (level != LogLevel.Other)
            {
                // An explicit severity prefix can identify a log after its first record.
                var prefixEnd = text[0] switch
                {
                    '[' => text.IndexOf(']', 0, Math.Min(32, text.Length)),
                    '<' => text.IndexOf('>', 0, Math.Min(5, text.Length)),
                    _ => -1
                };
                if (prefixEnd > 0 && Classify(text[..(prefixEnd + 1)]) != LogLevel.Other)
                    return true;
            }
            if (level != LogLevel.Other || DatedHeader().IsMatch(text)) recognized++;
        }

        return recognized >= 2 && recognized * 2 >= nonEmpty;
    }

    internal static LogLevel Classify(string text)
    {
        if (text.Length == 0) return LogLevel.Other;
        var close = text[0] == '<' ? text.IndexOf('>', 1, Math.Min(4, text.Length - 1)) : -1;
        if (close is >= 2 and <= 4
            && uint.TryParse(text.AsSpan(1, close - 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var priority) && priority <= 191)
        {
            return (priority % 8) switch
            {
                <= 2 => LogLevel.Fatal, 3 => LogLevel.Error, 4 => LogLevel.Warning, 5 or 6 => LogLevel.Info, _ => LogLevel.Debug
            };
        }
        var end = Math.Min(1024, text.Length);
        var field = StructuredLevel().Match(text, 0, end);
        if (!field.Success)
        {
            if (text.AsSpan().TrimStart().StartsWith("{")) return LogLevel.Other;
            field = HeaderLevel().Match(text, 0, Math.Min(end, 256));
        }
        if (!field.Success) return LogLevel.Other;
        return field.Groups["level"].Value.ToUpperInvariant() switch
        {
            "TRC" or "TRCE" or "TRACE" or "VRB" or "VERBOSE" => LogLevel.Trace,
            "DBG" or "DBUG" or "DEBUG" => LogLevel.Debug,
            "INF" or "INFO" or "INFORMATION" or "NOTICE" => LogLevel.Info,
            "WRN" or "WARN" or "WARNING" => LogLevel.Warning,
            "ERR" or "ERROR" or "FAIL" => LogLevel.Error,
            "FTL" or "FATAL" or "CRIT" or "CRITICAL" or "PANIC" => LogLevel.Fatal,
            _ => LogLevel.Other
        };
    }

    internal static IBrush Brush(LogLevel level) => level switch
    {
        LogLevel.Error or LogLevel.Fatal => FocusTheme.Danger,
        LogLevel.Warning => FocusTheme.Warning,
        LogLevel.Info => FocusTheme.Success,
        LogLevel.Debug or LogLevel.Trace => FocusTheme.Muted,
        _ => FocusTheme.Text
    };
}

internal sealed class LogLevelColorizer : DocumentColorizingTransformer, IBackgroundRenderer
{
    public bool Enabled { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView view, DrawingContext drawingContext)
    {
        if (!Enabled || !view.VisualLinesValid || view.Document is not { } document) return;
        using var tint = drawingContext.PushOpacity(0.16);
        foreach (var visual in view.VisualLines)
        {
            var level = Classify(document, visual.FirstDocumentLine);
            if (level == LogLevel.Other) continue;
            var bounds = new Rect(0, visual.VisualTop - view.VerticalOffset, view.Bounds.Width, visual.Height);
            drawingContext.FillRectangle(LogLevels.Brush(level), bounds);
        }
    }

    private static LogLevel Classify(TextDocument document, DocumentLine line) =>
        LogLevels.Classify(document.GetText(line.Offset, Math.Min(line.Length, 1024)));

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!Enabled || line.Length == 0) return;
        var level = Classify(CurrentContext.Document, line);
        if (level != LogLevel.Other)
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(LogLevels.Brush(level)));
    }
}
