using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class DetachedSessionRecoveryTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public Task DetachedDraftFromIndependentWorkspaceWindowMergesIntoPrimarySession(
        bool closeDetachedFirst, bool keepPrimaryWorkspaceLive) => HeadlessTests.RunAsync(async profile =>
    {
        const string canonicalText = "canonical active tab remains";
        const string canonicalEditedText = "canonical active tab remains · unsaved primary edit";
        const string canonicalDraftText = "canonical closed draft remains";
        const string detachedDraftText = "detached secondary draft remains";

        var catalog = new WorkspaceCatalogStore(profile);
        var namedWorkspace = await catalog.CreateAsync("Исходное пространство");
        var otherWorkspace = await catalog.CreateAsync("Проверка без активного первичного окна");
        var primary = await OpenWindowAsync();
        MainWindow? source = null;
        MainWindow? detached = null;
        MainWindow? restarted = null;
        try
        {
            await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
            Invoke(primary, "NewTab", canonicalText);
            var canonicalTabId = Property(ActiveTab(primary), "Id");
            Invoke(primary, "NewTab", canonicalDraftText);
            var canonicalDraftId = Property(ActiveTab(primary), "Id");
            await CallAsync(primary, "CloseTabAsync", ActiveTab(primary));
            await CallAsync(primary, "SaveSessionAsync");

            await CallAsync(primary, "OpenWorkspaceWindowAsync", namedWorkspace.Id);
            source = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));
            await HeadlessTests.WaitForAsync(() => (bool)Field(source, "_restoreFinished"));
            Assert.NotSame(Property(primary, "SessionStorage"), Property(source, "SessionStorage"));

            Invoke(source, "NewTab", detachedDraftText);
            var detachedTabId = Property(ActiveTab(source), "Id");
            Invoke(source, "OpenTabWindow", ActiveTab(source), null);
            detached = Assert.IsType<MainWindow>(Invoke(source, "FindOtherWindow"));
            Assert.Same(Property(source, "SessionStorage"), Property(detached, "SessionStorage"));

            var canonicalTab = Assert.Single(Tabs(primary), tab => Equals(Property(tab, "Id"), canonicalTabId));
            var canonicalEditor = (TextEditor)Property(canonicalTab, "Editor")!;
            canonicalEditor.Document.Insert(canonicalEditor.Document.TextLength, " · unsaved primary edit");
            Assert.True((bool)Property(canonicalTab, "Dirty")!);

            if (!keepPrimaryWorkspaceLive)
            {
                await CloseAsync(primary);
                primary = null!;
            }
            else
            {
                await CallAsync(primary, "SwitchWorkspaceAsync", otherWorkspace.Id);
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Assert.Same(Property(source, "SessionStorage"), Property(detached, "SessionStorage"));
            }

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

            if (primary is { IsVisible: true })
            {
                await CloseAsync(primary);
                primary = null!;
            }
            restarted = await OpenWindowAsync();
            Assert.Null(Invoke(restarted, "FindOtherWindow"));
            Assert.Single(WorkspaceWindows(restarted));

            if (!keepPrimaryWorkspaceLive)
                await CallAsync(restarted, "SwitchWorkspaceAsync", namedWorkspace.Id);

            var activeTabs = Tabs(restarted);
            var restoredCanonicalTab = Assert.Single(activeTabs, tab => Equals(Property(tab, "Id"), canonicalTabId));
            Assert.Equal(canonicalEditedText, ((TextEditor)Property(restoredCanonicalTab, "Editor")!).Text);

            var closed = ClosedTabs(restarted);
            var retainedCanonicalDraft = Assert.Single(closed, tab => Equals(Property(tab, "Id"), canonicalDraftId));
            Assert.Equal(canonicalDraftText, (await RestoreClosedTextAsync(restarted, retainedCanonicalDraft)).Text);
            foreach (var closedTab in ClosedTabs(restarted).ToArray())
                await RestoreClosedTextAsync(restarted, closedTab);

            var restoredTexts = Tabs(restarted)
                .Select(tab => ((TextEditor)Property(tab, "Editor")!).Text)
                .ToArray();
            Assert.Single(restoredTexts, text => text == canonicalDraftText);
            Assert.Single(restoredTexts, text => text == detachedDraftText);
            var restoredDetached = Assert.Single(Tabs(restarted), tab =>
                ((TextEditor)Property(tab, "Editor")!).Text == detachedDraftText);
            Assert.NotEqual(detachedTabId, Property(restoredDetached, "Id"));

            var restoredCatalog = await new WorkspaceCatalogStore(profile).LoadAsync();
            Assert.Equal(namedWorkspace.Id, restoredCatalog.WindowAssignments[WorkspaceCatalogStore.PrimaryWindowId]);
            Assert.DoesNotContain(restoredCatalog.WindowAssignments.Keys, id => id != WorkspaceCatalogStore.PrimaryWindowId);
            Assert.Single(WorkspaceWindows(restarted));
        }
        finally
        {
            if (detached is { IsVisible: true }) await CloseAsync(detached);
            if (source is { IsVisible: true }) await CloseAsync(source);
            if (primary is { IsVisible: true }) await CloseAsync(primary);
            if (restarted is { IsVisible: true }) await CloseAsync(restarted);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task DetachedPrimaryWorkspaceDraftSurvivesAtoBtoAAndEitherCloseOrder(bool closeDetachedFirst) =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string anchorText = "primary anchor draft";
            const string detachedText = "primary detached draft";
            var catalog = new WorkspaceCatalogStore(profile);
            var namedWorkspace = await catalog.CreateAsync("Первичное пространство");
            var otherWorkspace = await catalog.CreateAsync("Соседнее пространство");
            var primary = await OpenWindowAsync();
            MainWindow? detached = null;
            MainWindow? restarted = null;
            try
            {
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Invoke(primary, "NewTab", anchorText);
                var anchorId = Property(ActiveTab(primary), "Id");
                Invoke(primary, "NewTab", detachedText);
                var detachedId = Property(ActiveTab(primary), "Id");
                Invoke(primary, "OpenTabWindow", ActiveTab(primary), null);
                detached = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));

                var sharedStorage = Property(primary, "SessionStorage");
                Assert.Same(sharedStorage, Property(detached, "SessionStorage"));
                await CallAsync(primary, "SwitchWorkspaceAsync", otherWorkspace.Id);
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Assert.Same(sharedStorage, Property(primary, "SessionStorage"));
                Assert.Same(sharedStorage, Property(detached, "SessionStorage"));

                if (closeDetachedFirst)
                {
                    await CloseAsync(detached);
                    detached = null;
                    await CloseAsync(primary);
                    primary = null!;
                }
                else
                {
                    await CloseAsync(primary);
                    primary = null!;
                    await CloseAsync(detached);
                    detached = null;
                }

                restarted = await OpenWindowAsync();
                Assert.Null(Invoke(restarted, "FindOtherWindow"));
                Assert.Single(WorkspaceWindows(restarted));
                var closed = ClosedTabs(restarted);
                var expectedClosedId = closeDetachedFirst ? detachedId : anchorId;
                Assert.Contains(closed, tab => Equals(Property(tab, "Id"), expectedClosedId));
                foreach (var closedTab in ClosedTabs(restarted).ToArray())
                    await RestoreClosedTextAsync(restarted, closedTab);
                var texts = Tabs(restarted).Select(tab => ((TextEditor)Property(tab, "Editor")!).Text).ToArray();
                Assert.Contains(anchorText, texts);
                Assert.Contains(detachedText, texts);
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (primary is { IsVisible: true }) await CloseAsync(primary);
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
            }
        });

    [Fact]
    public Task UnreadablePrimaryCheckpointKeepsDetachedDraftOpenForCloseRetry() =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string canonicalText = "canonical session stays recoverable";
            const string detachedText = "detached text survives a failed merge";
            var catalog = new WorkspaceCatalogStore(profile);
            var namedWorkspace = await catalog.CreateAsync("Рабочее пространство для повтора");
            var primary = await OpenWindowAsync();
            MainWindow? source = null;
            MainWindow? detached = null;
            MainWindow? restarted = null;
            try
            {
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Invoke(primary, "NewTab", canonicalText);
                await CallAsync(primary, "SaveSessionAsync");
                var primaryStorage = Property(primary, "SessionStorage")!;

                await CallAsync(primary, "OpenWorkspaceWindowAsync", namedWorkspace.Id);
                source = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));
                await HeadlessTests.WaitForAsync(() => (bool)Field(source, "_restoreFinished"));
                Invoke(source, "NewTab", detachedText);
                var detachedTabId = Property(ActiveTab(source), "Id");
                Invoke(source, "OpenTabWindow", ActiveTab(source), null);
                detached = Assert.IsType<MainWindow>(Invoke(source, "FindOtherWindow"));

                await CloseAsync(primary);
                primary = null!;
                await CloseAsync(source);
                source = null;

                var sessionDirectory = Path.Combine((string)Property(primaryStorage, "RootDirectory")!, "session");
                var latestPath = Path.Combine(sessionDirectory, "latest.json");
                var previousPath = Path.Combine(sessionDirectory, "previous.json");
                Assert.True(File.Exists(latestPath));
                Assert.True(File.Exists(previousPath));
                var latestBytes = await File.ReadAllBytesAsync(latestPath);
                var previousBytes = await File.ReadAllBytesAsync(previousPath);
                try
                {
                    await File.WriteAllTextAsync(latestPath, "unreadable-latest-checkpoint");
                    await File.WriteAllTextAsync(previousPath, "unreadable-previous-checkpoint");

                    detached.Close();
                    await HeadlessTests.WaitForAsync(() =>
                        detached.FindControl<TextBlock>("StatusText")?.Text?.StartsWith(
                            "Не удалось записать сессию.", StringComparison.Ordinal) == true);

                    Assert.True(detached.IsVisible);
                    Assert.Equal(detachedText, ((TextEditor)Property(ActiveTab(detached), "Editor")!).Text);
                    Assert.Contains("восстанов", detached.FindControl<TextBlock>("StatusText")!.Text!, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    await File.WriteAllBytesAsync(latestPath, latestBytes);
                    await File.WriteAllBytesAsync(previousPath, previousBytes);
                }

                detached.FindControl<TextBlock>("StatusText")!.Text = string.Empty;
                await CloseAsync(detached);
                detached = null;
                restarted = await OpenWindowAsync();
                Assert.Null(Invoke(restarted, "FindOtherWindow"));
                Assert.Single(WorkspaceWindows(restarted));
                Assert.Equal(namedWorkspace.Id, Field(restarted, "_workspaceId"));
                Assert.Equal(canonicalText, ((TextEditor)Property(ActiveTab(restarted), "Editor")!).Text);

                var recovered = Assert.Single(ClosedTabs(restarted), tab =>
                    !Equals(Property(tab, "Id"), detachedTabId));
                Assert.Equal(detachedText, (await RestoreClosedTextAsync(restarted, recovered)).Text);
                var assignments = await new WorkspaceCatalogStore(profile).LoadAsync();
                Assert.DoesNotContain(assignments.WindowAssignments.Keys, id => id != WorkspaceCatalogStore.PrimaryWindowId);
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (source is { IsVisible: true }) await CloseAsync(source);
                if (primary is { IsVisible: true }) await CloseAsync(primary);
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
            }
        });

    private static async Task<MainWindow> OpenWindowAsync()
    {
        var window = new MainWindow([]);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task<(object? Id, string Text)> RestoreClosedTextAsync(MainWindow window, object closedTab)
    {
        var id = Property(closedTab, "Id");
        await CallAsync(window, "RestoreClosedAsync", closedTab);
        var tab = Assert.Single(Tabs(window), candidate => Equals(Property(candidate, "Id"), id));
        return (id, ((TextEditor)Property(tab, "Editor")!).Text);
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.FindControl<TextBlock>("StatusText")!.Text = string.Empty;
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        Assert.False(window.IsVisible, window.FindControl<TextBlock>("StatusText")?.Text);
    }

    private static async Task CallAsync(MainWindow window, string name, params object?[] arguments)
    {
        var task = (Task)Invoke(window, name, arguments)!;
        await task;
    }

    private static object? Invoke(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, AnyInstance)!.Invoke(window, arguments);

    private static object ActiveTab(MainWindow window) => Property(window, "ActiveTab")!;

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)!.GetValue(target)!;

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, AnyInstance)!.GetValue(target);

    private static object[] WorkspaceWindows(MainWindow window)
    {
        var workspace = Field(window, "_workspace");
        var windows = workspace.GetType().GetProperty("Windows", BindingFlags.Instance | BindingFlags.Public)!.GetValue(workspace);
        return ((IEnumerable)windows!).Cast<object>().ToArray();
    }

    private static List<object> Tabs(MainWindow window) =>
        ((IDictionary)Field(window, "_tabs")).Values.Cast<object>().ToList();

    private static List<object> ClosedTabs(MainWindow window) =>
        ((IEnumerable)Field(window, "_closedTabs")).Cast<object>().ToList();
}
