using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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

    [Fact]
    public Task TitleMenuCreatesAnEmptyWorkspaceAndRestoresItsBadgeAfterRestart() => HeadlessTests.RunAsync(async profile =>
    {
        var catalog = new WorkspaceCatalogStore(profile);
        await catalog.CreateAsync("Существующее пространство");
        var window = await OpenWindowAsync();
        try
        {
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Рабочие пространства");
            var menu = await OpenMenuAsync(window);
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item).StartsWith("По умолчанию", StringComparison.Ordinal));
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item) == "Существующее пространство");
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item) == "Пустое окно");
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item) == "Управление…");
            menu.Hide();

            const string originalDraft = "Документ в исходном пространстве";
            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(window, [originalDraft]);
            menu = await OpenMenuAsync(window);
            var newWorkspace = menu.Items.OfType<MenuItem>().Single(item => Header(item) == "Новая рабочая область…");
            menu.Hide();
            newWorkspace.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("Новая рабочая область", dialog.Title);
            FindInput<TextBox>(dialog, "WorkspaceNameInput").Text = "Логи сборки";
            FindInput<ComboBox>(dialog, "WorkspaceColorInput").SelectedItem = "Сиреневый";
            var symbol = FindInput<TextBox>(dialog, "WorkspaceSymbolInput");
            symbol.Text = "AB";
            var create = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Создать"));
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.IsVisible);
            Assert.DoesNotContain((await catalog.LoadAsync()).Workspaces, entry => entry.Name == "Логи сборки");

            symbol.Text = "★";
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => !dialog.IsVisible && BadgeSymbol(window) == "★");
            AssertBadge(window);
            Assert.Null(ActiveEditor(window));
            var saved = (await catalog.LoadAsync()).Workspaces.Single(entry => entry.Name == "Логи сборки");
            Assert.Equal("★", saved.Symbol);
            Assert.Equal("#BAA0F3", saved.Color);

            await CloseWindowAsync(window);
            window = await OpenWindowAsync();
            AssertBadge(window);
            menu = await OpenMenuAsync(window);
            var defaultWorkspace = menu.Items.OfType<MenuItem>().Single(item => Header(item) == "По умолчанию");
            menu.Hide();
            defaultWorkspace.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessTests.WaitForAsync(() => ActiveEditor(window)?.Text == originalDraft);
        }
        finally { if (window.IsVisible) await CloseWindowAsync(window); }
    });

    private static async Task<MainWindow> OpenWindowAsync()
    {
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => window.FindControl<Button>("WorkspaceMenuButton")!.IsEnabled);
        return window;
    }

    private static async Task<MenuFlyout> OpenMenuAsync(MainWindow window)
    {
        var button = window.FindControl<Button>("WorkspaceMenuButton")!;
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        window.UpdateLayout();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await HeadlessTests.WaitForAsync(() => flyout.IsOpen);
        return flyout;
    }

    private static string Header(MenuItem item) => item.Header is TextBlock text ? text.Text! : (string)item.Header!;

    private static T FindInput<T>(Window dialog, string id) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);

    private static string? BadgeSymbol(MainWindow window) => window.FindControl<TextBlock>("WorkspaceBadgeSymbol")!.Text;

    private static void AssertBadge(MainWindow window)
    {
        Assert.Equal("★", BadgeSymbol(window));
        var outline = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindControl<Border>("WorkspaceBadge")!.BorderBrush);
        Assert.Equal(Color.Parse("#BAA0F3"), outline.Color);
        Assert.Contains("Логи сборки", AutomationProperties.GetName(window.FindControl<Button>("WorkspaceMenuButton")!));
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
