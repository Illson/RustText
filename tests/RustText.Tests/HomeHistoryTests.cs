using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class HomeHistoryTests
{
    [Fact]
    public Task ClosedUntitledDraftAppearsOnHomeAndRestoresAfterRestart() => HeadlessTests.RunAsync(async _ =>
    {
        const string caption = "Черновик";
        const string draft = caption + "\nнесохранённый текст";
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", draft);
            await CallAsync(window, "CloseTabAsync", ActiveTab(window));

            await HeadlessTests.WaitForAsync(() =>
                window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Призрачные вкладки"));
            Assert.Contains(HomeButtons(window), button => button.Content is string label && label.StartsWith(caption, StringComparison.Ordinal));

            await CloseAsync(window);
            window = await OpenWorkspaceAsync();

            var closedDraft = Assert.Single(HomeButtons(window), button =>
                button.Content is string label && label.StartsWith(caption, StringComparison.Ordinal));
            closedDraft.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await HeadlessTests.WaitForAsync(() => ActiveEditor(window) is { Text: draft });
            Assert.Empty((System.Collections.IEnumerable)Field(window, "_closedTabs")!);
        }
        finally
        {
            if (window.IsVisible)
            {
                await CloseAsync(window);
            }
        }
    });

    private static IEnumerable<Button> HomeButtons(MainWindow window) =>
        window.GetVisualDescendants().OfType<Button>();

    private static TextEditor? ActiveEditor(MainWindow window) => Property(ActiveTab(window), "Editor") as TextEditor;

    private static async Task<MainWindow> OpenWorkspaceAsync()
    {
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished")!);
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible ||
            window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Не удалось записать сессию.", StringComparison.Ordinal) == true);
        Assert.False(window.IsVisible, window.FindControl<TextBlock>("StatusText")?.Text);
    }

    private static object ActiveTab(MainWindow window) => Property(window, "ActiveTab")!;

    private static object? Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static Task CallAsync(MainWindow window, string name, params object?[] args) =>
        (Task)Invoke(window, name, args)!;

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
}
