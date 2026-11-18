using System.Text;
using RustText.Demo;
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

    private static byte[] Encode(string text, TextEncodingKind encoding, bool preamble)
    {
        Encoding codec = encoding switch
        {
            TextEncodingKind.Utf16LittleEndian => new UnicodeEncoding(false, preamble),
            TextEncodingKind.Utf16BigEndian => new UnicodeEncoding(true, preamble),
            TextEncodingKind.Utf8Bom => new UTF8Encoding(preamble),
            _ => new UTF8Encoding(false)
        };
        var bytes = codec.GetBytes(text);
        var preambleBytes = preamble ? codec.GetPreamble() : Array.Empty<byte>();
        return preambleBytes.Concat(bytes).ToArray();
    }
}
