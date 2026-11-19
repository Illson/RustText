using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class MainWindowTabDoubleClickTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task BatchOpenShowsAllLoadingHeadersBeforeReadingFiles() => HeadlessTests.RunAsync(async profile =>
    {
        var paths = new[] { Path.Combine(profile, "one.txt"), Path.Combine(profile, "two.txt"), Path.Combine(profile, "three.txt") };
        foreach (var path in paths) await File.WriteAllTextAsync(path, path);
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
        try
        {
            var open = (Task)Invoke(window, "OpenPathsAsync", paths, false, null)!;
            var tabs = ((System.Collections.IDictionary)Field<object>(window, "_tabs")).Values.Cast<object>().ToArray();
            Assert.Equal(3, tabs.Length);
            Assert.All(tabs, tab =>
            {
                var mark = (TextBlock)Property(tab, "LoadMark")!;
                Assert.True(mark.IsVisible);
                Assert.Equal("↻", mark.Text);
                Assert.Null(Property(tab, "Editor"));
            });
            await open;
            Assert.All(tabs, tab =>
            {
                Assert.NotNull(Property(tab, "Editor"));
                Assert.False(((TextBlock)Property(tab, "LoadMark")!).IsVisible);
            });
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task DragFromTabPaddingAnimatesReorderingAndCanBeCancelledOrDetached() => HeadlessTests.RunAsync(async _ =>
    {
        var window = new MainWindow { Width = 1050, Height = 720 };
        window.Show();
        await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
        var workspace = Field<object>(window, "_workspace");
        var windows = (System.Collections.IList)Property(workspace, "Windows")!;
        try
        {
            foreach (var text in new[] { "first tab", "second tab", "third tab" }) Invoke(window, "NewTab", text);
            var tabs = (WorkspaceTabs)Property(Field<object>(window, "_active"), "Tabs")!;
            var original = tabs.Items.OfType<TabItem>().ToArray();
            await HeadlessTests.WaitForAsync(() => original.All(tab => tab.Bounds.Width > 0));
            Point Padding(TabItem tab) => tab.TranslatePoint(new Point(3, tab.Bounds.Height - 3), window)!.Value;
            var afterLast = original[^1].TranslatePoint(new Point(original[^1].Bounds.Width + 12, 16), window)!.Value;
            var start = Padding(original[0]);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(afterLast, RawInputModifiers.LeftMouseButton);
            var drag = workspace.GetType().GetField("Drag")!.GetValue(workspace);
            Assert.NotNull(drag);
            Assert.True(((Window)Property(drag, "Preview")!).IsVisible);
            await HeadlessTests.WaitForAsync(() => original[1].RenderTransform is TranslateTransform { X: < -1 });
            window.MouseUp(afterLast, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(new[] { original[1], original[2], original[0] }, tabs.Items.OfType<TabItem>());

            await HeadlessTests.WaitForAsync(() => Padding(original[0]).X > Padding(original[2]).X);
            var outside = new Point(window.Bounds.Width + 120, Padding(original[0]).Y + 90);
            start = Padding(original[0]);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            drag = workspace.GetType().GetField("Drag")!.GetValue(workspace);
            Assert.NotNull(drag);
            var preview = (Window)Property(drag, "Preview")!;
            Assert.Equal("first tab  ●", Assert.IsType<TextBlock>(Assert.IsType<Border>(preview.Content).Child).Text);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
            window.MouseUp(outside, MouseButton.Left, RawInputModifiers.None);
            Assert.False(preview.IsVisible);
            Assert.Single(windows);

            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            Assert.NotNull(workspace.GetType().GetField("Drag")!.GetValue(workspace));
            Assert.Null(Invoke(window, "DropWindowAt", window.PointToScreen(outside)));
            window.MouseUp(outside, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(2, windows.Count);
            Assert.Equal(2, tabs.Items.Count);
            var detached = Assert.IsType<MainWindow>(windows[1]);
            Assert.Equal(1, TabCount(detached));
        }
        finally
        {
            foreach (var open in windows.Cast<MainWindow>().Reverse().ToArray()) await CloseAsync(open);
        }
    });

    [Fact]
    public Task DoubleClickOnBlankTabStripCreatesTabButContentAndToolbarDoNot() =>
        HeadlessTests.RunAsync(async _ =>
        {
            var window = new MainWindow([]) { Width = 1100, Height = 760 };
            window.Show();
            await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
            try
            {
                Invoke(window, "NewTab", "tab under test");
                var group = Field<object>(window, "_active");
                var tabs = (WorkspaceTabs)group.GetType().GetProperty("Tabs")!.GetValue(group)!;
                var bar = Assert.Single(tabs.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabBar");
                var actions = Assert.IsType<StackPanel>(tabs.HeaderActions);
                await HeadlessTests.WaitForAsync(() => bar.Bounds.Width > 500 && bar.Bounds.Height > 0
                    && tabs.Items.OfType<TabItem>().Any(item => item.Bounds.Width > 0));

                var lastTabRight = tabs.Items.OfType<TabItem>()
                    .Select(item => item.TranslatePoint(new Point(item.Bounds.Width, item.Bounds.Height / 2), window)!.Value.X)
                    .Max();
                var firstActionLeft = actions.Children.OfType<Button>()
                    .Select(button => button.TranslatePoint(new Point(0, button.Bounds.Height / 2), window)!.Value.X)
                    .Min();
                Assert.True(firstActionLeft - lastTabRight > 20,
                    $"Expected blank tab-strip space between tabs and toolbar, got {firstActionLeft - lastTabRight:0.#}px.");
                var barOrigin = bar.TranslatePoint(new Point(0, 0), window)!.Value;
                var blankPoint = new Point((lastTabRight + firstActionLeft) / 2, barOrigin.Y + bar.Bounds.Height / 2);

                var initialCount = TabCount(window);
                DoubleClick(window, blankPoint);
                await HeadlessTests.WaitForAsync(() => TabCount(window) == initialCount + 1);

                var afterBlankDoubleClick = TabCount(window);
                var contentPoint = new Point(barOrigin.X + 100, barOrigin.Y + bar.Bounds.Height + 80);
                DoubleClick(window, contentPoint);
                await Task.Delay(60);
                Assert.Equal(afterBlankDoubleClick, TabCount(window));

                var restoreButton = Assert.Single(actions.Children.OfType<Button>(), button =>
                    Equals(ToolTip.GetTip(button), "Восстановить закрытую вкладку"));
                var restorePoint = restoreButton.TranslatePoint(
                    new Point(restoreButton.Bounds.Width / 2, restoreButton.Bounds.Height / 2), window)!.Value;
                DoubleClick(window, restorePoint);
                await Task.Delay(60);
                Assert.Equal(afterBlankDoubleClick, TabCount(window));
            }
            finally
            {
                window.Close();
                await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_closed"));
            }
        });

    [Fact]
    public Task DoubleClickOnTabPaddingClosesThatTab() => HeadlessTests.RunAsync(async _ =>
    {
        var window = new MainWindow([]) { Width = 1100, Height = 760 };
        window.Show();
        await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
        try
        {
            Invoke(window, "NewTab", "first tab");
            Invoke(window, "NewTab", "second tab");
            var group = Field<object>(window, "_active");
            var tabs = (WorkspaceTabs)group.GetType().GetProperty("Tabs")!.GetValue(group)!;
            await HeadlessTests.WaitForAsync(() => tabs.Items.OfType<TabItem>().Count() == 2
                && tabs.Items.OfType<TabItem>().All(item => item.Bounds.Width > 0));

            var tab = tabs.Items.OfType<TabItem>().First();
            var surface = Assert.Single(tab.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabSurface");
            var point = surface.TranslatePoint(new Point(surface.Bounds.Width / 2, 1), window)!.Value;
            Assert.Equal(2, TabCount(window));

            DoubleClick(window, point);
            await HeadlessTests.WaitForAsync(() => TabCount(window) == 1);
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_closed"));
        }
    });

    private static void DoubleClick(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static int TabCount(MainWindow window) => ((System.Collections.IDictionary)Field<object>(window, "_tabs")).Count;

    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner);

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_closed"));
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static object? Invoke(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
}
