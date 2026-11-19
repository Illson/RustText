using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

public sealed class JsonTextTests
{
    [Theory]
    [InlineData("\uFEFFnull\r\n[1, 2]\r\ntrue", "events.JSONL", null)]
    [InlineData("{}\n\n{}", "events.ndjson", 2)]
    [InlineData("{} {} \n{}", "events.jsonl", 1)]
    [InlineData("{\n  \"items\": []\n}\n", null, null)]
    [InlineData("{}\n{}", "document.json", 2)]
    public void ValidationRespectsRecordBoundaries(string text, string? path, int? errorLine) =>
        Assert.Equal(errorLine, TextOperations.ValidateJson(text, path)?.Line);

    [Fact]
    public void JsonLinesFormattingKeepsDelimitersAndFormatsASelectedRecord()
    {
        const string text = "{ \"values\": [1, 2] }\r\n  null";
        Assert.Equal("{\"values\":[1,2]}\r\nnull", TextOperations.FormatJson(text, "events.jsonl"));
        Assert.Equal("{\"values\":[1,2]}", TextOperations.FormatJson("{ \"values\": [1, 2] }", "events.log", text));
    }
}
