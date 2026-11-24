using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RustText.Demo;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class MainWindowDropTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AutomaticTxtLogDetectionCoversStartupAndForwardedPaths(bool forwarded) => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "automatic-log.txt");
        await File.WriteAllTextAsync(path, "[INFO] starting\n[WARN] retrying\n");
        var window = new MainWindow(forwarded ? [] : [path]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)typeof(MainWindow)
                .GetField("_restoreFinished", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            if (forwarded)
                await window.ReceivePathsAsync([path], activate: false);

            await HeadlessTests.WaitForAsync(() => Tabs(window).Any(tab => Equals(TabPath(tab), path) && TabLog(tab) is not null));
            var log = Assert.IsType<LogView>(TabLog(Assert.Single(Tabs(window), tab => Equals(TabPath(tab), path))));
            await log.InitialPageLoad;

            var recent = Assert.Single(await StorageService.OpenForTesting(profile).GetRecentFilesAsync());
            Assert.True(recent.ViewAsLog);
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    [Theory]
    [InlineData("{")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    public Task BrokenRecentHistoryDoesNotBlockOpeningOrSavingTheFile(string brokenJson) => HeadlessTests.RunAsync(async profile =>
    {
        var recent = Path.Combine(StorageService.OpenDefault().RootDirectory, "recent-files.json");
        await File.WriteAllTextAsync(recent, brokenJson);
        var path = Path.Combine(profile, "note.md");
        await File.WriteAllTextAsync(path, "original");
        var window = new MainWindow([]);
        window.Show();
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            await HeadlessTests.WaitForAsync(() => (bool)typeof(MainWindow).GetField("_restoreFinished", flags)!.GetValue(window)!);
            await window.ReceivePathsAsync([path], activate: false);
            var tab = Assert.Single(Tabs(window), tab => Equals(TabPath(tab), path));
            var editor = Assert.IsType<AvaloniaEdit.TextEditor>(TabEditor(tab));
            Assert.Equal("original", editor.Text);
            Assert.Contains("недавних", window.FindControl<TextBlock>("StatusText")!.Text);

            editor.Text = "changed";
            await (Task)typeof(MainWindow).GetMethod("SaveAsync", flags)!.Invoke(window, [false])!;
            Assert.Equal("changed", await File.ReadAllTextAsync(path));
            await (Task)typeof(MainWindow).GetMethod("ShowHomeAsync", flags)!.Invoke(window, null)!;
            Assert.Contains("недавних", window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "История файлов");
            Assert.Equal(brokenJson, await File.ReadAllTextAsync(recent));
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    [Fact]
    public Task PreviouslyOpenedTxtLogReopensAutomaticallyWithoutLevelMarkers() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "recent-log.txt");
        await File.WriteAllTextAsync(path, "first plain-looking line\nsecond plain-looking line\n");
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)typeof(MainWindow)
                .GetField("_restoreFinished", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            var open = typeof(MainWindow).GetMethod("OpenPathAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)open.Invoke(window, [path, true, null])!;
            var initialTab = Assert.Single(Tabs(window), tab => Equals(TabPath(tab), path));
            var initialLog = Assert.IsType<LogView>(TabLog(initialTab));
            await initialLog.InitialPageLoad;

            var recent = Assert.Single(await StorageService.OpenForTesting(profile).GetRecentFilesAsync());
            Assert.True(recent.ViewAsLog);
            var close = typeof(MainWindow).GetMethod("CloseTabAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)close.Invoke(window, [initialTab])!;
            await window.ReceivePathsAsync([path], activate: false);

            await HeadlessTests.WaitForAsync(() => Tabs(window).Any(tab => Equals(TabPath(tab), path) && TabLog(tab) is not null));
            var reopenedLog = Assert.IsType<LogView>(TabLog(Assert.Single(Tabs(window), tab => Equals(TabPath(tab), path))));
            await reopenedLog.InitialPageLoad;
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    [Theory]
    [InlineData(".log", false)]
    [InlineData(".log", true)]
    [InlineData(".txt", false)]
    [InlineData(".txt", true)]
    public Task DroppingLogOpensLogModeAndRefreshesAppendedError(string extension, bool alreadyInEditor) => HeadlessTests.RunAsync(async profile =>
    {
        var logPath = Path.Combine(profile, "dropped" + extension);
        var textPath = Path.Combine(profile, "dropped-note.txt");
        await File.WriteAllTextAsync(logPath, "[INFO] initial\n[WARN] retry\n");
        await File.WriteAllTextAsync(textPath, "plain text");
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)typeof(MainWindow)
                .GetField("_restoreFinished", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            if (alreadyInEditor)
            {
                var open = typeof(MainWindow).GetMethod("OpenPathAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)open.Invoke(window, [logPath, false, null])!;
            }
            var logFile = await window.StorageProvider.TryGetFileFromPathAsync(logPath);
            var textFile = await window.StorageProvider.TryGetFileFromPathAsync(textPath);
            Assert.NotNull(logFile);
            Assert.NotNull(textFile);

            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(textFile));
            transfer.Add(DataTransferItem.CreateFile(logFile));
            var drop = new DragEventArgs(DragDrop.DropEvent, transfer, window, default, KeyModifiers.None);
            window.RaiseEvent(drop);

            Assert.True(drop.Handled);
            await HeadlessTests.WaitForAsync(() => Tabs(window).Any(tab => Equals(TabPath(tab), logPath) && TabLog(tab) is not null)
                && Tabs(window).Any(tab => Equals(TabPath(tab), textPath) && TabEditor(tab) is not null));
            var tabs = Tabs(window);
            var log = Assert.IsType<LogView>(TabLog(Assert.Single(tabs, tab => Equals(TabPath(tab), logPath)
                && TabLog(tab) is not null)));
            if (alreadyInEditor)
                Assert.Single(tabs, tab => Equals(TabPath(tab), logPath) && TabEditor(tab) is not null);
            Assert.NotNull(TabEditor(Assert.Single(tabs, tab => Equals(TabPath(tab), textPath))));
            await log.InitialPageLoad;

            await File.AppendAllTextAsync(logPath, "[ERR] appended error\n");
            await HeadlessTests.WaitForAsync(() => LogText(log).Text?.Contains("appended error", StringComparison.Ordinal) == true);
            Click(log, "Уровни…");
            var filter = Assert.Single(log.GetVisualDescendants().OfType<LogLevelFilterPanel>());
            await HeadlessTests.WaitForAsync(() => filter.GetVisualDescendants().OfType<Button>()
                .Any(button => Equals(button.Content, "Снять все")));
            Click(filter, "Снять все");
            filter.GetVisualDescendants().OfType<ToggleButton>().Single(button => Equals(button.Content, "Error")).IsChecked = true;

            await HeadlessTests.WaitForAsync(() => FilteredText(log).Contains("appended error", StringComparison.Ordinal));
            Assert.False(FilteredText(log).Contains("INFO initial", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    private static object[] Tabs(MainWindow window) =>
        ((System.Collections.IDictionary)typeof(MainWindow).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!).Values.Cast<object>().ToArray();

    private static object? TabPath(object tab) => Property(tab, "Path");

    private static object? TabLog(object tab) => Property(tab, "Log");

    private static object? TabEditor(object tab) => Property(tab, "Editor");

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);

    private static TextBox LogText(LogView log) =>
        (TextBox)typeof(LogView).GetField("_logText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log)!;

    private static string FilteredText(LogView log) => log.GetVisualDescendants().OfType<TextBox>().Single(box =>
        AutomationProperties.GetAutomationId(box) == "LogLevelFilterResults").Text ?? "";

    private static void Click(Control scope, string caption) => scope.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.Content, caption)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
