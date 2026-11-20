using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using RustText.Demo;
using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
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

    [Theory]
    [InlineData("[INFO] request-42 failed: { \"detail\": \"bad\" }", "demo.log", false)]
    [InlineData("[404] GET /health", "demo.log", false)]
    [InlineData("{worker} request-42 failed", "demo.log", false)]
    [InlineData("2026-11-19 12:30:00 [INFO] received {\"id\":42}", "demo.log", false)]
    [InlineData("message contains {\"detail\": \"bad\"}", null, false)]
    [InlineData("{ \"detail\": \"bad\" }", "events.log", false)]
    [InlineData("{\"detail\": \"bad\"}", null, true)]
    [InlineData("[{\"id\": 42}, null]", null, true)]
    [InlineData("{broken", null, true)]
    [InlineData("[1,]", null, true)]
    [InlineData("[not valid JSON]", null, true)]
    [InlineData("not JSON", "events.json", true)]
    [InlineData("not JSON", "events.jsonl", true)]
    [InlineData("not JSON", "events.ndjson", true)]
    public void AutomaticValidationOnlyRecognizesJsonDocumentsAndJsonPaths(string text, string? path, bool expected) =>
        Assert.Equal(expected, TextOperations.LooksLikeJson(text, path));

    [Fact]
    public void LogJsonLinesRequireAValidFirstRecordAndAContainerStartOnTheNextLine()
    {
        const string first = "{\"kind\":\"start\"}";
        const string invalidSecond = "{\"kind\": }";

        var jsonLines = first + "\r\n" + invalidSecond;
        Assert.True(TextOperations.LooksLikeJson(jsonLines, "events.log"));
        Assert.Equal(2, TextOperations.ValidateJson(jsonLines, "events.log")?.Line);

        Assert.False(TextOperations.LooksLikeJson("{not JSON}\r\n" + invalidSecond, "events.log"));
        Assert.False(TextOperations.LooksLikeJson(first + "\r\nnull", "events.log"));
        Assert.False(TextOperations.LooksLikeJson(first + "\r\n[INFO] request-42", "events.log"));
        Assert.False(TextOperations.LooksLikeJson(first + "\r\n{worker} request-42", "events.log"));
    }

    [Fact]
    public Task DemoLogDoesNotStartJsonValidationOrShowItsBanner() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "demo.log");
        await File.WriteAllTextAsync(path, "[INFO] request-42 failed: { \"detail\": \"bad\" }\r\n[404] GET /health");
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => Read<bool>(window, "_restoreFinished"));
            var open = typeof(MainWindow).GetMethod("OpenPathAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)open.Invoke(window, [path, false, null])!;
            var tab = ((IDictionary)Read<object>(window, "_tabs")).Values.Cast<object>().Single();
            await Task.Delay(400);
            Assert.Null(Value(tab, "JsonCancellation"));
            Assert.Null(Value(tab, "JsonError"));
            Assert.NotNull(Read<Border>(tab, "JsonBanner"));
            Assert.False(Read<Border>(tab, "JsonBanner").IsVisible);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
                await HeadlessTests.WaitForAsync(() => !window.IsVisible);
            }
        }
    });

    private static T Read<T>(object target, string name)
    {
        return (T)(Value(target, name) ?? throw new MissingMemberException(target.GetType().Name, name));
    }

    private static object? Value(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return target.GetType().GetField(name, flags)?.GetValue(target)
            ?? target.GetType().GetProperty(name, flags)?.GetValue(target);
    }

    [Fact]
    public void JsonLinesFormattingKeepsDelimitersAndFormatsASelectedRecord()
    {
        const string text = "{ \"values\": [1, 2] }\r\n  null";
        Assert.Equal("{\"values\":[1,2]}\r\nnull", TextOperations.FormatJson(text, "events.jsonl"));
        Assert.Equal("{\"values\":[1,2]}", TextOperations.FormatJson("{ \"values\": [1, 2] }", "events.log", text));
    }
}
