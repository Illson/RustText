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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task DetachedNamedWorkspaceDraftRemainsReachableWhenWindowsClose(bool closeDetachedFirst) =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string draftText = "detached draft survives close order";
            const string anchorText = "source window stays open";
            var catalog = new WorkspaceCatalogStore(profile);
            var namedWorkspace = await catalog.CreateAsync("Проверка отдельного окна");
            var otherWorkspace = await catalog.CreateAsync("Другое пространство");
            var primary = await OpenWindowAsync();
            MainWindow? detached = null;
            MainWindow? reopened = null;
            try
            {
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Invoke(primary, "NewTab", anchorText);
                var anchorTabId = PublicProperty(ActiveTab(primary), "Id");
                Invoke(primary, "NewTab", draftText);
                var originalTab = ActiveTab(primary);
                var detachedTabId = PublicProperty(originalTab, "Id");
                Invoke(primary, "OpenTabWindow", originalTab, null);
                detached = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));

                var primaryStorage = PrivateProperty(primary, "SessionStorage");
                var detachedStorage = PrivateProperty(detached, "SessionStorage");
                Assert.Same(primaryStorage, detachedStorage);
                await CallAsync(detached, "SwitchWorkspaceAsync", otherWorkspace.Id);
                await CallAsync(detached, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Assert.Same(primaryStorage, PrivateProperty(detached, "SessionStorage"));

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

                reopened = await OpenWindowAsync();
                Assert.Null(Invoke(reopened, "FindOtherWindow"));
                Assert.Single(WorkspaceWindows(reopened));

                var closedTab = Assert.Single(ClosedTabs(reopened));
                if (closeDetachedFirst)
                {
                    Assert.Equal(detachedTabId, PublicProperty(closedTab, "Id"));
                    Assert.Single(Tabs(reopened));
                    Assert.Equal(anchorText, ((TextEditor)PublicProperty(ActiveTab(reopened), "Editor")!).Text);
                    await CallAsync(reopened, "RestoreClosedAsync", closedTab);
                    var restoredEditor = (TextEditor)PublicProperty(ActiveTab(reopened), "Editor")!;
                    Assert.Equal(draftText, restoredEditor.Text);
                }
                else
                {
                    Assert.Equal(anchorTabId, PublicProperty(closedTab, "Id"));
                    var restoredTabs = Tabs(reopened);
                    Assert.Single(restoredTabs);
                    Assert.Equal(draftText, ((TextEditor)PublicProperty(restoredTabs[0], "Editor")!).Text);
                    Assert.True((bool)PublicProperty(restoredTabs[0], "Dirty")!);
                    await CallAsync(reopened, "RestoreClosedAsync", closedTab);
                    var afterRestore = Tabs(reopened);
                    var restoredAnchor = Assert.Single(afterRestore, tab => Equals(PublicProperty(tab, "Id"), anchorTabId));
                    var restoredDraft = Assert.Single(afterRestore, tab => Equals(PublicProperty(tab, "Id"), detachedTabId));
                    Assert.Equal(anchorText, ((TextEditor)PublicProperty(restoredAnchor, "Editor")!).Text);
                    Assert.Equal(draftText, ((TextEditor)PublicProperty(restoredDraft, "Editor")!).Text);
                }

                var restoredCatalog = await new WorkspaceCatalogStore(profile).LoadAsync();
                Assert.Equal(namedWorkspace.Id, restoredCatalog.WindowAssignments[WorkspaceCatalogStore.PrimaryWindowId]);
                Assert.DoesNotContain(restoredCatalog.WindowAssignments.Keys, id => id != WorkspaceCatalogStore.PrimaryWindowId);
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (primary is { IsVisible: true }) await CloseAsync(primary);
                if (reopened is { IsVisible: true }) await CloseAsync(reopened);
            }
        });

    private static async Task<MainWindow> OpenWindowAsync()
    {
        var window = new MainWindow([]);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)PrivateField(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible
            || window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Не удалось записать сессию.", StringComparison.Ordinal) == true);
        Assert.False(window.IsVisible, window.FindControl<TextBlock>("StatusText")?.Text);
    }

    private static async Task CallAsync(MainWindow window, string name, params object?[] arguments)
    {
        var task = (Task)Invoke(window, name, arguments)!;
        await task;
    }

    private static object? Invoke(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(window, arguments);

    private static object ActiveTab(MainWindow window) => PrivateProperty(window, "ActiveTab")!;

    private static object PrivateField(object target, string name) =>
        target.GetType().GetField(name, Private)!.GetValue(target)!;

    private static object? PrivateProperty(object target, string name) =>
        target.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(target);

    private static object? PublicProperty(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(target);

    private static object[] WorkspaceWindows(MainWindow window)
    {
        var workspace = PrivateField(window, "_workspace");
        var windows = workspace.GetType().GetProperty("Windows", BindingFlags.Instance | BindingFlags.Public)!.GetValue(workspace);
        return ((IEnumerable)windows!).Cast<object>().ToArray();
    }

    private static List<object> Tabs(MainWindow window) =>
        ((IDictionary)PrivateField(window, "_tabs")).Values.Cast<object>().ToList();

    private static List<object> ClosedTabs(MainWindow window) =>
        ((IEnumerable)PrivateField(window, "_closedTabs")).Cast<object>().ToList();
}
