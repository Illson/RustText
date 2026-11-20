using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class LogWindowCloseTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task WindowCloseBlocksTypingWhileWaitingForCloseGate() => HeadlessTests.RunAsync(async profile =>
    {
        const string original = "first row\nsecond row\n";
        var path = Path.Combine(profile, "closing.log");
        await File.WriteAllTextAsync(path, original);
        var window = await OpenLogWindowAsync(path);
        var gate = CloseGate(window);
        var gateHeld = await gate.WaitAsync(0);
        Assert.True(gateHeld);

        try
        {
            var log = Assert.IsType<LogView>(Property(ActiveTab(window), "Log"));
            await InvokeTask(log, "TogglePageEditAsync");
            var page = PageEditor(log);
            page.CaretOffset = page.Document.TextLength;
            page.TextArea.Focus();

            window.Close();

            Assert.True((bool)Field(window, "_closing"));
            window.KeyTextInput("!");
            Assert.False(page.IsEnabled);
            Assert.Equal(original, page.Text);
            Assert.False(log.HasPendingEdit);
        }
        finally
        {
            gate.Release();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closed"));
        }

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    });

    [Fact]
    public Task PageEditorOpenedDuringWindowCloseRemainsBlocked() => HeadlessTests.RunAsync(async profile =>
    {
        const string original = "first row\nsecond row\n";
        var path = Path.Combine(profile, "opening-while-closing.log");
        await File.WriteAllTextAsync(path, original);
        var window = await OpenLogWindowAsync(path);
        var gate = CloseGate(window);
        Assert.True(await gate.WaitAsync(0));

        try
        {
            var log = Assert.IsType<LogView>(Property(ActiveTab(window), "Log"));
            var opening = InvokeTask(log, "TogglePageEditAsync");

            window.Close();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closing"));
            await opening;

            if (typeof(LogView).GetField("_pageEditor", Private)!.GetValue(log) is TextEditor page)
            {
                Assert.False(page.IsEnabled);
                page.CaretOffset = page.Document.TextLength;
                window.KeyTextInput("!");
                Assert.Equal(original, page.Text);
            }
            Assert.False(log.HasPendingEdit);
        }
        finally
        {
            gate.Release();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closed"));
        }

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    });

    [Fact]
    public Task CancellingWindowCloseRestoresPageInput() => HeadlessTests.RunAsync(async profile =>
    {
        const string original = "first row\nsecond row\n";
        var path = Path.Combine(profile, "cancel-closing.log");
        await File.WriteAllTextAsync(path, original);
        var window = await OpenLogWindowAsync(path);
        LogView? log = null;
        try
        {
            log = Assert.IsType<LogView>(Property(ActiveTab(window), "Log"));
            await InvokeTask(log, "TogglePageEditAsync");
            var page = PageEditor(log);
            page.CaretOffset = page.Document.TextLength;
            page.TextArea.Focus();
            window.KeyTextInput("!");
            Assert.True(log.HasPendingEdit);

            window.Close();
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog => dialog.Title == "Изменения страницы"));
            Assert.False(page.IsEnabled);
            var dialog = window.OwnedWindows.Single(item => item.Title == "Изменения страницы");
            FindButton(dialog, "Отмена").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => !(bool)Field(window, "_closing"));

            Assert.True(page.IsEnabled);
            page.CaretOffset = page.Document.TextLength;
            page.TextArea.Focus();
            window.KeyTextInput("?");
            Assert.Equal(original + "!?", page.Text);

            log.DiscardPageEdits();
            window.Close();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closed"));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            log?.DiscardPageEdits();
            if (!(bool)Field(window, "_closed")) await CloseWindowAsync(window);
        }
    });

    private static async Task<MainWindow> OpenLogWindowAsync(string path)
    {
        var window = new MainWindow { Width = 900, Height = 700 };
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        await InvokeTask(window, "OpenPathAsync", path, true, null);
        var log = Assert.IsType<LogView>(Property(ActiveTab(window), "Log"));
        await log.InitialPageLoad;
        return window;
    }

    private static async Task CloseWindowAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closed"));
    }

    private static SemaphoreSlim CloseGate(MainWindow window)
    {
        var workspace = Field(window, "_workspace");
        return (SemaphoreSlim)Property(workspace, "CloseGate")!;
    }

    private static object ActiveTab(MainWindow window) => Property(window, "ActiveTab")!;

    private static TextEditor PageEditor(LogView log) =>
        (TextEditor)typeof(LogView).GetField("_pageEditor", Private)!.GetValue(log)!;

    private static Button FindButton(Window dialog, string caption) => dialog.GetVisualDescendants()
        .OfType<Button>().Single(button => Equals(button.Content, caption));

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static object? Property(object owner, string name) =>
        owner.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(owner);

    private static async Task InvokeTask(object owner, string name, params object?[] arguments)
    {
        var task = (Task)(owner.GetType().GetMethod(name, Private | BindingFlags.Public)!
            .Invoke(owner, arguments) ?? throw new InvalidOperationException($"{name} returned null."));
        await task;
    }
}
