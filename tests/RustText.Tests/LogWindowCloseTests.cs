using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DeferredLogLoadCannotLosePageInputWhileWindowCloseWaitsForGate(bool closeBeforeClick) => HeadlessTests.RunAsync(async profile =>
    {
        const string original = "first row\nsecond row\n";
        var path = Path.Combine(profile, "deferred-close.log");
        await File.WriteAllTextAsync(path, original);
        var window = await OpenWindowAsync();
        var tab = Property(window, "ActiveTab")
            ?? window.GetType().GetMethod("AddTab", Private)!.Invoke(window, new object?[] { null, null, true })!;
        var saved = new SessionTab((long)Property(tab, "Id")!, path, TextEncodingKind.Utf8,
            Log: new LogTabState(Encoding: null, AutoRefresh: false, EditPages: true));
        Invoke(window, "SetDeferred", tab, saved);
        await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Any(button => Equals(button.Content, "Загрузить вкладку")));
        var gate = CloseGate(window);
        Assert.True(await gate.WaitAsync(0));

        try
        {
            if (closeBeforeClick)
            {
                window.Close();
                await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closing"));
                FindButton(window, "Загрузить вкладку").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else
            {
                var item = (ContentControl)Property(tab, "Item")!;
                var closedDuringLoad = false;
                item.PropertyChanged += (_, args) =>
                {
                    if (closedDuringLoad || args.Property != ContentControl.ContentProperty
                        || !(bool)Property(tab, "Loading")!) return;
                    Assert.Null(Property(tab, "Log"));
                    closedDuringLoad = true;
                    window.Close();
                };
                FindButton(window, "Загрузить вкладку").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(closedDuringLoad);
            }

            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closing"));
            await HeadlessTests.WaitForAsync(() => Property(tab, "Log") is LogView);
            var log = Assert.IsType<LogView>(Property(tab, "Log"));
            await log.InitialPageLoad;
            var page = PageEditor(log);
            window.UpdateLayout();
            await HeadlessTests.WaitForAsync(() => page.Bounds.Width > 0 && page.Bounds.Height > 0
                && ReferenceEquals(TopLevel.GetTopLevel(page), window));

            var textArea = page.TextArea;
            var point = textArea.TranslatePoint(new Point(10, 10), window)
                ?? throw new InvalidOperationException("Page editor is not attached to the window.");
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            page.CaretOffset = page.Document.TextLength;
            window.KeyTextInput("!");
            Assert.False(log.HasPendingEdit);
            Assert.Equal(original, page.Text);
            Assert.False(page.IsEnabled);
        }
        finally
        {
            if (Property(tab, "Log") is LogView currentLog) currentLog.DiscardPageEdits();
            if (!(bool)Field(window, "_closing")) window.Close();
            gate.Release();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closed"));
        }

        Assert.Equal(original, await File.ReadAllTextAsync(path));
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

    private static async Task<MainWindow> OpenWindowAsync()
    {
        var window = new MainWindow { Width = 900, Height = 700 };
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
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

    private static void Invoke(object owner, string name, params object?[] arguments) =>
        owner.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(owner, arguments);

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
