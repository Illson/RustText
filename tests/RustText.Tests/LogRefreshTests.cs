using System.Collections;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class LogRefreshTests
{
    [Fact]
    public Task AppendToLargeLogAfterMinimapRenderedUpdatesFollowingPage() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "growing.log");
            var block = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("old\n", 65_536)));
            await using (var output = File.Create(path))
            {
                for (var index = 0; index < 208; index++)
                {
                    await output.WriteAsync(block);
                }
            }
            Assert.True(new FileInfo(path).Length > 50L * 1024 * 1024);
            await WithLogAsync(path, followEnd: true, async log =>
            {
                var minimap = Field<LogView.LogMinimap>(log, "_minimap");
                await HeadlessTests.WaitForAsync(() => minimap.Widths.Length > 0);
                await File.AppendAllTextAsync(path, "appended marker\n");
                Refresh(log);
                await HeadlessTests.WaitForAsync(() => Text(log).Contains("appended marker", StringComparison.Ordinal));
            });
        });

    [Fact]
    public Task ShortLogRefreshesTheSamePageWithoutFollowingTail() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "short.log");
            await File.WriteAllTextAsync(path, "old\n");
            await WithLogAsync(path, followEnd: false, async log =>
            {
                await File.AppendAllTextAsync(path, "appended\n");
                Refresh(log);
                await HeadlessTests.WaitForAsync(() => Text(log).Contains("appended", StringComparison.Ordinal));
                Assert.Equal(0, Field<LogPage>(log, "_page").StartOffset);
            });
        });

    [Fact]
    public Task SameSizeRotationRereadsTheFirstPage() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "rotate.log");
        var rotated = Path.Combine(profile, "rotated.log");
        await File.WriteAllTextAsync(path, "old\n");
        await WithLogAsync(path, followEnd: false, async log =>
        {
            Assert.StartsWith("old", Text(log));
            File.Move(path, rotated);
            await File.WriteAllTextAsync(path, "new\n");
            Refresh(log);
            await HeadlessTests.WaitForAsync(() => Text(log).StartsWith("new", StringComparison.Ordinal));
            Assert.Equal(0, Field<LogPage>(log, "_page").StartOffset);
            Assert.Equal("old\n", await File.ReadAllTextAsync(rotated));
        });
    });

    [Fact]
    public Task TruncateAndRegrowClearsHiddenBoundaryAndOldHits() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "regrow.log");
            await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("old\n", 250)));
            await WithLogAsync(path, followEnd: false, async log =>
            {
                await (Task)Invoke(log, "NavigateAsync", 200L, false)!;
                await (Task)Invoke(log, "HideBeginningAsync")!;
                Assert.Equal(200, Field<long>(log, "_hiddenOffset"));
                Field<TextBox>(log, "_searchBox").Text = "old";
                await (Task)Invoke(log, "SearchAsync", false)!;
                Assert.NotEmpty((IEnumerable)Field<object>(log, "_searchResults"));

                await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("new sequence\n", 500)));
                Refresh(log);
                await HeadlessTests.WaitForAsync(() => Text(log).StartsWith("new sequence", StringComparison.Ordinal));
                Assert.Equal(0, Field<LogPage>(log, "_page").StartOffset);
                Assert.Equal(0, Field<long>(log, "_hiddenOffset"));
                Assert.Empty((IEnumerable)Field<object>(log, "_searchResults"));
            });
        });

    private static async Task WithLogAsync(string path, bool followEnd, Func<LogView, Task> scenario)
    {
        var log = new LogView(path, state: new LogTabState(FollowEnd: followEnd, IndexPaused: true));
        var window = new Window { Width = 950, Height = 650, Content = log };
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => Text(log).Length > 0 && !Field<bool>(log, "_busy"));
            await scenario(log);
        }
        finally
        {
            log.Close();
            window.Close();
        }
    }

    private static void Refresh(LogView log) => Invoke(log, "OnRefreshTimerTick", null, EventArgs.Empty);
    private static string Text(LogView log) => Field<TextBox>(log, "_logText").Text ?? "";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(LogView log, string field) => (T)typeof(LogView).GetField(field, Flags)!.GetValue(log)!;
    private static object? Invoke(LogView log, string method, params object?[] args) =>
        typeof(LogView).GetMethod(method, Flags)!.Invoke(log, args);
}
