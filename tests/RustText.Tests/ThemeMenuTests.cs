using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
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

    [Fact]
    public Task GlassPreviewsChromeAndPersistsAcrossWindowsAndRestarts() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        await File.WriteAllTextAsync(Path.Combine(storage.RootDirectory, "settings.json"),
            "{\"theme\":\"vice_city\"}");
        var first = await ShowWindowAsync();
        MainWindow? second = null;
        try
        {
            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(first, ["Тестовый документ"]);
            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(first, ["Вторая вкладка"]);
            var active = typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(first)!;
            typeof(MainWindow).GetMethod("OpenTabWindow", Private)!.Invoke(first, [active, null]);
            second = Assert.IsType<MainWindow>(first.FindOtherWindow());
            await HeadlessTests.WaitForAsync(() => ReadFlag(second, "_restoreFinished"));

            await HeadlessTests.WaitForAsync(() => second.IsVisible);
            Assert.DoesNotContain(Options(first), item => item.ToggleType == MenuItemToggleType.CheckBox);
            var off = WindowPixels(first);
            Assert.Equal(255, FocusTheme.TabBar.GradientStops[0].Color.A);
            Assert.Equal(0, FocusTheme.TitleMotifExtension.Color.A);
            var palmPixel = PalmExtensionPixel(first);

            var (dialog, slider) = await OpenGlassSliderAsync(first);
            Assert.Equal(0, slider.Value);
            slider.Value = 37;
            var soft = WindowPixels(first);
            Assert.NotEqual(off.Title, soft.Title);
            Assert.NotEqual(off.Tabs, soft.Tabs);
            Assert.Equal(soft.Title, soft.Tabs);
            Assert.Equal(soft, WindowPixels(second));
            Assert.True(FocusTheme.InactiveTab.Color.A < 255);
            Assert.NotEqual(0, FocusTheme.TitleMotifExtension.Color.A);
            var trunkPixel = PalmExtensionPixel(first);
            Assert.NotEqual(palmPixel, trunkPixel);
            Assert.Equal(FocusTheme.TitleMotifExtension.Color.G, (byte)(trunkPixel >> 8));
            Assert.NotEqual(off.Status, soft.Status);
            Assert.Equal(off.Editor, soft.Editor);
            slider.Value = 100;
            var strong = WindowPixels(first);
            Assert.NotEqual(soft.Title, strong.Title);
            Assert.Equal(off.Editor, strong.Editor);
            ClickDialogButton(dialog, "Отмена");
            await HeadlessTests.WaitForAsync(() => FocusTheme.TabBar.GradientStops[0].Color.A == 255);
            Assert.Equal(off, WindowPixels(first));

            (dialog, slider) = await OpenGlassSliderAsync(first);
            slider.Value = 37;
            ClickDialogButton(dialog, "Сохранить");
            await HeadlessTests.WaitForAsync(() => GlassSetting(first) == 37);
            Assert.Equal(37, (await storage.LoadSettingsAsync()).GlassIntensity);
            Assert.Equal(soft, WindowPixels(first));
            AssertSelected(first, "Vice City");

            (dialog, slider) = await OpenGlassSliderAsync(first);
            slider.Value = 0;
            ClickDialogButton(dialog, "Сохранить");
            await HeadlessTests.WaitForAsync(() => GlassSetting(first) == 0);
            Assert.Equal(0, (await storage.LoadSettingsAsync()).GlassIntensity);
            Assert.Equal(off, WindowPixels(first));

            (dialog, slider) = await OpenGlassSliderAsync(first);
            slider.Value = 37;
            ClickDialogButton(dialog, "Сохранить");
            await HeadlessTests.WaitForAsync(() => GlassSetting(first) == 37);
            Assert.Equal(soft, WindowPixels(first));
        }
        finally
        {
            foreach (var dialog in first.OwnedWindows.ToArray()) dialog.Close();
            if (second is not null) await CloseAsync(second);
            await CloseAsync(first);
        }

        var reopened = await ShowWindowAsync();
        try
        {
            var (dialog, slider) = await OpenGlassSliderAsync(reopened);
            Assert.Equal(37, slider.Value);
            ClickDialogButton(dialog, "Отмена");
        }
        finally
        {
            foreach (var dialog in reopened.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(reopened);
        }
    });

    private static async Task<(Window Dialog, Slider Slider)> OpenGlassSliderAsync(MainWindow window)
    {
        ThemeCommand(window, "Настройки тем…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Contains(dialog.GetVisualDescendants().OfType<ListBoxItem>(),
            item => AutomationProperties.GetAutomationId(item) == "SettingsSectionThemes" && item.IsSelected);
        return (dialog, Assert.Single(dialog.GetVisualDescendants().OfType<Slider>()));
    }

    private static int GlassSetting(MainWindow window) =>
        ((UserSettings)typeof(MainWindow).GetProperty("Settings", Private)!.GetValue(window)!).GlassIntensity;

    private static MenuItem ThemeCommand(MainWindow window, string header) =>
        Options(window).Single(item => Equals(item.Header, header));

    private static void ClickDialogButton(Window dialog, string label) =>
        dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static int PalmExtensionPixel(MainWindow window)
    {
        window.UpdateLayout();
        var palm = window.FindControl<Avalonia.Controls.Shapes.Path>("TitlePalmExtension")!;
        var point = palm.TranslatePoint(new Point(15, 8), window)!.Value;
        var scale = window.RenderScaling;
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            frame.CopyPixels(new PixelRect((int)(point.X * scale), (int)(point.Y * scale), 1, 1), buffer, 4, 4);
            return Marshal.ReadInt32(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static (int Title, int Tabs, int Status, int Editor) WindowPixels(MainWindow window)
    {
        window.UpdateLayout();
        var tabs = Assert.Single(window.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabBar");
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            int Read(Control control, double y)
            {
                var point = control.TranslatePoint(new Point(control.Bounds.Width * .7, y), window)!.Value;
                var x = (int)(point.X * window.RenderScaling);
                var row = (int)(point.Y * window.RenderScaling);
                frame.CopyPixels(new PixelRect(x, row, 1, 1), buffer, 4, 4);
                return Marshal.ReadInt32(buffer);
            }

            return (Read(window.FindControl<Border>("TitleBar")!, 12), Read(tabs, 12),
                Read(window.FindControl<Border>("StatusBar")!, 10), Read(window, 180));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

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
        Assert.Equal(label, Assert.Single(Options(window), item => item.ToggleType == MenuItemToggleType.Radio && item.IsChecked).Header);

    private static Color Background(MainWindow window) => Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

    private static bool ReadFlag(MainWindow window, string name) =>
        (bool)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
}
