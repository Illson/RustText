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
        Options(window).Single(item => Equals(item.Header, label)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await HeadlessTests.WaitForAsync(() => Options(window).Any(item => item.IsChecked && Equals(item.Header, label)));
        Assert.Equal(key, (await storage.LoadSettingsAsync()).Theme);
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
