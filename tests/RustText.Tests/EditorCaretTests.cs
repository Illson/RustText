using Avalonia;
using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

public sealed class EditorCaretTests
{
    [Fact]
    public void BlinkHoldsBetweenSoftFadesAndStartsBrightAfterActivity()
    {
        const double interval = 530;
        Assert.Equal((1, 410), EditorCaret.BlinkFrame(0, interval));
        Assert.Equal(0.56, EditorCaret.BlinkFrame(450, interval).Opacity, precision: 6);
        Assert.Equal((0.12, 410), EditorCaret.BlinkFrame(530, interval));
        Assert.Equal(0.56, EditorCaret.BlinkFrame(980, interval).Opacity, precision: 6);
        Assert.Equal((1, 410), EditorCaret.BlinkFrame(1060, interval));
        Assert.Equal((1, 80), EditorCaret.BlinkFrame(450, interval, smooth: false));
        Assert.Equal((0, 530), EditorCaret.BlinkFrame(530, interval, smooth: false));
    }

    [Fact]
    public void TypingGlidesOnlyBetweenNearbyPositionsOnTheSameVisualRow()
    {
        var start = new Rect(20, 10, 0.5, 16);
        Assert.True(EditorCaret.CanGlide(start, start.WithX(28)));
        Assert.False(EditorCaret.CanGlide(start, start));
        Assert.False(EditorCaret.CanGlide(start, start.WithX(90)));
        Assert.False(EditorCaret.CanGlide(start, start.WithY(26)));
        Assert.False(EditorCaret.CanGlide(default, start));
    }
}
