using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using RustText.Demo;
using RustText.Demo.Editor;
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
    public Task GlassInThemeMenuPreviewsAndPersistsAcrossWindowsAndRestarts() => HeadlessTests.RunAsync(async profile =>
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
            var cursor = Assert.Single(Options(first), item => item.ToggleType == MenuItemToggleType.CheckBox);
            Assert.Equal("Плавный курсор", cursor.Header);
            Assert.True(cursor.IsChecked);
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(cursor)!;
            Assert.IsAssignableFrom<Avalonia.Automation.Provider.IToggleProvider>(peer).Toggle();
            await HeadlessTests.WaitForAsync(() => !cursor.IsChecked && !second.GetValue(EditorCaret.SmoothProperty));
            Assert.False((await storage.LoadSettingsAsync()).SmoothCaret);
            var editor = Assert.Single(first.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>());
            editor.TextArea.Focus();
            Assert.False(Assert.Single(editor.TextArea.TextView.Layers.OfType<EditorCaret>()).MotionEnabled);
            Assert.DoesNotContain(Options(first), item => Equals(item.Header, "Настройки тем…"));
            var off = WindowPixels(first);
            Assert.Equal(255, FocusTheme.TabBar.GradientStops[0].Color.A);
            AssertPalmsReachTabBarBottom(first);

            var (menu, slider) = await OpenGlassSliderAsync(first);
            Assert.Equal(0, slider.Value);
            var popup = TopLevel.GetTopLevel(slider)!;
            var thumb = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            var start = thumb.TranslatePoint(thumb.Bounds.Center - thumb.Bounds.Position, popup)!.Value;
            var end = start + new Vector(slider.Bounds.Width / 2, 0);
            popup.MouseMove(start);
            popup.MouseDown(start, MouseButton.Left);
            popup.MouseMove(end, RawInputModifiers.LeftMouseButton);
            popup.MouseUp(end, MouseButton.Left);
            Assert.InRange(slider.Value, 35, 75);
            Assert.True(menu.IsSubMenuOpen);
            var glassItem = Options(first).Single(item => AutomationProperties.GetName(item) == "Liquid Glass");
            Assert.True(glassItem.Focus());
            popup.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
            Assert.True(slider.IsFocused, $"Focus: {popup.FocusManager?.GetFocusedElement()}.");
            var dragged = slider.Value;
            popup.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, "");
            Assert.Equal(dragged + 1, slider.Value);
            slider.Value = 37;
            var soft = WindowPixels(first);
            Assert.NotEqual(off.Title, soft.Title);
            Assert.NotEqual(off.Tabs, soft.Tabs);
            Assert.Equal(soft.Title, soft.Tabs);
            Assert.Equal(soft, WindowPixels(second));
            Assert.True(FocusTheme.InactiveTab.Color.A < 255);
            AssertPalmsReachTabBarBottom(first);
            Assert.NotEqual(off.Status, soft.Status);
            Assert.Equal(off.Editor, soft.Editor);
            slider.Value = 100;
            var strong = WindowPixels(first);
            Assert.NotEqual(soft.Title, strong.Title);
            Assert.Equal(off.Editor, strong.Editor);
            Assert.Equal(0, (await storage.LoadSettingsAsync()).GlassIntensity);
            slider.Value = 37;
            popup.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
            Assert.False(menu.IsSubMenuOpen);
            await HeadlessTests.WaitForAsync(() => GlassSetting(first) == 37);
            Assert.Equal(37, (await storage.LoadSettingsAsync()).GlassIntensity);
            Assert.Equal(soft, WindowPixels(first));
            AssertSelected(first, "Vice City");

            (menu, slider) = await OpenGlassSliderAsync(second);
            Assert.Equal(37, slider.Value);
            slider.Value = 0;
            menu.IsSubMenuOpen = false;
            await HeadlessTests.WaitForAsync(() => GlassSetting(first) == 0);
            Assert.Equal(0, (await storage.LoadSettingsAsync()).GlassIntensity);
            Assert.Equal(off, WindowPixels(first));

            (_, slider) = await OpenGlassSliderAsync(first);
            slider.Value = 37;
            Assert.Equal(soft, WindowPixels(first));
        }
        finally
        {
            if (second is not null) await CloseAsync(second);
            await CloseAsync(first);
        }

        var reopened = await ShowWindowAsync();
        try
        {
            Assert.False(reopened.GetValue(EditorCaret.SmoothProperty));
            var (menu, slider) = await OpenGlassSliderAsync(reopened);
            Assert.Equal(37, slider.Value);
            menu.IsSubMenuOpen = false;
        }
        finally
        {
            await CloseAsync(reopened);
        }
    });

    private static async Task<(MenuItem Menu, Slider Slider)> OpenGlassSliderAsync(MainWindow window)
    {
        var menu = ThemeMenu(window);
        var item = Assert.Single(Options(window), item => AutomationProperties.GetName(item) == "Liquid Glass");
        var slider = Assert.Single(Assert.IsType<StackPanel>(item.Header).Children.OfType<Slider>());
        window.UpdateLayout();
        var point = menu.TranslatePoint(new Point(menu.Bounds.Width / 2, menu.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.True(menu.IsSubMenuOpen);
        await HeadlessTests.WaitForAsync(() => slider.Bounds.Width > 0 && TopLevel.GetTopLevel(slider) is not null);
        return (menu, slider);
    }

    private static int GlassSetting(MainWindow window) =>
        ((UserSettings)typeof(MainWindow).GetProperty("Settings", Private)!.GetValue(window)!).GlassIntensity;

    private static void AssertPalmsReachTabBarBottom(MainWindow window)
    {
        window.UpdateLayout();
        var palm = window.FindControl<Avalonia.Controls.Shapes.Path>("TitlePalm")!;
        var tabs = Assert.Single(window.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabBar");
        var origin = palm.TranslatePoint(default, window)!.Value;
        var top = tabs.TranslatePoint(default, window)!.Value.Y;
        var bottom = top + tabs.Bounds.Height;
        var ink = FocusTheme.TitleMotif.Color;
        var scale = window.RenderScaling;
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var rgb = frame.Format == PixelFormats.Rgba8888
            ? (ink.B << 16) | (ink.G << 8) | ink.R
            : (ink.R << 16) | (ink.G << 8) | ink.B;
        var row = new int[(int)(palm.Bounds.Width * scale)];
        var bytes = row.Length * 4;
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            int TrunksAt(double y)
            {
                frame.CopyPixels(new PixelRect((int)(origin.X * scale), (int)(y * scale), row.Length, 1), buffer, bytes, bytes);
                Marshal.Copy(buffer, row, 0, row.Length);
                var trunks = 0;
                var insideTrunk = false;
                foreach (var pixel in row)
                {
                    var isTrunk = (pixel & 0xffffff) == rgb;
                    if (isTrunk && !insideTrunk) trunks++;
                    insideTrunk = isTrunk;
                }
                return trunks;
            }
            for (var y = top - 1; y < bottom; y++)
                Assert.True(TrunksAt(y) == 2,
                    $"Expected two trunks at y={y} in the tab strip {top}–{bottom}.");
            Assert.Equal(0, TrunksAt(bottom + 1));
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

    private static MenuItem ThemeMenu(MainWindow window) =>
        window.FindControl<Menu>("TopMenu")!.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "_Тема"));

    private static IEnumerable<MenuItem> Options(MainWindow window) => ThemeMenu(window).Items.OfType<MenuItem>();

    private static void AssertSelected(MainWindow window, string label) =>
        Assert.Equal(label, Assert.Single(Options(window), item => item.ToggleType == MenuItemToggleType.Radio && item.IsChecked).Header);

    private static Color Background(MainWindow window) => Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

    private static bool ReadFlag(MainWindow window, string name) =>
        (bool)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
}
