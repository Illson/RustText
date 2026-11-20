using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class ThemeMenuTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("")]
    [InlineData(",\"theme\":\"future-theme\"")]
    public Task ThemeFollowsMenuAcrossWindowsAndRestarts(string themeProperty) => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        await File.WriteAllTextAsync(Path.Combine(storage.RootDirectory, "settings.json"),
            $"{{\"untitled_tab_limit\":17,\"open_large_files_as_log\":true{themeProperty}}}");

        var first = await ShowWindowAsync();
        MainWindow? second = null;
        try
        {
            AssertSelected(first, "Фокус");
            var focusBackground = Background(first);

            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(first, ["первый"]);
            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(first, ["второй"]);
            var tab = typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(first)!;
            typeof(MainWindow).GetMethod("OpenTabWindow", Private)!.Invoke(first, [tab, null]);
            second = Assert.IsType<MainWindow>(first.FindOtherWindow());

            await SelectAsync(first, storage, "Неон", "neon");
            AssertSelected(second, "Неон");
            var neonBackground = Background(first);
            Assert.NotEqual(focusBackground, neonBackground);
            Assert.Equal(neonBackground, Background(second));

            await SelectAsync(second, storage, "Vice City", "vice_city");
            AssertSelected(first, "Vice City");
            Assert.NotEqual(neonBackground, Background(first));
            Assert.NotEqual(focusBackground, Background(first));
            Assert.Equal(Background(first), Background(second));
            var saved = await storage.LoadSettingsAsync();
            Assert.Equal(17, saved.UntitledTabLimit);
            Assert.True(saved.OpenLargeFilesAsLog);
        }
        finally
        {
            if (second is not null) await CloseAsync(second);
            await CloseAsync(first);
        }

        var reopened = await ShowWindowAsync();
        try
        {
            AssertSelected(reopened, "Vice City");
            await SelectAsync(reopened, storage, "Фокус", "focus");
        }
        finally { await CloseAsync(reopened); }
    });

    [Fact]
    public Task NamedWorkspacesRememberDifferentThemesAcrossSwitchAndRestart() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSettingsAsync(new UserSettings { Theme = "neon" });
        var catalog = new WorkspaceCatalogStore(profile);
        var first = await catalog.CreateAsync("Первое пространство");
        var second = await catalog.CreateAsync("Второе пространство");

        var window = await ShowWindowAsync();
        Color firstColor;
        try
        {
            await SwitchWorkspaceAsync(window, first.Id);
            AssertSelected(window, "Неон");
            var legacyColor = Background(window);
            await SelectAsync(window, "Vice City");
            firstColor = Background(window);
            Assert.NotEqual(legacyColor, firstColor);

            await SwitchWorkspaceAsync(window, second.Id);
            AssertSelected(window, "Неон");
            Assert.Equal(legacyColor, Background(window));
            await SelectAsync(window, "Фокус");
            Assert.NotEqual(firstColor, Background(window));

            await SwitchWorkspaceAsync(window, first.Id);
            AssertSelected(window, "Vice City");
            Assert.Equal(firstColor, Background(window));

            var saved = await catalog.LoadAsync();
            Assert.Equal("vice_city", saved.Workspaces.Single(workspace => workspace.Id == first.Id).Theme);
            Assert.Equal("focus", saved.Workspaces.Single(workspace => workspace.Id == second.Id).Theme);
            Assert.Equal("neon", (await storage.LoadSettingsAsync()).Theme);
        }
        finally { await CloseAsync(window); }

        var reopened = await ShowWindowAsync();
        try
        {
            AssertSelected(reopened, "Vice City");
            Assert.Equal(firstColor, Background(reopened));
            await SwitchWorkspaceAsync(reopened, second.Id);
            AssertSelected(reopened, "Фокус");
            await SwitchWorkspaceAsync(reopened, WorkspaceCatalogStore.DefaultWorkspaceId);
            AssertSelected(reopened, "Неон");
        }
        finally { await CloseAsync(reopened); }
    });

    private static Task SwitchWorkspaceAsync(MainWindow window, string id) =>
        (Task)typeof(MainWindow).GetMethod("SwitchWorkspaceAsync", Private)!.Invoke(window, [id])!;

    private static async Task<MainWindow> ShowWindowAsync()
    {
        var window = new MainWindow([]);
        window.Show();
        await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed") && !window.IsVisible);
    }

    private static async Task SelectAsync(MainWindow window, StorageService storage, string label, string key)
    {
        await SelectAsync(window, label);
        Assert.Equal(key, (await storage.LoadSettingsAsync()).Theme);
    }

    private static async Task SelectAsync(MainWindow window, string label)
    {
        Options(window).Single(item => Equals(item.Header, label)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await HeadlessTests.WaitForAsync(() => Options(window).Any(item => item.IsChecked && Equals(item.Header, label)));
        AssertSelected(window, label);
    }

    private static IEnumerable<MenuItem> Options(MainWindow window) =>
        window.FindControl<Menu>("TopMenu")!.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "_Тема")).Items.OfType<MenuItem>();

    private static void AssertSelected(MainWindow window, string label) =>
        Assert.Equal(label, Assert.Single(Options(window), item => item.IsChecked).Header);

    private static Color Background(MainWindow window) => Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

    private static bool ReadFlag(MainWindow window, string name) =>
        (bool)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
}
