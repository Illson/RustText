using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using RustText.Demo;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
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
    public Task OversizedNewTabStartsInEditablePageWithExactUtf8Content() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            var content = new string('ё', 65_537) + "\nпоследняя строка 💡";
            Invoke(window, "NewTab", content);
            var tab = Tabs(window).Single(value => Property(value, "Loading") is true);
            await HeadlessTests.WaitForAsync(() => Property(tab, "Recovery") is SessionTab);

            Assert.Null(Property(tab, "Editor"));
            await EditablePageAsync(tab);
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
    public Task TextInputCrossingFiftyThousandLinesContinuesAtTheInsertionPoint() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", string.Concat(Enumerable.Repeat("r\n", 49_999)));
            var tab = ActiveTab(window);
            var editor = Assert.IsType<TextEditor>(Property(tab, "Editor"));
            await HeadlessTests.WaitForAsync(() => TopLevel.GetTopLevel(editor.TextArea) == window
                && (editor.TextArea.IsKeyboardFocusWithin || editor.TextArea.Focus()));
            Assert.True(editor.TextArea.IsKeyboardFocusWithin);
            editor.CaretOffset = editor.Document.TextLength;
            Assert.Equal(50_000, editor.Document.LineCount);
            editor.TextArea.PerformTextInput("ещё\nпоследняя 💡");
            var (paged, page) = await EditablePageAsync(tab);
            Assert.EndsWith("ещё\nпоследняя 💡", page.Text);
            Assert.Equal(page.Text!.Length, page.CaretIndex);
            await HeadlessTests.WaitForAsync(() => page.IsKeyboardFocusWithin);
            window.KeyTextInput(" строка");
            Assert.EndsWith("ещё\nпоследняя 💡 строка", page.Text);
            await paged.SavePageEditAsync();
            var (_, reopened) = await EditablePageAsync(tab);
            Assert.EndsWith("ещё\nпоследняя 💡 строка", reopened.Text);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task LargeClipboardPasteCreatesEditableDraftAndPreservesAllBytes() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", "");
            var tab = ActiveTab(window);
            var editor = Assert.IsType<TextEditor>(Property(tab, "Editor"));
            await HeadlessTests.WaitForAsync(() => TopLevel.GetTopLevel(editor.TextArea) == window
                && editor.TextArea.Focus());
            var text = string.Concat(Enumerable.Repeat("paste row\r\n", 50_001)) + "конец";
            var clipboard = TopLevel.GetTopLevel(editor)!.Clipboard;
            Assert.NotNull(clipboard);
            await clipboard.SetTextAsync(text);

            ApplicationCommands.Paste.Execute(null, editor.TextArea);
            var (_, page) = await EditablePageAsync(tab);
            Assert.Equal(0, editor.Document.TextLength);
            Assert.EndsWith("paste row\r\nконец", page.Text);
            Assert.Equal(page.Text!.Length, page.CaretIndex);
            var saved = Assert.IsType<SessionTab>(Property(tab, "Recovery"));
            Assert.Equal(50_002, saved.LineCount);
            var path = StorageService.OpenForTesting(profile).GetDraftPath(saved)!;
            Assert.Equal(Encoding.UTF8.GetBytes(text), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    private static async Task<(LogView Editor, TextBox Text)> EditablePageAsync(object tab)
    {
        await HeadlessTests.WaitForAsync(() => Property(tab, "Log") is LogView);
        var editor = Assert.IsType<LogView>(Property(tab, "Log"));
        await editor.InitialPageLoad;
        var page = Assert.IsType<TextBox>(typeof(LogView).GetField("_logText",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        Assert.False(page.IsReadOnly);
        Assert.False(editor.IsReadOnly);
        return (editor, page);
    }

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
