using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using RustText.Demo;
using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class OversizedInputTests
{
    [Fact]
    public async Task ReplacingReaderStreamsPrefixInsertionAndSuffixWithoutBuildingCombinedString()
    {
        using var reader = new ReplacingTextReader(new StringReader("before selected after"), 7, 8, "large\ninsert");
        Assert.Equal("before large\ninsert after", await reader.ReadToEndAsync());
    }

    [Fact]
    public Task OversizedNewTabIsStreamedToPreviewWithExactUtf8Content() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            const string row = "строка 💡\n";
            var content = string.Concat(Enumerable.Repeat(row, 50_001));
            Invoke(window, "NewTab", content);
            var tab = Tabs(window).Single(value => Property(value, "Loading") is true);
            await HeadlessTests.WaitForAsync(() => Property(tab, "Recovery") is SessionTab);

            Assert.Null(Property(tab, "Editor"));
            Assert.NotNull(Property(tab, "Log"));
            var saved = Assert.IsType<SessionTab>(Property(tab, "Recovery"));
            var path = StorageService.OpenForTesting(profile).GetDraftPath(saved)!;
            Assert.Equal(Encoding.UTF8.GetBytes(content), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task TextInputOverLineLimitIsInterceptedBeforeAvaloniaEditInsertion() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", "");
            var tab = ActiveTab(window);
            var editor = Assert.IsType<TextEditor>(Property(tab, "Editor"));
            var text = string.Concat(Enumerable.Repeat("r\n", 50_001));

            editor.TextArea.PerformTextInput(text);
            await HeadlessTests.WaitForAsync(() => Property(tab, "Recovery") is SessionTab);

            Assert.Equal(0, editor.Document.TextLength);
            var saved = Assert.IsType<SessionTab>(Property(tab, "Recovery"));
            var path = StorageService.OpenForTesting(profile).GetDraftPath(saved)!;
            Assert.Equal(Encoding.UTF8.GetBytes(text), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task OversizedClipboardPasteIsInterceptedBeforeAvaloniaEditInsertion() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", "");
            var tab = ActiveTab(window);
            var editor = Assert.IsType<TextEditor>(Property(tab, "Editor"));
            var text = string.Concat(Enumerable.Repeat("paste row\r\n", 50_001));
            var clipboard = TopLevel.GetTopLevel(editor)!.Clipboard;
            Assert.NotNull(clipboard);
            await clipboard.SetTextAsync(text);

            ApplicationCommands.Paste.Execute(null, editor.TextArea);
            await HeadlessTests.WaitForAsync(() => Property(tab, "Recovery") is SessionTab);

            Assert.Equal(0, editor.Document.TextLength);
            var saved = Assert.IsType<SessionTab>(Property(tab, "Recovery"));
            Assert.Equal(50_001, saved.LineCount);
            var path = StorageService.OpenForTesting(profile).GetDraftPath(saved)!;
            Assert.Equal(Encoding.UTF8.GetBytes(text), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    private static async Task<MainWindow> OpenWorkspaceAsync()
    {
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished")!);
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
    }

    private static IEnumerable<object> Tabs(MainWindow window) =>
        ((System.Collections.IDictionary)Field(window, "_tabs")!).Values.Cast<object>();
    private static object ActiveTab(MainWindow window) => typeof(MainWindow)
        .GetProperty("ActiveTab", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static object? Property(object target, string name) => target.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(target);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
}
