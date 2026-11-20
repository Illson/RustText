using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

public sealed class JsonFoldRangesTests
{
    [Fact]
    public void CreateReturnsMultilineRangesInOpeningOrder()
    {
        const string text = "{\r\n  \"values\": [\r\n    {\r\n      \"id\": 1\r\n    }\r\n  ]\r\n}";
        var innerStart = text.IndexOf('{', 1);
        var arrayStart = text.IndexOf('[');

        var foldings = JsonFoldRanges.Create(text, CancellationToken.None);

        Assert.Collection(foldings,
            folding => Assert.Equal((0, text.Length, "{…}"),
                (folding.StartOffset, folding.EndOffset, folding.Name)),
            folding => Assert.Equal((arrayStart, text.IndexOf(']', arrayStart) + 1, "[…]"),
                (folding.StartOffset, folding.EndOffset, folding.Name)),
            folding => Assert.Equal((innerStart, text.IndexOf('}', innerStart) + 1, "{…}"),
                (folding.StartOffset, folding.EndOffset, folding.Name)));
    }

    [Fact]
    public void CreateIgnoresDelimitersInsideEscapedJsonStrings()
    {
        const string text = "{\n  \"text\": \"fake } ] { [ and escaped quote \\\" here\",\n  \"items\": [1]\n}";

        var foldings = JsonFoldRanges.Create(text, CancellationToken.None);

        var folding = Assert.Single(foldings);
        Assert.Equal(0, folding.StartOffset);
        Assert.Equal(text.Length, folding.EndOffset);
        Assert.Equal("{…}", folding.Name);
    }

    [Fact]
    public void CreateKeepsCompletedPairWhenDocumentEndsInsideOuterPair()
    {
        const string text = "{\n  [\n    1\n  ]\n";

        var folding = Assert.Single(JsonFoldRanges.Create(text, CancellationToken.None));

        Assert.Equal(text.IndexOf('['), folding.StartOffset);
        Assert.Equal(text.IndexOf(']') + 1, folding.EndOffset);
        Assert.Equal("[…]", folding.Name);
    }

    [Fact]
    public void CreateDiscardsOpeningsAfterMismatchedClose()
    {
        const string text = "{\n  [\n  }\n  ]\n";

        Assert.Empty(JsonFoldRanges.Create(text, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"value\": [1]}")]
    [InlineData("{\r\n  \"value\": 1\r\n")]
    [InlineData("")]
    public void CreateOmitsSingleLineAndIncompleteRanges(string text) =>
        Assert.Empty(JsonFoldRanges.Create(text, CancellationToken.None));

    [Fact]
    public void CreateChecksCancellationBeforeScanning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => JsonFoldRanges.Create("{\n}", cancellation.Token));
    }
}
