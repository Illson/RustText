using System.Reflection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Diagnostics;
using Xunit.Abstractions;
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
public sealed class OversizedInputTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(51_000, 8)]
    [InlineData(115_000, 80)]
    public Task CtrlVPastesIntoOrdinaryEditorAndClosedDraftRestoresInFull(int rows, int width) => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", "prefix suffix");
            var tab = ActiveTab(window);
            var editor = Assert.IsType<TextEditor>(Property(tab, "Editor"));
            await HeadlessTests.WaitForAsync(() => editor.TextArea.Focus());
            editor.CaretOffset = 7;
            var row = new string('я', width);
            var paste = string.Concat(Enumerable.Repeat(row + "\r\n", rows)) + "конец";
            var expected = "prefix " + paste.Replace("\r\n", "\n") + "suffix";
            await window.Clipboard!.SetTextAsync(paste);
            var timer = Stopwatch.StartNew();
            window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            window.KeyRelease(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await HeadlessTests.WaitForAsync(() => editor.LineCount == rows + 1);
            output.WriteLine($"Ctrl+V, {rows} rows: {timer.ElapsedMilliseconds} ms");
            Assert.Same(editor, Property(tab, "Editor"));
            Assert.Null(Property(tab, "Log"));
            Assert.Equal(expected, editor.Text);
            window.KeyTextInput("!");
            Assert.Equal(expected.Insert(expected.Length - 6, "!"), editor.Text);
            editor.Undo();
            Assert.Equal(expected, editor.Text);
            editor.Undo();
            Assert.Equal("prefix suffix", editor.Text);
            editor.Redo();
            await (Task)Invoke(window, "CloseTabAsync", tab)!;
            Assert.DoesNotContain(tab, Tabs(window));
            await (Task)Invoke(window, "RestoreLastClosedAsync")!;
            var restored = Assert.IsType<TextEditor>(Property(ActiveTab(window), "Editor"));
            Assert.Equal(expected, restored.Text);
            restored.SelectAll();
            restored.TextArea.PerformTextInput("снова");
            Assert.Equal("снова", restored.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ManualDraftLogModeKeepsEditsAcrossCloseAndReturnToEditor() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            const string row = "draft row\n";
            var original = string.Concat(Enumerable.Repeat(row, 51_000));
            Invoke(window, "NewTab", original);
            var tab = ActiveTab(window);
            var item = Assert.IsType<TabItem>(Property(tab, "Item"));
            var toggle = item.ContextMenu!.Items.OfType<MenuItem>().Single(menu => Equals(menu.Header, "Режим лога"));
            toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessTests.WaitForAsync(() => Property(tab, "Loading") is false && Property(tab, "Log") is LogView);
            var log = Assert.IsType<LogView>(Property(tab, "Log"));
            await log.InitialPageLoad;
            Assert.True(log.IsReadOnly);
            await (Task)typeof(LogView).GetMethod("TogglePageEditAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(log, null)!;
            var page = log.GetVisualDescendants().OfType<TextEditor>().Single();
            var suffix = original[page.Text.Length..];
            page.SelectAll();
            page.TextArea.PerformTextInput("новая строка");
            var bulk = string.Concat(Enumerable.Repeat("ж\n", 115_000));
            await window.Clipboard!.SetTextAsync(bulk);
            page.CaretOffset = page.Document.TextLength;
            page.TextArea.Focus();
            window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            window.KeyRelease(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await HeadlessTests.WaitForAsync(() => page.LineCount == 115_001);
            page.SelectAll();
            page.Document.Remove(0, page.Document.TextLength);
            window.KeyTextInput("после очистки");
            Assert.Equal("после очистки", page.Text);
            page.Document.Remove(0, page.Document.TextLength);
            Assert.True(log.HasPendingEdit);
            await (Task)Invoke(window, "CloseTabAsync", tab)!;
            Assert.DoesNotContain(tab, Tabs(window));
            await (Task)Invoke(window, "RestoreLastClosedAsync")!;
            var restored = ActiveTab(window);
            Assert.True(Assert.IsType<LogView>(Property(restored, "Log")).IsReadOnly);
            await (Task)Invoke(window, "ToggleLogModeAsync", restored)!;
            var editor = Assert.IsType<TextEditor>(Property(restored, "Editor"));
            Assert.Equal(suffix, editor.Text);
            editor.TextArea.PerformTextInput("после");
            Assert.Contains("после", editor.Text);
        }
        finally { await CloseAsync(window); }
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
