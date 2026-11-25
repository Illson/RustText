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

        Assert.Equal((true, (string?)null), await ConsoleCapture.CopyFrameAsync(new StringReader("Я"), writer, 1, 7, default));
        Assert.Equal((false, (string?)null), await ConsoleCapture.CopyFrameAsync(new StringReader("a\nb"), writer, 2, 7, default));
        Assert.Equal("Я\r\na\r\n", Encoding.UTF8.GetString(bytes.ToArray()));
        Assert.Equal(7, bytes.Length);
    }

    [Theory]
    [InlineData("", "tail\n[ERR] failed", "full-widthtail\r\n[ERR] failed\r\n")]
    [InlineData(" ", "tail\n[ERR] failed", "full-width tail\r\n[ERR] failed\r\n")]
    [InlineData(" ", "[ERR] failed", "full-width\r\n[ERR] failed\r\n")]
    [InlineData(" ", "{\"msg\":\"next\",\"level\":\"error\"}", "full-width\r\n{\"msg\":\"next\",\"level\":\"error\"}\r\n")]
    [InlineData(" ", "<11>second", "full-width\r\n<11>second\r\n")]
    [InlineData(" ", "level=warn next", "full-width\r\nlevel=warn next\r\n")]
    [InlineData(" ", "details level=error", "full-width details level=error\r\n")]
    [InlineData(" ", "<192>invalid", "full-width <192>invalid\r\n")]
    [InlineData(" ", "\n[ERR] failed", "full-width\r\n[ERR] failed\r\n")]
    public async Task CapturePreservesConsoleRecordBoundaries(string padding, string followingRows, string expected)
    {
        using var bytes = new MemoryStream();
        await using var writer = new StreamWriter(bytes, new UTF8Encoding(false)) { NewLine = "\r\n" };

        var (_, pendingJoin) = await ConsoleCapture.CopyFrameAsync(
            new StringReader($"full-width{padding}\0"), writer, 1, 1024, default);
        Assert.Equal(padding, pendingJoin);
        Assert.Equal((true, (string?)null), await ConsoleCapture.CopyFrameAsync(new StringReader(followingRows), writer,
            followingRows.Contains('\n') ? 2 : 1, 1024, default, pendingJoin));
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
