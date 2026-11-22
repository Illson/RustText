using System.Text;
using RustText.Demo;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

public sealed class LogTests
{
    [Theory]
    [InlineData(TextEncodingKind.Utf8, false)]
    [InlineData(TextEncodingKind.Utf8Bom, true)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true)]
    [InlineData(TextEncodingKind.Utf16BigEndian, true)]
    [InlineData(TextEncodingKind.Windows1251, false)]
    public void MinimapSamplesTheVisibleRangeInItsEncoding(TextEncodingKind encoding, bool bom)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "minimap.log");
        const string hidden = "hidden\r\n";
        var start = Encode(hidden, encoding, bom).Length;
        var end = Encode(hidden + "ЁяZ", encoding, bom).Length;
        File.WriteAllBytes(path, Encode(hidden + "ЁяZ\r" + new string('x', 100) + "\nQ", encoding, bom));
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, encoding);

        var widths = reader.ReadMinimapWidths(start, file.Length, default);
        Assert.Equal(32, widths.Length);
        Assert.Equal(3, widths[0]);
        Assert.Contains((byte)32, widths);
        Assert.Equal(1, widths[^1]);
        Assert.Equal(2, reader.ReadMinimapWidths(start + 1, end, default)[0]);
        Assert.Empty(reader.ReadMinimapWidths(end, end, default));
        Assert.Throws<OperationCanceledException>(() => reader.ReadMinimapWidths(start, end, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8, false)]
    [InlineData(TextEncodingKind.Utf8Bom, true)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true)]
    [InlineData(TextEncodingKind.Utf16BigEndian, true)]
    public void ReadPage_RecognizesLfCrLfAndBareCr(TextEncodingKind encoding, bool preamble)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "mixed.log");
        const string text = "lf-one\ncrlf-two\r\ncr-three\rlast";
        File.WriteAllBytes(path, Encode(text, encoding, preamble));

        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, encoding);
        var page = reader.ReadPage(0, pageSize: 10, maxFrameBytes: 64, maxPageBytes: 256);

        Assert.Equal(new[] { "lf-one", "crlf-two", "cr-three", "last" }, page.Rows.Select(row => row.Text));
        Assert.False(page.HasMore);
        Assert.Equal(file.Length, page.NextOffset);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8, false)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true)]
    [InlineData(TextEncodingKind.Utf16BigEndian, true)]
    public void ReadPage_ConsumesCrLfSplitAtFrameBoundary(TextEncodingKind encoding, bool preamble)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "split-crlf.log");
        var unit = encoding is TextEncodingKind.Utf16LittleEndian or TextEncodingKind.Utf16BigEndian ? 2 : 1;
        var prefix = "123456789012345"[..(16 / unit - 1)];
        const string text = "next";
        File.WriteAllBytes(path, Encode(prefix + "\r\n" + text + "\n", encoding, preamble));

        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, encoding);
        var page = reader.ReadPage(0, pageSize: 10, maxFrameBytes: 16, maxPageBytes: 64);
        var firstEnd = (preamble ? unit : 0) + prefix.Length * unit + 2 * unit;

        Assert.Equal(new[] { prefix, text }, page.Rows.Select(row => row.Text));
        Assert.Equal(firstEnd, page.Rows[0].EndOffset);
        Assert.Equal(firstEnd, page.Rows[1].StartOffset);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8, false, 16)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true, 32)]
    [InlineData(TextEncodingKind.Utf16BigEndian, true, 32)]
    public void BareCrKeepsNextCharacterWhenFrameIsFollowedByLf(TextEncodingKind encoding, bool bom, int frameBytes)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "bare-cr.log");
        var middle = "B" + new string('x', 13);
        File.WriteAllBytes(path, Encode("A\r" + middle + "\nTAIL", encoding, bom));

        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, encoding);
        var page = reader.ReadPage(0, maxFrameBytes: frameBytes);
        Assert.Equal(new[] { "A", middle, "TAIL" }, page.Rows.Select(row => row.Text));
        Assert.Equal(reader.PreambleLength + (bom ? 4 : 2), page.Rows[1].StartOffset);
    }

    [Fact]
    public void ReadPage_StopsAtEndAfterConsumingCrLfAcrossPageBoundary()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "page-boundary.log");
        const int pageBytes = 32;
        var prefix = new string('a', pageBytes - 1);
        const string tail = "tail";
        File.WriteAllText(path, prefix + "\r\n" + tail, new UTF8Encoding(false));

        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);
        var page = reader.ReadPage(0, maxFrameBytes: pageBytes, maxPageBytes: pageBytes);
        var last = reader.ReadPage(page.NextOffset, maxFrameBytes: pageBytes, maxPageBytes: pageBytes);
        var eof = reader.ReadPage(last.NextOffset);

        Assert.Equal(prefix, Assert.Single(page.Rows).Text);
        Assert.Equal((long)pageBytes + 1, page.NextOffset);
        Assert.True(page.HasMore);
        Assert.Equal(tail, Assert.Single(last.Rows).Text);
        Assert.Equal(file.Length, last.NextOffset);
        Assert.False(last.HasMore);
        Assert.Empty(eof.Rows);
        Assert.Equal(file.Length, eof.NextOffset);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8, false)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true)]
    [InlineData(TextEncodingKind.Utf16BigEndian, true)]
    [InlineData(TextEncodingKind.Windows1251, false)]
    public void SequentialScanPreservesRowsAcrossPageAndBufferBoundaries(TextEncodingKind encoding, bool bom)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "sequential.log");
        var shortLines = string.Concat(Enumerable.Repeat("Юг > лог\r\n", 9_000));
        var longLine = new string('я', 132_000);
        File.WriteAllBytes(path, Encode(shortLines + longLine + "\r\nпоследняя", encoding, bom));
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, encoding);
        using var scan = reader.BeginSequentialScan();

        long offset = 0;
        var pages = 0;
        while (offset < file.Length)
        {
            var expected = reader.ReadPage(offset);
            var actual = scan.ReadPage(offset);
            Assert.Equal(expected.Rows.AsEnumerable(), actual.Rows);
            Assert.Equal(expected.NextOffset, actual.NextOffset);
            Assert.Equal(expected.HasMore, actual.HasMore);
            Assert.True(actual.NextOffset > offset);
            offset = actual.NextOffset;
            pages++;
        }

        Assert.True(pages > 2);
        Assert.Equal(file.Length, offset);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8, false)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true)]
    [InlineData(TextEncodingKind.Utf16BigEndian, true)]
    public void PageStartsWithinLineIsCorrectForOrdinaryAndSequentialReads(TextEncodingKind encoding, bool bom)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "page-starts.log");
        var shortLines = string.Concat(Enumerable.Range(0, 81).Select(index => $"row-{index}\n"));
        var longLine = new string('x', 20 * 1024) + "\nafter-long-line\n";
        File.WriteAllBytes(path, Encode(shortLines + longLine, encoding, bom));

        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, encoding);
        using var scan = reader.BeginSequentialScan();

        var firstPage = reader.ReadPage(0);
        var ordinaryNext = reader.ReadPage(firstPage.NextOffset);
        var scanFirstPage = scan.ReadPage(0);
        var scanNext = scan.ReadPage(scanFirstPage.NextOffset);

        Assert.Equal(80, firstPage.Rows.Count);
        Assert.False(ordinaryNext.Rows[0].StartsWithinLine);
        Assert.False(scanNext.Rows[0].StartsWithinLine);

        var longLineOffset = Encode(shortLines, encoding, bom).Length;
        var ordinaryLongPage = reader.ReadPage(longLineOffset, maxPageBytes: 16 * 1024);
        var ordinaryContinuation = reader.ReadPage(ordinaryLongPage.NextOffset, maxPageBytes: 16 * 1024);
        using var shortScan = reader.BeginSequentialScan(16 * 1024);
        var scanLongPage = shortScan.ReadPage(longLineOffset);
        var scanContinuation = shortScan.ReadPage(scanLongPage.NextOffset);

        Assert.True(ordinaryLongPage.Rows[0].IsTruncated);
        Assert.True(ordinaryContinuation.Rows[0].StartsWithinLine);
        Assert.True(scanLongPage.Rows[0].IsTruncated);
        Assert.True(scanContinuation.Rows[0].StartsWithinLine);
    }

    [Fact]
    public void SequentialScanReusesCursorAfterCancellationAndRereadsTruncatedFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "truncated.log");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("previous\n", 600)));
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);
        using var scan = reader.BeginSequentialScan();

        var first = scan.ReadPage(0);
        Assert.True(first.HasMore);
        Assert.Throws<OperationCanceledException>(() => scan.ReadPage(first.NextOffset, new CancellationToken(true)));
        Assert.Equal("previous", scan.ReadPage(first.NextOffset).Rows[0].Text);

        using (var writer = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write("replacement"u8);
        }
        var replacement = scan.ReadPage(0);
        Assert.Equal("replacement", Assert.Single(replacement.Rows).Text);
        Assert.Equal(file.Length, replacement.NextOffset);
        Assert.False(replacement.HasMore);
    }

    [Fact]
    public void GetEndOffset_1000CrDelimitedRowsReturnsLastEighty()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "thousand-cr.log");
        File.WriteAllText(path, string.Join('\r', Enumerable.Range(1, 1000).Select(value => $"row {value}")) + "\r", new UTF8Encoding(false));

        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);
        var page = reader.ReadPage(reader.GetEndOffset(), pageSize: 80);

        Assert.Equal(80, page.Rows.Count);
        Assert.Equal("row 921", page.Rows[0].Text);
        Assert.Equal("row 1000", page.Rows[^1].Text);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8, false, 1)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true, 2)]
    [InlineData(TextEncodingKind.Utf16BigEndian, true, 2)]
    public void SparseScanCountsCrLfAcrossBlocksAndCanResume(TextEncodingKind encoding, bool preamble, int unit)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "index.log");
        const int block = 256 * 1024;
        File.WriteAllBytes(path, Encode(new string('a', block / unit - 1) + "\r\nsecond\rthird\nfourth", encoding, preamble));
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, encoding);
        var points = new List<LogLineCheckpoint>();

        var complete = reader.ScanLineBreaks(default, file.Length, points.Add, CancellationToken.None);
        Assert.Equal(file.Length, complete.Offset);
        Assert.Equal(3L, complete.Lines);
        var first = points[0];
        Assert.Equal(reader.PreambleLength + block, first.Offset);
        Assert.Equal(1L, first.Lines);
        Assert.Equal(complete, reader.ScanLineBreaks(first, file.Length, null, CancellationToken.None));
    }

    [Fact]
    public void SparseScanHandlesDenseMixedSeparatorsAcrossVectorLanesAndPartialCheckpoints()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "dense-index.log");
        var prefix = new string('x', 31) + "\r\n" + new string('y', 30) + "\r\n";
        var text = prefix + string.Concat(Enumerable.Repeat("a\r\r\n\nb\nc\r", 300)) + "tail";
        File.WriteAllText(path, text);
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);

        foreach (var offset in new[] { 31, 32, 33, 63, 64, 65, text.Length - 1 })
        {
            var head = reader.ScanLineBreaks(default, offset, null, default);
            var whole = reader.ScanLineBreaks(head, file.Length, null, default);
            Assert.Equal(new(file.Length, 2 + 300 * 5), whole);
        }
    }

    [Fact]
    public void SearchSkipsOnlyCompleteLinesAndKeepsNumberAndMatchOffsetsAtWindowBoundary()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "search-window.log");
        var prefix = "first\rlf\n" + new string('x', LogDataReader.DefaultPageBytes - "first\rlf\n".Length - 1) + "\r\n";
        const string target = "😀needle needle\r";
        File.WriteAllText(path, prefix + target + "needle at tail");
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);
        var options = new SearchOptions("needle", false, true, false);

        var batch = reader.Search(options, new(0, 0), file.Length, 1, 1, long.MaxValue, default);
        var hit = Assert.Single(batch.Hits);
        Assert.Equal(prefix.Length, hit.Offset);
        Assert.Equal(4L, hit.LineNumber);
        Assert.Equal(2, hit.MatchIndex);
        Assert.Equal(Encoding.UTF8.GetByteCount(target), hit.LineLengthBytes);
        Assert.False(hit.WasIncomplete);
        Assert.Equal(new LogSearchCursor(prefix.Length, 9), batch.NextCursor);

        var continuation = reader.Search(options, batch.NextCursor!, file.Length, hit.LineNumber, 32, long.MaxValue, default);
        Assert.Equal([9, 0], continuation.Hits.Select(item => item.MatchIndex));
        Assert.Equal([4L, 5L], continuation.Hits.Select(item => item.LineNumber));
        Assert.True(continuation.Hits[^1].WasIncomplete);
        Assert.False(continuation.HasMore);
    }

    [Theory]
    [InlineData(@"^\d+.*MARKER", true, "123 MARKER", 0, 10)]
    [InlineData(@"foo?bar", true, "fobar", 0, 5)]
    [InlineData(@"(unneeded|target)", true, "a target", 2, 6)]
    [InlineData(@"(?i)MARKER", true, "marker", 0, 6)]
    [InlineData(@"(?x)M A R K E R", true, "MARKER", 0, 6)]
    [InlineData(@"\x61nd", true, "and", 0, 3)]
    [InlineData("K", false, "K", 0, 1)]
    public void SearchPrefilterDoesNotChangeRegexOrInvariantCaseMatching(string query, bool matchCase, string text, int index, int length)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "prefilter.log");
        var prefix = string.Concat(Enumerable.Repeat("ignore\r\n", 20_000));
        File.WriteAllText(path, prefix + text + "\n");
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);
        var options = new SearchOptions(query, query != "K", matchCase, false);

        var result = reader.Search(options, new(0, 0), file.Length, 1, 32, long.MaxValue, default);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(prefix.Length, hit.Offset);
        Assert.Equal(20_001, hit.LineNumber);
        Assert.Equal(index, hit.MatchIndex);
        Assert.Equal(length, hit.MatchLength);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8Bom)]
    [InlineData(TextEncodingKind.Utf16LittleEndian)]
    [InlineData(TextEncodingKind.Utf16BigEndian)]
    [InlineData(TextEncodingKind.Windows1251)]
    public void SearchUsesByteOffsetsAcrossEncodingsAndKeepsPartialLineWhenResuming(TextEncodingKind encoding)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "encoded-search.log");
        const string prefix = "первая\r\n";
        const string target = "Ёжик пришёл; Ёжик остался\r";
        var bom = encoding != TextEncodingKind.Windows1251;
        var offset = Encode(prefix, encoding, bom).Length;
        File.WriteAllBytes(path, Encode(prefix + target + "хвост", encoding, bom));
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, encoding);
        var options = new SearchOptions("Ёжик", false, true, true);

        var first = reader.Search(options, new(offset, 0), file.Length, 2, 32, 8, default);
        Assert.Empty(first.Hits);
        Assert.True(first.HasMore);
        Assert.NotEmpty(first.NextCursor!.PendingText);
        var rest = reader.Search(options, first.NextCursor, file.Length, 2, 32, long.MaxValue, default);

        Assert.Equal([0, 13], rest.Hits.Select(hit => hit.MatchIndex));
        Assert.All(rest.Hits, hit =>
        {
            Assert.Equal(offset, hit.Offset);
            Assert.Equal(2L, hit.LineNumber);
            Assert.Equal(Encode(target, encoding, false).Length, hit.LineLengthBytes);
        });
        Assert.False(rest.HasMore);
    }

    [Fact]
    public void SearchReplacementCharacterMatchesInvalidUtf8WithoutSearchingPastSnapshot()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "invalid-sequence.log");
        File.WriteAllBytes(path, [(byte)'a', 0xff, (byte)'\n', 0xff]);
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf8);
        var options = new SearchOptions("\uFFFD", false, true, false);
        var result = reader.Search(options, new(0, 0), 3, 1, 32, long.MaxValue, default);
        Assert.Equal(1, Assert.Single(result.Hits).MatchIndex);
        Assert.Throws<OperationCanceledException>(() => reader.Search(options, new(0, 0), 3, 1, 32,
            long.MaxValue, new CancellationToken(true)));
    }

    private static byte[] Encode(string text, TextEncodingKind encoding, bool preamble)
    {
        if (encoding == TextEncodingKind.Windows1251) Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding codec = encoding switch
        {
            TextEncodingKind.Utf16LittleEndian => new UnicodeEncoding(false, preamble),
            TextEncodingKind.Utf16BigEndian => new UnicodeEncoding(true, preamble),
            TextEncodingKind.Utf8Bom => new UTF8Encoding(preamble),
            TextEncodingKind.Windows1251 => Encoding.GetEncoding(1251),
            _ => new UTF8Encoding(false)
        };
        var bytes = codec.GetBytes(text);
        var preambleBytes = preamble ? codec.GetPreamble() : Array.Empty<byte>();
        return preambleBytes.Concat(bytes).ToArray();
    }
}
