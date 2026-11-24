using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using RustText.Demo;
using RustText.Demo.Logs;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class LogModeSwitchTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task ConsoleTabCanToggleLogModeFromItsMenuWithoutStoppingCapture() => HeadlessTests.RunAsync(async _ =>
    {
        using var capture = new ConsoleCapture("Synthetic console");
        await using var writer = new StreamWriter(new FileStream(capture.Path, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync("[INFO] first frame");
        var window = new MainWindow { Width = 900, Height = 700 };
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            var tab = typeof(MainWindow).GetMethod("AddConsoleTab", Private)!.Invoke(window, [capture])!;
            var item = Assert.IsType<TabItem>(Property(tab, "Item"));
            var log = Assert.IsType<LogView>(item.Content);
            await log.InitialPageLoad;

            var menu = item.ContextMenu!;
            MenuItem ModeToggle(bool expected)
            {
                item.RaiseEvent(new ContextRequestedEventArgs());
                var toggle = Assert.Single(menu.Items.OfType<MenuItem>(), entry => Equals(entry.Header, "Режим лога")
                    || entry.Header is TextBlock { Text: "Режим лога" });
                Assert.True(toggle.IsVisible);
                Assert.True(toggle.IsEnabled);
                Assert.Equal(expected, toggle.IsChecked);
                return toggle;
            }

            ModeToggle(true).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            var plain = Assert.IsType<ConsoleTextView>(item.Content);
            Assert.True(plain.Editor.IsReadOnly);
            Assert.True((bool)Property(tab, "IsTransient")!);
            Assert.False(capture.IsDisposed);
            await HeadlessTests.WaitForAsync(() => plain.Editor.Text.Contains("first frame", StringComparison.Ordinal));
            await writer.WriteLineAsync("[WARN] новый вывод");
            await HeadlessTests.WaitForAsync(() => plain.Editor.Text.Contains("новый вывод", StringComparison.Ordinal));
            Assert.Same(plain, item.Content);

            ModeToggle(false).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            var restored = Assert.IsType<LogView>(item.Content);
            Assert.NotSame(log, restored);
            await restored.InitialPageLoad;
            var text = Assert.IsType<TextBox>(Field(restored, "_logText"));
            Assert.Contains("новый вывод", text.Text);
            Assert.False(capture.IsDisposed);
            ModeToggle(true).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            Assert.IsType<ConsoleTextView>(item.Content);
            await InvokeTask(window, "CloseTabAsync", tab);
            Assert.True(capture.IsDisposed);
        }
        finally { await CloseMainWindowAsync(window); }
    });

    [Fact]
    public Task ConsolePlainTextKeepsRecentUtf8WithoutTruncatingTheCapture() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "long-console.log");
        // The initial byte window starts one byte into the first Cyrillic character.
        var original = "older output\nЯ" + new string('x', ConsoleTextView.TextLimit - 6) + "\nend\n";
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));
        using var view = new ConsoleTextView(path, 12, wordWrap: false);
        var window = new Window { Content = view };
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => view.Editor.Text.EndsWith("\nend\n", StringComparison.Ordinal));
            Assert.DoesNotContain('\uFFFD', view.Editor.Text);
            Assert.DoesNotContain("older output", view.Editor.Text);
            await File.AppendAllTextAsync(path, "after switch\n");
            view.ReportDisconnected("Источник отключён");
            await HeadlessTests.WaitForAsync(() => view.Editor.Text.EndsWith("after switch\n", StringComparison.Ordinal));
            Assert.InRange(view.Editor.Document.TextLength, 1, ConsoleTextView.TextLimit);
            Assert.Equal(original + "after switch\n", await File.ReadAllTextAsync(path));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SwitchingToTheEditorDisablesPageInputDuringTheReload() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "page-edit.log");
        var original = string.Concat(Enumerable.Range(1, 20_000).Select(row => $"row-{row:D5}\n"));
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));
        var (window, tab, log) = await OpenLogAsync(path);

        try
        {
            await InvokeTask(log, "TogglePageEditAsync");
            var pageEditor = (TextEditor)Field(log, "_pageEditor");
            Assert.True(pageEditor.IsEnabled);
            var visiblePage = pageEditor.Text;

            var switching = InvokeTask(window, "ToggleLogModeAsync", tab);

            Assert.False(pageEditor.IsEnabled);
            Assert.Equal(visiblePage, pageEditor.Text);
            await switching;

            var editor = (TextEditor)Property(tab, "Editor")!;
            Assert.StartsWith(visiblePage, editor.Text);
            Assert.False((bool)Property(tab, "Dirty")!);
        }
        finally
        {
            await CloseMainWindowAsync(window);
        }
    });

    [Fact]
    public Task SwitchingFromUtf16LogPreservesEncodingAndBomWhenSaved() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "utf16.log");
        await File.WriteAllTextAsync(path, "запись", Encoding.Unicode);
        var (window, tab, _) = await OpenLogAsync(path);

        try
        {
            Assert.Equal(TextEncodingKind.Utf16LittleEndian, Property(tab, "Encoding"));

            await InvokeTask(window, "ToggleLogModeAsync", tab);

            var editor = (TextEditor)Property(tab, "Editor")!;
            Assert.Equal("запись", editor.Text);
            editor.Document.Insert(editor.Document.TextLength, "!");
            await InvokeTask(window, "SaveAsync", false);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal([0xFF, 0xFE], bytes[..2]);
            Assert.Equal("запись!", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }
        finally
        {
            await CloseMainWindowAsync(window);
        }
    });

    [Fact]
    public Task DirtyNamedUtf16EditorKeepsEncodingAcrossLogModeAndSave() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "dirty-utf16.txt");
        await File.WriteAllTextAsync(path, "исходный", Encoding.Unicode);
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));

        try
        {
            await InvokeTask(window, "OpenPathAsync", path, false, null);
            var tab = Property(window, "ActiveTab")!;
            Assert.Equal(TextEncodingKind.Utf16LittleEndian, Property(tab, "Encoding"));
            ((TextEditor)Property(tab, "Editor")!).Text = "изменённый";

            await InvokeTask(window, "ToggleLogModeAsync", tab);
            var log = (LogView)Property(tab, "Log")!;
            await log.InitialPageLoad;
            Assert.Equal(TextEncodingKind.Utf16LittleEndian, Property(tab, "Encoding"));

            await InvokeTask(window, "ToggleLogModeAsync", tab);
            Assert.Equal("изменённый", ((TextEditor)Property(tab, "Editor")!).Text);
            Assert.Equal(TextEncodingKind.Utf16LittleEndian, Property(tab, "Encoding"));
            await InvokeTask(window, "SaveAsync", false);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal([0xFF, 0xFE], bytes[..2]);
            Assert.Equal("изменённый", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }
        finally
        {
            await CloseMainWindowAsync(window);
        }
    });

    private static async Task<(MainWindow Window, object Tab, LogView Log)> OpenLogAsync(string path)
    {
        var window = new MainWindow { Width = 900, Height = 700 };
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        await InvokeTask(window, "OpenPathAsync", path, true, null);
        var tab = Property(window, "ActiveTab")!;
        var log = (LogView)Property(tab, "Log")!;
        await log.InitialPageLoad;
        return (window, tab, log);
    }

    private static async Task CloseMainWindowAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closed"));
    }

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static object? Property(object owner, string name) =>
        owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner);

    private static async Task InvokeTask(object owner, string name, params object?[] arguments)
    {
        var task = (Task)(owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(owner, arguments) ?? throw new InvalidOperationException($"{name} returned null."));
        await task;
    }
}
