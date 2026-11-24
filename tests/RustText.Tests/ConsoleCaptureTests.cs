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

        Assert.True(await ConsoleCapture.CopyFrameAsync(new StringReader("Я"), writer, 1, 7, default));
        Assert.False(await ConsoleCapture.CopyFrameAsync(new StringReader("a\nb"), writer, 2, 7, default));
        Assert.Equal("Я\r\na\r\n", Encoding.UTF8.GetString(bytes.ToArray()));
        Assert.Equal(7, bytes.Length);
    }
}
