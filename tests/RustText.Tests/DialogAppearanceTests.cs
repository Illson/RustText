using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using RustText.Demo;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class DialogAppearanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task CommonDialogsUseTheSharedWindowChrome() => HeadlessTests.RunAsync(async _ =>
    {
        var owner = await OpenWorkspaceAsync();
        var body = new StackPanel
        {
            Children = { new TextBlock { Text = "Dialog body" } }
        };
        var dialog = MainWindow.Dialog("Проверка оформления окна", body);
        try
        {
            dialog.Show(owner);
            await HeadlessTests.WaitForAsync(() => FindByIdOrNull<Control>(dialog, "DialogChromeRoot") is not null);

            var root = FindById<Control>(dialog, "DialogChromeRoot");
            Assert.Equal(WindowDecorations.BorderOnly, dialog.WindowDecorations);
            Assert.Equal("Проверка оформления окна",
                FindById<TextBlock>(dialog, "DialogChromeTitle").Text);
            Assert.NotNull(FindById<Control>(dialog, "DialogChromeTitleBar"));
            Assert.NotNull(FindById<Control>(dialog, "DialogChromeDragRegion"));
            Assert.NotNull(FindById<Button>(dialog, "DialogChromeMaximize"));
            Assert.NotNull(FindById<Button>(dialog, "DialogChromeClose"));
            Assert.Null(FindByIdOrNull<Button>(dialog, "DialogChromeMinimize"));
            Assert.Contains(body, root.GetVisualDescendants());
        }
        finally
        {
            dialog.Close();
            await CloseAsync(owner);
        }
    });

    [Fact]
    public Task SettingsSaveAndCheckboxKeepDarkPaletteOnHover() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            var showSettings = CallAsync(window, "ShowSettingsAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.OfType<Window>()
                .Any(dialog => AutomationProperties.GetAutomationId(dialog) == "SettingsDialog"));
            var dialog = window.OwnedWindows.OfType<Window>()
                .Single(item => AutomationProperties.GetAutomationId(item) == "SettingsDialog");

            var save = FindById<Button>(dialog, "SettingsSave");
            Assert.Equal(FocusTheme.Selection.Color, ColorOf(save.Background));
            Assert.Equal(FocusTheme.Text.Color, ColorOf(save.Foreground));
            Assert.Equal(FocusTheme.ActionBorder.Color, ColorOf(save.BorderBrush));
            var presenter = save.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(item => item.Name == "PART_ContentPresenter");
            Assert.Equal(FocusTheme.Selection.Color, ColorOf(presenter.Background));

            FindById<ListBoxItem>(dialog, "SettingsSectionDraftArchive").IsSelected = true;
            var checkbox = FindById<CheckBox>(dialog, "SettingsShowDraftLines");
            dialog.UpdateLayout();
            var box = CheckboxBox(checkbox);
            var checkGlyph = CheckboxGlyph(checkbox);
            checkbox.IsChecked = false;
            dialog.UpdateLayout();
            Assert.Equal(FocusTheme.Field.Color, ColorOf(box.Background));

            checkbox.IsChecked = true;
            dialog.UpdateLayout();
            Assert.Equal(FocusTheme.Selection.Color, ColorOf(box.Background));
            Assert.Equal(FocusTheme.Text.Color, ColorOf(checkGlyph.Fill));

            var checkboxCenter = checkbox.TranslatePoint(
                new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), dialog)!.Value;
            await HeadlessTests.WaitForAsync(() =>
            {
                dialog.MouseMove(checkboxCenter);
                return checkbox.IsPointerOver;
            });
            dialog.UpdateLayout();
            Assert.Equal(FocusTheme.Selection.Color, ColorOf(box.Background));
            Assert.Equal(FocusTheme.ControlOutline.Color, ColorOf(box.BorderBrush));
            Assert.Equal(FocusTheme.Text.Color, ColorOf(checkGlyph.Fill));

            var saveCenter = save.TranslatePoint(
                new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), dialog)!.Value;
            await HeadlessTests.WaitForAsync(() =>
            {
                dialog.MouseMove(saveCenter);
                return save.IsPointerOver;
            });
            dialog.UpdateLayout();
            Assert.Equal(FocusTheme.Selection.Color, ColorOf(presenter.Background));
            Assert.Equal(FocusTheme.Text.Color, ColorOf(presenter.Foreground));
            Assert.Equal(FocusTheme.ControlOutline.Color, ColorOf(presenter.BorderBrush));

            dialog.Close();
            await showSettings;
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task WorkspaceCreateButtonUsesDarkPrimarySurface() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            var showCreate = CallAsync(window, "ShowCreateWorkspaceAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any());
            var dialog = Assert.Single(window.OwnedWindows);
            var create = dialog.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Создать"));
            Assert.Equal(FocusTheme.Selection.Color, ColorOf(create.Background));
            Assert.Equal(FocusTheme.Text.Color, ColorOf(create.Foreground));

            dialog.Close();
            await showCreate;
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    private static Border CheckboxBox(CheckBox checkbox) => checkbox.GetVisualDescendants().OfType<Border>()
        .Single(part => part.Name == "NormalRectangle");

    private static Path CheckboxGlyph(CheckBox checkbox) => checkbox.GetVisualDescendants().OfType<Path>()
        .Single(part => part.Name == "CheckGlyph");

    private static T FindById<T>(Control root, string id) where T : Control =>
        FindByIdOrNull<T>(root, id) ?? throw new InvalidOperationException($"Control '{id}' was not found.");

    private static T? FindByIdOrNull<T>(Control root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>()
            .SingleOrDefault(control => AutomationProperties.GetAutomationId(control) == id);

    private static Color ColorOf(IBrush? brush) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static async Task<MainWindow> OpenWorkspaceAsync()
    {
        var window = new MainWindow([]);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
    }

    private static Task CallAsync(MainWindow window, string name) =>
        (Task)typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null)!;

    private static object Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
}
