using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class WorkspaceTabsScrollTests
{
    [Fact]
    public Task MouseWheelScrollsOverflowingHeadersWithoutChangingSelection() => HeadlessTests.RunAsync(async _ =>
    {
        var tabs = new WorkspaceTabs
        {
            Width = 640,
            Height = 320,
            HeaderActions = new Button { Content = "Actions", Width = 72 }
        };
        var firstContent = new TextBlock { Text = "Tab content" };
        var firstTab = new TabItem { Header = "First tab", Content = firstContent };
        tabs.Items.Add(firstTab);
        tabs.SelectedItem = firstTab;

        var window = new Window
        {
            Width = 640,
            Height = 320,
            Content = tabs
        };

        try
        {
            window.Show();
            await HeadlessTests.WaitForAsync(() => tabs.HeaderViewport is
                { Bounds.Width: > 0, Bounds.Height: > 0 });
            var viewport = Assert.IsType<ScrollViewer>(tabs.HeaderViewport);
            var position = Assert.Single(tabs.GetVisualDescendants().OfType<Track>(),
                track => track.Name == "TabScrollPosition");
            var oneTabHeaderHeight = viewport.Bounds.Height;
            Assert.False(position.IsVisible);
            Assert.False(position.IsHitTestVisible);
            Assert.True(position.ClipToBounds);
            Assert.False(viewport.AllowAutoHide);

            for (var index = 2; index <= 12; index++)
            {
                tabs.Items.Add(new TabItem
                {
                    Header = $"Overflow tab {index}",
                    Content = new TextBlock { Text = $"Content {index}" }
                });
            }

            await HeadlessTests.WaitForAsync(() => viewport.Extent.Width > viewport.Viewport.Width);
            await HeadlessTests.WaitForAsync(() => position.IsVisible && position.Maximum > 0);
            Assert.Equal(2, position.Bounds.Height);
            Assert.Equal(viewport.Bounds.Bottom, position.Bounds.Top);
            Assert.Equal(viewport.Bounds.Width, position.Bounds.Width);
            Assert.Equal(viewport.ScrollBarMaximum.X, position.Maximum, precision: 3);
            Assert.Equal(viewport.Viewport.Width, position.ViewportSize, precision: 3);
            Assert.Equal(0, position.Value, precision: 3);

            var thumb = Assert.IsType<Thumb>(position.Thumb);
            var expectedThumbWidth = position.Bounds.Width * position.ViewportSize
                / (position.Maximum + position.ViewportSize);
            Assert.InRange(Math.Abs(thumb.Bounds.Width - expectedThumbWidth), 0, 1.5);

            window.MouseMove(new Point(-10, -10));
            await Task.Delay(40);
            var indicatorPixels = CaptureIndicatorPixels(window, position);
            var indicatorOrigin = position.TranslatePoint(default, window);
            Assert.NotNull(indicatorOrigin);
            var thumbCenter = thumb.TranslatePoint(
                new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window);
            Assert.NotNull(thumbCenter);

            window.MouseMove(thumbCenter!.Value);
            await Task.Delay(150);
            Assert.Equal(indicatorPixels, CaptureIndicatorPixels(window, position));
            Assert.False(position.IsPointerOver);
            Assert.False(thumb.IsPointerOver);
            Assert.DoesNotContain(viewport.GetVisualDescendants().OfType<ScrollBar>(),
                scrollBar => scrollBar.Orientation == Orientation.Horizontal && scrollBar.IsVisible);

            window.MouseMove(new Point(thumbCenter.Value.X, indicatorOrigin!.Value.Y - 2));
            await Task.Delay(150);
            Assert.Equal(indicatorPixels, CaptureIndicatorPixels(window, position));
            Assert.DoesNotContain(viewport.GetVisualDescendants().OfType<ScrollBar>(),
                scrollBar => scrollBar.Orientation == Orientation.Horizontal && scrollBar.IsVisible);

            var selection = tabs.SelectedItem;
            var viewportCenter = viewport.TranslatePoint(
                new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window);
            Assert.NotNull(viewportCenter);

            var initialOffset = viewport.Offset.X;
            window.MouseWheel(viewportCenter!.Value, new Vector(0, -1));
            window.UpdateLayout();
            Assert.True(viewport.Offset.X > initialOffset);
            Assert.Equal(viewport.Offset.X, position.Value, precision: 3);
            Assert.Same(selection, tabs.SelectedItem);
            Assert.True(thumb.Bounds.X > 0);

            var scrolledOffset = viewport.Offset.X;
            window.MouseWheel(viewportCenter.Value, new Vector(0, 1));
            Assert.True(viewport.Offset.X < scrolledOffset);
            Assert.Same(selection, tabs.SelectedItem);

            window.MouseWheel(viewportCenter.Value, new Vector(0, -50));
            window.UpdateLayout();
            Assert.Equal(position.Maximum, position.Value);
            Assert.InRange(Math.Abs(thumb.Bounds.Right - position.Bounds.Width), 0, 1.5);

            window.MouseWheel(viewportCenter.Value, new Vector(0, 50));
            window.UpdateLayout();
            Assert.Equal(0, position.Value);
            Assert.InRange(Math.Abs(thumb.Bounds.X), 0, 1.5);

            var lastTab = tabs.Items.OfType<TabItem>().Last();
            var clippedEdge = lastTab.TranslatePoint(
                new Point(lastTab.Bounds.Width, lastTab.Bounds.Height / 2), window);
            var viewportEdge = viewport.TranslatePoint(new Point(viewport.Bounds.Width, 0), window);
            Assert.NotNull(clippedEdge);
            Assert.NotNull(viewportEdge);
            Assert.True(clippedEdge!.Value.X > viewportEdge!.Value.X);
            Assert.Equal(ScrollBarVisibility.Hidden, viewport.HorizontalScrollBarVisibility);
            Assert.DoesNotContain(viewport.GetVisualDescendants().OfType<ScrollBar>(),
                scrollBar => scrollBar.Orientation == Orientation.Horizontal && scrollBar.IsVisible);
            Assert.Equal(oneTabHeaderHeight, viewport.Bounds.Height);

            var actions = Assert.IsType<Button>(tabs.HeaderActions);
            var actionsCenter = actions.TranslatePoint(
                new Point(actions.Bounds.Width / 2, actions.Bounds.Height / 2), window);
            Assert.NotNull(actionsCenter);
            var beforeActionsWheel = viewport.Offset.X;
            window.MouseWheel(actionsCenter!.Value, new Vector(0, -1));
            Assert.Equal(beforeActionsWheel, viewport.Offset.X);

            var contentCenter = firstContent.TranslatePoint(
                new Point(firstContent.Bounds.Width / 2, firstContent.Bounds.Height / 2), window);
            Assert.NotNull(contentCenter);
            var beforeContentWheel = viewport.Offset.X;
            window.MouseWheel(contentCenter!.Value, new Vector(0, -1));
            Assert.Equal(beforeContentWheel, viewport.Offset.X);

            var originalIndicatorWidth = position.Bounds.Width;
            tabs.Width -= 120;
            window.UpdateLayout();
            Assert.True(position.Bounds.Width < originalIndicatorWidth);
            Assert.Equal(viewport.Viewport.Width, position.ViewportSize, precision: 3);

            foreach (var extraTab in tabs.Items.Skip(1).ToArray()) tabs.Items.Remove(extraTab);
            await HeadlessTests.WaitForAsync(() => !position.IsVisible);
            Assert.Equal(0, position.Maximum);
        }
        finally
        {
            window.Close();
        }
    });

    private static byte[] CaptureIndicatorPixels(Window window, Track track)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var origin = track.TranslatePoint(default, window)!.Value;
        var scaling = window.RenderScaling;
        var left = (int)Math.Round(origin.X * scaling);
        var top = (int)Math.Round(origin.Y * scaling);
        var width = (int)Math.Ceiling(track.Bounds.Width * scaling);
        var height = (int)Math.Ceiling(track.Bounds.Height * scaling);
        var pixels = new byte[width * height * 4];
        var address = Marshal.AllocHGlobal(pixels.Length);
        try
        {
            frame.CopyPixels(new PixelRect(left, top, width, height), address, pixels.Length, width * 4);
            Marshal.Copy(address, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(address);
        }
    }
}
