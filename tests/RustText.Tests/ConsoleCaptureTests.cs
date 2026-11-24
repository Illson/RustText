using System.Text;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class ConsoleCaptureTests
{
    [Fact]
    public async Task CaptureStopsAtTheByteLimitWithoutWritingAPartialRow()
    {
        using var bytes = new MemoryStream();
        await using var writer = new StreamWriter(bytes, new UTF8Encoding(false)) { NewLine = "\r\n" };

        Assert.Equal((true, false), await ConsoleCapture.CopyFrameAsync(new StringReader("Я"), writer, 1, 7, default));
        Assert.Equal((false, false), await ConsoleCapture.CopyFrameAsync(new StringReader("a\nb"), writer, 2, 7, default));
        Assert.Equal("Я\r\na\r\n", Encoding.UTF8.GetString(bytes.ToArray()));
        Assert.Equal(7, bytes.Length);
    }

    [Theory]
    [InlineData("tail\n[ERR] failed", "full-widthtail\r\n[ERR] failed\r\n")]
    [InlineData("[ERR] failed", "full-width\r\n[ERR] failed\r\n")]
    [InlineData("\n[ERR] failed", "full-width\r\n[ERR] failed\r\n")]
    public async Task CapturePreservesConsoleRecordBoundaries(string followingRows, string expected)
    {
        using var bytes = new MemoryStream();
        await using var writer = new StreamWriter(bytes, new UTF8Encoding(false)) { NewLine = "\r\n" };

        var (_, openLine) = await ConsoleCapture.CopyFrameAsync(new StringReader("full-width\0"), writer, 1, 1024, default);
        Assert.True(openLine);
        Assert.Equal((true, false), await ConsoleCapture.CopyFrameAsync(new StringReader(followingRows), writer,
            followingRows.Contains('\n') ? 2 : 1, 1024, default, openLine));
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
