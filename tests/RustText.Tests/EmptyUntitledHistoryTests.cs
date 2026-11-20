using System.Reflection;
using Avalonia.Controls;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class EmptyUntitledHistoryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task ClosingAndRestartingDoesNotPromoteEmptyUntitledTabsToHistory() =>
        HeadlessTests.RunAsync(async profile =>
        {
            MainWindow? window = null;
            MainWindow? restarted = null;
            try
            {
                window = await OpenWorkspaceAsync();
                Invoke(window, "NewTab", "");
                var empty = ActiveTab(window);
                Assert.Empty(Read<TextEditor>(empty, "Editor").Text);
                await CallAsync(window, "CloseTabAsync", empty);
                Assert.Empty(ClosedTabs(window));

                const string whitespace = " \t\r\n";
                Invoke(window, "NewTab", whitespace);
                var whitespaceTab = ActiveTab(window);
                await CallAsync(window, "CloseTabAsync", whitespaceTab);
                var closed = Assert.Single(ClosedTabs(window));
                var storage = Assert.IsType<StorageService>(Read(window, "SessionStorage", Private));
                Assert.Equal(whitespace, await storage.LoadDraftAsync(closed));

                await CloseAsync(window);
                window = null;
                restarted = await OpenWorkspaceAsync();
                var restored = Assert.Single(ClosedTabs(restarted));
                Assert.Equal(closed.Id, restored.Id);
                Assert.Equal(whitespace, await Assert.IsType<StorageService>(Read(restarted, "SessionStorage", Private))
                    .LoadDraftAsync(restored));
            }
            finally
            {
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
                if (window is { IsVisible: true }) await CloseAsync(window);
            }
        });

    [Fact]
    public Task EmptyStartupHistoryDropsOnlyKnownZeroByteUntitledDraftsAndLeavesFilesIntact() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var window = await OpenWorkspaceAsync();
            try
            {
                var storage = Assert.IsType<StorageService>(Read(window, "SessionStorage", Private));
                var emptyTab = await storage.SaveClosedDraftAsync(
                    new SessionTab(91001, null, TextEncodingKind.Utf8) { UntitledName = "Без имени" }, "");
                var whitespaceTab = await storage.SaveClosedDraftAsync(
                    new SessionTab(91002, null, TextEncodingKind.Utf8) { UntitledName = "Без имени" }, " \n");
                var filePath = Path.Combine(profile, "empty-file.txt");
                await File.WriteAllTextAsync(filePath, "");
                var emptyFileTab = new SessionTab(91003, filePath, TextEncodingKind.Utf8);
                var emptyClosedTab = await storage.SaveClosedDraftAsync(
                    new SessionTab(91004, null, TextEncodingKind.Utf8) { UntitledName = "Без имени" }, "");

                Call(window, "AddEmptyStartupHistory", new SessionState(
                    [emptyTab, whitespaceTab, emptyFileTab], [emptyClosedTab]));

                Assert.Equal([91002L, 91003L], ClosedTabs(window).Select(tab => tab.Id).Order());
                Assert.Equal(" \n", await storage.LoadDraftAsync(ClosedTabs(window).Single(tab => tab.Id == 91002)));
                Assert.True(File.Exists(storage.GetDraftPath(emptyTab)));
                Assert.True(File.Exists(storage.GetDraftPath(emptyClosedTab)));
                Assert.Empty(await File.ReadAllTextAsync(filePath));
            }
            finally { await CloseAsync(window); }
        });

    [Fact]
    public Task MoreThanOneHundredFilteredEmptyHistoryEntriesDoNotDisableScratchHistoryCap() =>
        HeadlessTests.RunAsync(async _ =>
        {
            var window = await OpenWorkspaceAsync();
            try
            {
                await CallAsync(window, "EnterEmptyStartupAsync");
                var oldEmptyEntries = Enumerable.Range(1, 101)
                    .Select(id => new SessionTab(id, null, TextEncodingKind.Utf8) { UntitledName = "Без имени" })
                    .ToList();
                Call(window, "AddEmptyStartupHistory", new SessionState([], oldEmptyEntries));
                Assert.Empty(ClosedTabs(window));

                for (var i = 0; i < 101; i++)
                {
                    Invoke(window, "NewTab", $"scratch draft {i}");
                    await CallAsync(window, "CloseTabAsync", ActiveTab(window));
                }

                Assert.Equal(100, ClosedTabs(window).Count);
                Assert.All(ClosedTabs(window), tab => Assert.NotNull(tab.DraftRef));
            }
            finally { if (window.IsVisible) await CloseAsync(window); }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task DetachedWorkspaceCloseRecoversNonemptyDraftButSkipsTransferredEmptyTab(bool closeDetachedFirst) =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string expectedText = "recover this detached draft";
            var named = await new WorkspaceCatalogStore(profile).CreateAsync("Detached empty ghost check");
            MainWindow? primary = null;
            MainWindow? source = null;
            MainWindow? detached = null;
            MainWindow? restarted = null;
            try
            {
                primary = await OpenWorkspaceAsync();
                await CallAsync(primary, "SwitchWorkspaceAsync", named.Id);
                Invoke(primary, "NewTab", "primary anchor");
                await CallAsync(primary, "OpenWorkspaceWindowAsync", named.Id);
                source = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));
                await HeadlessTests.WaitForAsync(() => Field<bool>(source, "_restoreFinished"));

                Invoke(source, "NewTab", "independent source anchor");
                Invoke(source, "NewTab", expectedText);
                var recoverable = ActiveTab(source);
                Invoke(source, "OpenTabWindow", recoverable, null);
                detached = Assert.IsType<MainWindow>(Invoke(source, "FindOtherWindow"));
                await HeadlessTests.WaitForAsync(() => Field<bool>(detached, "_restoreFinished"));

                Invoke(source, "NewTab", "");
                Invoke(source, "TransferTab", ActiveTab(source), detached, Field(detached, "_active"), null);
                Assert.Equal(2, Tabs(detached).Length);

                if (closeDetachedFirst)
                {
                    await CloseAsync(detached);
                    detached = null;
                    await CloseAsync(source);
                    source = null;
                }
                else
                {
                    await CloseAsync(source);
                    source = null;
                    await CloseAsync(detached);
                    detached = null;
                }
                await CloseAsync(primary);
                primary = null;

                restarted = await OpenWorkspaceAsync();
                var history = ClosedTabs(restarted);
                var onlyDraft = Assert.Single(history, tab => tab.Path is null);
                Assert.Equal(expectedText, await Assert.IsType<StorageService>(Read(restarted, "SessionStorage", Private))
                    .LoadDraftAsync(onlyDraft));
            }
            finally
            {
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (source is { IsVisible: true }) await CloseAsync(source);
                if (primary is { IsVisible: true }) await CloseAsync(primary);
            }
        });

    private static async Task<MainWindow> OpenWorkspaceAsync()
    {
        var window = new MainWindow([]);
        window.Show();
        await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_closed")
            || window.FindControl<Avalonia.Controls.TextBlock>("StatusText")?.Text?.StartsWith("Не удалось записать сессию.", StringComparison.Ordinal) == true);
        Assert.True(Field<bool>(window, "_closed"), window.FindControl<Avalonia.Controls.TextBlock>("StatusText")?.Text);
    }

    private static object ActiveTab(MainWindow window) => Read(window, "ActiveTab", Private)!;

    private static List<SessionTab> ClosedTabs(MainWindow window) =>
        Field<List<SessionTab>>(window, "_closedTabs");

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static object? Read(object owner, string name,
        BindingFlags binding = BindingFlags.Instance | BindingFlags.Public) =>
        owner.GetType().GetProperty(name, binding)!.GetValue(owner);

    private static T Read<T>(object owner, string name) => (T)Read(owner, name)!;

    private static object? Call(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);

    private static Task CallAsync(object owner, string name, params object?[] args) =>
        (Task)Call(owner, name, args)!;

    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        Call(window, name, args);

    private static object[] Tabs(MainWindow window) =>
        ((System.Collections.IDictionary)Field<System.Collections.IDictionary>(window, "_tabs")).Values.Cast<object>().ToArray();

    private static object Field(MainWindow window, string name) =>
        window.GetType().GetField(name, Private)!.GetValue(window)!;
}
