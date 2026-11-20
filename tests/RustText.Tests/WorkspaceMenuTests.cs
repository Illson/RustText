using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class WorkspaceMenuTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, "#BAA0F3")]
    [InlineData(true, "#377562")]
    public Task TitleMenuCreatesAnEmptyWorkspaceAndRestoresItsBadgeAfterRestart(bool customColor, string expectedColor) => HeadlessTests.RunAsync(async profile =>
    {
        var catalog = new WorkspaceCatalogStore(profile);
        await catalog.CreateAsync("Существующее пространство");
        var window = await OpenWindowAsync();
        try
        {
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Рабочие пространства");
            var fileMenu = window.FindControl<Menu>("TopMenu")!.Items.OfType<MenuItem>()
                .Single(item => Header(item) == "_Файл");
            Assert.DoesNotContain(fileMenu.Items.OfType<MenuItem>(), item => Header(item) == "Рабочие пространства…");

            var menu = await OpenMenuAsync(window);
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item).StartsWith("По умолчанию", StringComparison.Ordinal));
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item) == "Существующее пространство");
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item) == "Пустое окно");
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item) == "Управление…");
            menu.IsSubMenuOpen = false;

            const string originalDraft = "Документ в исходном пространстве";
            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(window, [originalDraft]);
            menu = await OpenMenuAsync(window);
            var newWorkspace = menu.Items.OfType<MenuItem>().Single(item => Header(item) == "Новая рабочая область…");
            menu.IsSubMenuOpen = false;
            newWorkspace.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("Новая рабочая область", dialog.Title);
            FindInput<TextBox>(dialog, "WorkspaceNameInput").Text = "Логи сборки";
            var color = FindInput<ColorView>(dialog, "WorkspaceColorInput");
            Assert.False(color.IsVisible);
            var swatch = FindInput<ToggleButton>(dialog, "WorkspaceColor-BAA0F3");
            Assert.Equal("Сиреневый", AutomationProperties.GetName(swatch));
            Assert.True(swatch.Focus());
            dialog.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            dialog.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.True(swatch.IsChecked);
            Assert.Equal(Color.Parse("#BAA0F3"), color.Color);
            Assert.False(FindInput<ToggleButton>(dialog, "WorkspaceColor-75A9FF").IsChecked);
            if (customColor)
            {
                var custom = FindInput<Button>(dialog, "WorkspaceCustomColor");
                ClickControl(dialog, custom);
                Assert.True(color.IsVisible);
                Assert.Contains(color.GetVisualDescendants().OfType<ColorSpectrum>(), spectrum => spectrum.IsVisible);
                ClickControl(dialog, swatch);
                Assert.False(color.IsVisible);
                Assert.True(swatch.IsChecked);
                ClickControl(dialog, custom);
                color.Color = Color.Parse(expectedColor);
                Assert.False(swatch.IsChecked);
            }

            var preview = FindInput<Border>(dialog, "WorkspaceBadgePreview");
            Assert.Equal(Color.Parse(expectedColor), Assert.IsAssignableFrom<ISolidColorBrush>(preview.BorderBrush).Color);
            var symbol = FindInput<TextBox>(dialog, "WorkspaceSymbolInput");
            symbol.Text = "AB";
            var create = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Создать"));
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.IsVisible);
            Assert.DoesNotContain((await catalog.LoadAsync()).Workspaces, entry => entry.Name == "Логи сборки");

            symbol.Text = "★";
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => !dialog.IsVisible && BadgeSymbol(window) == "★");
            AssertBadge(window, expectedColor);
            Assert.Null(ActiveEditor(window));
            var saved = (await catalog.LoadAsync()).Workspaces.Single(entry => entry.Name == "Логи сборки");
            Assert.Equal("★", saved.Symbol);
            Assert.Equal(expectedColor, saved.Color);

            await CloseWindowAsync(window);
            window = await OpenWindowAsync();
            AssertBadge(window, expectedColor);
            menu = await OpenMenuAsync(window);
            var defaultWorkspace = menu.Items.OfType<MenuItem>().Single(item => Header(item) == "По умолчанию");
            menu.IsSubMenuOpen = false;
            defaultWorkspace.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessTests.WaitForAsync(() => ActiveEditor(window)?.Text == originalDraft);
        }
        finally { if (window.IsVisible) await CloseWindowAsync(window); }
    });

    private static async Task<MainWindow> OpenWindowAsync()
    {
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => window.FindControl<MenuItem>("WorkspaceMenuButton")!.IsEnabled);
        return window;
    }

    private static async Task<MenuItem> OpenMenuAsync(MainWindow window)
    {
        var menu = window.FindControl<MenuItem>("WorkspaceMenuButton")!;
        ClickControl(window, menu);
        await HeadlessTests.WaitForAsync(() => menu.IsSubMenuOpen);
        return menu;
    }

    private static void ClickControl(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static string Header(MenuItem item) => item.Header is TextBlock text ? text.Text! : (string)item.Header!;

    private static T FindInput<T>(Window dialog, string id) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);

    private static string? BadgeSymbol(MainWindow window) => window.FindControl<TextBlock>("WorkspaceBadgeSymbol")!.Text;

    private static void AssertBadge(MainWindow window, string expectedColor)
    {
        Assert.Equal("★", BadgeSymbol(window));
        var outline = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindControl<Border>("WorkspaceBadge")!.BorderBrush);
        Assert.Equal(Color.Parse(expectedColor), outline.Color);
        Assert.Contains("Логи сборки", AutomationProperties.GetName(window.FindControl<MenuItem>("WorkspaceMenuButton")!));
    }

    private static TextEditor? ActiveEditor(MainWindow window)
    {
        var tab = typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(window);
        return (TextEditor?)tab?.GetType().GetProperty("Editor")!.GetValue(tab);
    }

    private static async Task CloseWindowAsync(MainWindow window)
    {
        foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
    }
}
