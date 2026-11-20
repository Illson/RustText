using System.Reflection;
using System.Text;
using Avalonia.Controls;
using AvaloniaEdit;
using RustText.Demo;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class LogModeSwitchTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

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
