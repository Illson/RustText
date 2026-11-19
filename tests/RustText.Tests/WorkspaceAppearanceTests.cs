using System.Reflection;
using Avalonia.Controls;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class WorkspaceAppearanceTests
{
    [Fact]
    public Task WindowSizeSurvivesColdRestoreAndCachedWorkspaceSwitch() => HeadlessTests.RunAsync(async profile =>
    {
        var catalog = new WorkspaceCatalogStore(profile);
        var named = await catalog.CreateAsync("Workspace with saved size");
        var window = await OpenWorkspaceAsync();
        try
        {
            SetSize(window, 1377, 911);
            await CallAsync(window, "SaveSessionAsync");

            await CallAsync(window, "SwitchWorkspaceAsync", named.Id);
            SetSize(window, 982, 734);
            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            AssertSize(window, 1377, 911);

            await CallAsync(window, "SwitchWorkspaceAsync", named.Id);
            AssertSize(window, 982, 734);
            await CloseAsync(window);

            window = await OpenWorkspaceAsync();
            AssertSize(window, 982, 734);

            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            AssertSize(window, 1377, 911);
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
        }
    });

    [Fact]
    public Task IndependentlyOpenedNamedWindowSavesItsOwnSize() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        MainWindow? secondary = null;
        try
        {
            await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Independent size");
            var entry = (await new WorkspaceCatalogStore(profile).LoadAsync()).Workspaces
                .Single(item => item.Name == "Independent size");
            SetSize(window, 1280, 820);
            await CallAsync(window, "SaveSessionAsync");

            await CallAsync(window, "OpenWorkspaceWindowAsync", entry.Id);
            secondary = Assert.Single(((System.Collections.IEnumerable)Field(window, "_workspace").GetType()
                    .GetProperty("Windows", BindingFlags.Instance | BindingFlags.Public)!
                    .GetValue(Field(window, "_workspace"))!).Cast<MainWindow>(), item => !ReferenceEquals(item, window));
            await HeadlessTests.WaitForAsync(() => (bool)Field(secondary, "_restoreFinished"));
            AssertSize(secondary, 1280, 820);

            SetSize(secondary, 947, 701);
            await CallAsync(secondary, "SaveSessionAsync");
            var storage = StorageService.OpenWorkspace(profile, entry.Id, (string)Field(secondary, "_windowId"));
            var saved = await storage.LoadSessionAsync();
            Assert.NotNull(saved);
            Assert.Equal(947, saved.WindowWidth);
            Assert.Equal(701, saved.WindowHeight);
        }
        finally
        {
            if (secondary is { IsVisible: true }) await CloseAsync(secondary);
            if (window.IsVisible) await CloseAsync(window);
        }
    });

    [Fact]
    public Task MaximizedWorkspaceRestoresItsNormalSizeAfterRestart() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            SetSize(window, 1377, 911);
            await CallAsync(window, "SaveSessionAsync");

            window.WindowState = WindowState.Maximized;
            // Model the platform resize that follows WM_SIZE after maximize.
            SetSize(window, 1920, 1080);
            await CallAsync(window, "SaveSessionAsync");

            var saved = await StorageService.OpenForTesting(profile).LoadSessionAsync();
            Assert.NotNull(saved);
            Assert.True(saved.Maximized);
            Assert.Equal(1377, saved.WindowWidth);
            Assert.Equal(911, saved.WindowHeight);
            await CloseAsync(window);

            window = await OpenWorkspaceAsync();
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.WindowState = WindowState.Normal;
            AssertSize(window, 1377, 911);
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
        }
    });

    [Fact]
    public Task CachedMaximizedWorkspaceKeepsNormalSizeAcrossSwitches() => HeadlessTests.RunAsync(async profile =>
    {
        var named = await new WorkspaceCatalogStore(profile).CreateAsync("Maximized size cache");
        var window = await OpenWorkspaceAsync();
        try
        {
            SetSize(window, 1377, 911);
            await CallAsync(window, "SaveSessionAsync");
            await CallAsync(window, "SwitchWorkspaceAsync", named.Id);
            SetSize(window, 982, 734);
            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);

            window.WindowState = WindowState.Maximized;
            SetSize(window, 1920, 1080);
            await CallAsync(window, "SwitchWorkspaceAsync", named.Id);
            Assert.Equal(WindowState.Normal, window.WindowState);
            AssertSize(window, 982, 734);

            window.WindowState = WindowState.Maximized;
            SetSize(window, 1600, 900);
            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.WindowState = WindowState.Normal;
            AssertSize(window, 1377, 911);

            await CallAsync(window, "SwitchWorkspaceAsync", named.Id);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.WindowState = WindowState.Normal;
            AssertSize(window, 982, 734);
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
        }
    });

    private static async Task<MainWindow> OpenWorkspaceAsync()
    {
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible ||
            window.FindControl<Avalonia.Controls.TextBlock>("StatusText")?.Text?.StartsWith(
                "Не удалось записать сессию.", StringComparison.Ordinal) == true);
        Assert.False(window.IsVisible, window.FindControl<Avalonia.Controls.TextBlock>("StatusText")?.Text);
    }

    private static void SetSize(MainWindow window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
    }

    private static void AssertSize(MainWindow window, double width, double height)
    {
        Assert.Equal(width, window.Width);
        Assert.Equal(height, window.Height);
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static Task CallAsync(MainWindow window, string name, params object?[] args) => (Task)Invoke(window, name, args)!;
}
