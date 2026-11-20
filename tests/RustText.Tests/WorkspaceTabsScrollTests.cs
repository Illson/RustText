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
            var oneTabHeaderHeight = viewport.Bounds.Height;

            for (var index = 2; index <= 12; index++)
            {
                tabs.Items.Add(new TabItem
                {
                    Header = $"Overflow tab {index}",
                    Content = new TextBlock { Text = $"Content {index}" }
                });
            }

            await HeadlessTests.WaitForAsync(() => viewport.Extent.Width > viewport.Viewport.Width);
            var selection = tabs.SelectedItem;
            var viewportCenter = viewport.TranslatePoint(
                new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window);
            Assert.NotNull(viewportCenter);

            var initialOffset = viewport.Offset.X;
            window.MouseWheel(viewportCenter!.Value, new Vector(0, -1));
            Assert.True(viewport.Offset.X > initialOffset);
            Assert.Same(selection, tabs.SelectedItem);

            var scrolledOffset = viewport.Offset.X;
            window.MouseWheel(viewportCenter.Value, new Vector(0, 1));
            Assert.True(viewport.Offset.X < scrolledOffset);
            Assert.Same(selection, tabs.SelectedItem);

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
        }
        finally
        {
            window.Close();
        }
    });
}
