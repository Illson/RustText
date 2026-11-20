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
    [Fact]
    public Task DroppingLogOpensLogModeAndRefreshesAppendedError() => HeadlessTests.RunAsync(async profile =>
    {
        var logPath = Path.Combine(profile, "dropped.log");
        var textPath = Path.Combine(profile, "dropped.txt");
        await File.WriteAllTextAsync(logPath, "INFO initial\n");
        await File.WriteAllTextAsync(textPath, "plain text");
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)typeof(MainWindow)
                .GetField("_restoreFinished", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
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
            var log = Assert.IsType<LogView>(TabLog(Assert.Single(tabs, tab => Equals(TabPath(tab), logPath))));
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
