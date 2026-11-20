using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
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

    [Fact]
    public Task JsonEditorFoldsNestedContainersWithoutChangingDocumentAndCleansUpOnClose() =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string json = "{\n  \"items\": [\n    {\n      \"name\": \"first\"\n    }\n  ]\n}\n";
            var path = Path.Combine(profile, "nested.json");
            await File.WriteAllTextAsync(path, json);
            var window = new MainWindow([]);
            window.Show();
            try
            {
                await HeadlessTests.WaitForAsync(() => Read<bool>(window, "_restoreFinished"));
                var open = typeof(MainWindow).GetMethod("OpenPathAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)open.Invoke(window, [path, false, null])!;

                var tab = ((IDictionary)Read<object>(window, "_tabs")).Values.Cast<object>().Single();
                var editor = Read<TextEditor>(tab, "Editor");
                var textView = editor.TextArea.TextView;
                FoldingManager? foldingService = null;
                await HeadlessTests.WaitForAsync(() =>
                {
                    foldingService = textView.GetService(typeof(FoldingManager)) as FoldingManager;
                    return foldingService?.AllFoldings.Count() == 3;
                });
                await HeadlessTests.WaitForAsync(() => textView.VisualLinesValid
                    && textView.VisualLines.Count > 0);

                var foldingMargin = Assert.Single(editor.TextArea.LeftMargins.OfType<FoldingMargin>());
                var foldings = foldingService!.AllFoldings.ToArray();
                Assert.All(foldings, folding => Assert.False(folding.IsFolded));
                var originalLineCount = textView.VisualLines.Count;
                await ClickTopMarkerAsync();
                await HeadlessTests.WaitForAsync(() => textView.VisualLinesValid
                    && textView.VisualLines.Count < originalLineCount);
                Assert.Contains(foldingService.AllFoldings, folding => folding.IsFolded);
                Assert.Equal(json, editor.Text);

                await ClickTopMarkerAsync();
                await HeadlessTests.WaitForAsync(() => textView.VisualLinesValid
                    && textView.VisualLines.Count == originalLineCount);
                Assert.All(foldingService.AllFoldings, folding => Assert.False(folding.IsFolded));
                Assert.Equal(json, editor.Text);

                const string firstValue = "\"first\"";
                editor.Document.Replace(json.IndexOf(firstValue, StringComparison.Ordinal), firstValue.Length, "[\n        null\n      ]");
                try
                {
                    await HeadlessTests.WaitForAsync(() => foldingService.AllFoldings.Count() == 4);
                }
                finally
                {
                    editor.Document.UndoStack.Undo();
                }
                await HeadlessTests.WaitForAsync(() => foldingService.AllFoldings.Count() == 3);
                Assert.Equal(json, editor.Text);

                await (Task)typeof(MainWindow).GetMethod("CloseTabAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [tab])!;
                Assert.Null(textView.GetService(typeof(FoldingManager)));
                Assert.Empty(editor.TextArea.LeftMargins.OfType<FoldingMargin>());

                async Task ClickTopMarkerAsync()
                {
                    await HeadlessTests.WaitForAsync(() => FoldingMarkers(foldingMargin)
                        .Any(control => control.Bounds.Width > 0 && control.Bounds.Height > 0));
                    var marker = FoldingMarkers(foldingMargin).MinBy(control => control.Bounds.Y)!;
                    var point = marker.TranslatePoint(new Point(marker.Bounds.Width / 2, marker.Bounds.Height / 2), window);
                    Assert.NotNull(point);
                    window.MouseDown(point.Value, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(point.Value, MouseButton.Left, RawInputModifiers.None);
                }
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

    [Fact]
    public Task JsonDiagnosticMarkerTracksInvalidTokenAndClearsAfterCorrection() =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string invalidJson = "{\n  \"имя\": }\n";
            var invalidOffset = invalidJson.IndexOf('}');
            var path = Path.Combine(profile, "invalid.json");
            await File.WriteAllTextAsync(path, invalidJson);
            var window = new MainWindow([]);
            TextEditor? editor = null;
            window.Show();
            try
            {
                await HeadlessTests.WaitForAsync(() => Read<bool>(window, "_restoreFinished"));
                var open = typeof(MainWindow).GetMethod("OpenPathAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)open.Invoke(window, [path, false, null])!;

                var tab = ((IDictionary)Read<object>(window, "_tabs")).Values.Cast<object>().Single();
                editor = Read<TextEditor>(tab, "Editor");
                await HeadlessTests.WaitForAsync(() => Value(tab, "JsonError") is not null);

                var marker = Assert.Single(editor.TextArea.TextView.BackgroundRenderers
                    .OfType<JsonErrorMarker>());
                Assert.Equal(invalidOffset, marker.Offset);

                var view = editor.TextArea.TextView;
                await HeadlessTests.WaitForAsync(() => view.GetService(typeof(FoldingManager)) is FoldingManager manager
                    && manager.AllFoldings.Any());
                var folding = Assert.Single(Assert.IsAssignableFrom<FoldingManager>(view.GetService(typeof(FoldingManager))).AllFoldings);
                folding.IsFolded = true;
                var jump = Assert.Single(Read<Border>(tab, "JsonBanner").GetVisualDescendants().OfType<Button>(),
                    button => Equals(button.Content, "Перейти к ошибке"));
                jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(folding.IsFolded);
                Assert.Equal(invalidOffset, editor.CaretOffset);

                editor.Document.Insert(invalidOffset, "null");
                await HeadlessTests.WaitForAsync(() => Value(tab, "JsonError") is null
                    && marker.Offset is null
                    && Value(tab, "JsonCancellation") is null);

            }
            finally
            {
                if (window.IsVisible)
                {
                    if (editor is not null && !editor.Document.UndoStack.IsOriginalFile)
                    {
                        editor.Document.UndoStack.Undo();
                    }

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

    private static IEnumerable<Control> FoldingMarkers(Control foldingMargin) =>
        foldingMargin.GetVisualDescendants().OfType<Control>()
            .Where(control => control.GetType().Name == "FoldingMarginMarker");

    [Fact]
    public void JsonLinesFormattingKeepsDelimitersAndFormatsASelectedRecord()
    {
        const string text = "{ \"values\": [1, 2] }\r\n  null";
        Assert.Equal("{\"values\":[1,2]}\r\nnull", TextOperations.FormatJson(text, "events.jsonl"));
        Assert.Equal("{\"values\":[1,2]}", TextOperations.FormatJson("{ \"values\": [1, 2] }", "events.log", text));
    }
}
