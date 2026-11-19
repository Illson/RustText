using System.Reflection;
using Avalonia.Controls;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class LogIndexOptInTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task SmallLogKeepsAutomaticIndexingByDefault() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "small.log");
            await File.WriteAllTextAsync(path, "one\ntwo\nthree\n");
            var (window, log) = ShowLog(path);
            try
            {
                await HeadlessTests.WaitForAsync(() => Field<long>(log, "_indexedTo") == new FileInfo(path).Length);

                Assert.False(Field<bool>(log, "_indexPaused"));
                Assert.False(Field<bool>(log, "_indexOptIn"));
                Assert.Equal("Индексирование", Field<Button>(log, "_indexButton").Content);
            }
            finally
            {
                Close(window, log);
            }
        });

    [Fact]
    public Task SmallLogThatGrowsPastTheThresholdStopsAutomaticIndexing() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "growing.log");
            await File.WriteAllTextAsync(path, "one\ntwo\nthree\n");
            var initialLength = new FileInfo(path).Length;
            var (window, log) = ShowLog(path);
            try
            {
                await HeadlessTests.WaitForAsync(() => Field<long>(log, "_indexedTo") == initialLength);
                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    file.SetLength(128L * 1024 * 1024);
                }

                Invoke(log, "OnRefreshTimerTick", null, EventArgs.Empty);
                await HeadlessTests.WaitForAsync(() => Field<bool>(log, "_indexPaused")
                    && !Field<bool>(log, "_indexBuilding")
                    && !Field<bool>(log, "_busy"));

                Assert.Equal(initialLength, Field<long>(log, "_indexedTo"));
                Assert.Equal("Индексировать весь файл", Field<Button>(log, "_indexButton").Content);
            }
            finally
            {
                Close(window, log);
            }
        });

    [Fact]
    public Task LargeLogOpensWithoutFullIndexAndExplainsTheAction() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = await CreateLargeLogAsync(profile);
            var (window, log) = ShowLog(path);
            try
            {
                await HeadlessTests.WaitForAsync(() => Field<LogPage?>(log, "_page") is not null
                    && !Field<bool>(log, "_busy"));

                var indexButton = Field<Button>(log, "_indexButton");
                Assert.StartsWith("first", Field<TextBox>(log, "_logText").Text);
                Assert.True(Field<bool>(log, "_indexPaused"));
                Assert.False(Field<bool>(log, "_indexBuilding"));
                Assert.Equal(0, Field<long>(log, "_indexedTo"));
                Assert.Equal("Индексировать весь файл", indexButton.Content);
                Assert.Contains("не индексируется автоматически", ToolTip.GetTip(indexButton)?.ToString());
            }
            finally
            {
                Close(window, log);
            }
        });

    [Fact]
    public Task ExplicitLargeLogIndexOptInRestoresAndCanBeCancelled() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = await CreateLargeLogAsync(profile);
            var (firstWindow, firstLog) = ShowLog(path);
            LogTabState optedIn;
            try
            {
                await HeadlessTests.WaitForAsync(() => Field<LogPage?>(firstLog, "_page") is not null
                    && !Field<bool>(firstLog, "_busy"));
                Invoke(firstLog, "ToggleIndexing");
                await HeadlessTests.WaitForAsync(() => Field<bool>(firstLog, "_indexBuilding"));
                optedIn = firstLog.CaptureState();
                Assert.True(optedIn.IndexOptIn);
                Assert.False(optedIn.IndexPaused);
                Invoke(firstLog, "ToggleIndexing");
                await HeadlessTests.WaitForAsync(() => !Field<bool>(firstLog, "_indexBuilding"));
            }
            finally
            {
                Close(firstWindow, firstLog);
            }

            var (restoredWindow, restoredLog) = ShowLog(path, optedIn);
            try
            {
                await HeadlessTests.WaitForAsync(() => Field<bool>(restoredLog, "_indexBuilding"));
                Assert.False(Field<bool>(restoredLog, "_indexPaused"));
                Assert.Equal("Индексирование", Field<Button>(restoredLog, "_indexButton").Content);
                Invoke(restoredLog, "ToggleIndexing");
                await HeadlessTests.WaitForAsync(() => !Field<bool>(restoredLog, "_indexBuilding"));
            }
            finally
            {
                Close(restoredWindow, restoredLog);
            }
        });

    private static async Task<string> CreateLargeLogAsync(string directory)
    {
        var path = Path.Combine(directory, "large.log");
        await File.WriteAllTextAsync(path, "first line\nsecond line\n");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        file.SetLength(128L * 1024 * 1024);
        return path;
    }

    private static (Window Window, LogView Log) ShowLog(string path, LogTabState? state = null)
    {
        var log = new LogView(path, state: state);
        var window = new Window { Width = 900, Height = 700, Content = log };
        window.Show();
        return (window, log);
    }

    private static void Close(Window window, LogView log)
    {
        log.Dispose();
        window.Close();
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static object? Invoke(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
}
