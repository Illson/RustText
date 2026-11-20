using Avalonia.Media;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

public sealed class WindowChromeTests
{
    [Theory]
    [InlineData(0x12, 0x34, 0x56, 0x00563412u)]
    [InlineData(0xf0, 0x0a, 0x80, 0x00800af0u)]
    public void BorderColorUsesWindowsColorRefChannelOrder(byte red, byte green, byte blue, uint expected)
    {
        var color = Color.FromRgb(red, green, blue);

        Assert.Equal(expected, WindowFrameColor.ToColorRef(color));
    }

    [Fact]
    public void BorderColorIgnoresAlpha()
    {
        var color = Color.FromArgb(0x7f, 0x12, 0x34, 0x56);

        Assert.Equal(0x00563412u, WindowFrameColor.ToColorRef(color));
    }

    [Fact]
    public void FrameColorTargetsOnlyTheThinOuterBorder()
    {
        Assert.Equal(34u, WindowFrameColor.DwmBorderColorAttribute);
    }
}
