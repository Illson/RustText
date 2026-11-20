using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class WorkspaceMenuPublicationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CreatedWorkspaceIsPublishedWhenDirtyFilePreventsSwitching(bool copyCurrent) => HeadlessTests.RunAsync(async profile =>
    {
        const string name = "Published workspace";
        var catalog = new WorkspaceCatalogStore(profile);
        var secondWorkspace = await catalog.CreateAsync("Already open");
        var path = Path.Combine(profile, "dirty-source.txt");
        await File.WriteAllTextAsync(path, "before");
        var window = new MainWindow([path]);
        MainWindow? other = null;
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            await InvokeAsync(window, "OpenWorkspaceWindowAsync", secondWorkspace.Id);
            other = Assert.IsType<MainWindow>(typeof(MainWindow).GetMethod("FindOtherWindow", Private)!.Invoke(window, null));
            await HeadlessTests.WaitForAsync(() => (bool)Field(other, "_restoreFinished"));
            var activeTab = typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(window)!;
            var editor = (TextEditor)activeTab.GetType().GetProperty("Editor")!.GetValue(activeTab)!;
            editor.Document.Insert(editor.Document.TextLength, " + unsaved");
            Assert.True((bool)activeTab.GetType().GetProperty("Dirty")!.GetValue(activeTab)!);

            var createFlow = copyCurrent
                ? InvokeAsync(window, "CreateWorkspaceFromCurrentAsync", name)
                : InvokeAsync(window, "ShowCreateWorkspaceAsync");
            if (!copyCurrent)
            {
                await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog => dialog.Title == "Новая рабочая область"));
                var createDialog = window.OwnedWindows.Single(dialog => dialog.Title == "Новая рабочая область");
                var input = createDialog.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => AutomationProperties.GetAutomationId(control) == "WorkspaceNameInput");
                input.Text = name;
                Click(FindButton(createDialog, "Создать"));
            }

            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog =>
                dialog.Title == "Переключить рабочее пространство?"));
            var switchDialog = window.OwnedWindows.Single(dialog => dialog.Title == "Переключить рабочее пространство?");
            Click(FindButton(switchDialog, "Отмена"));
            await createFlow;

            Assert.Equal(WorkspaceCatalogStore.DefaultWorkspaceId, Field(window, "_workspaceId"));
            Assert.Equal("before + unsaved", editor.Text);
            Assert.Equal("before", await File.ReadAllTextAsync(path));
            foreach (var openWindow in new[] { window, other })
            {
                var menu = openWindow.FindControl<MenuItem>("WorkspaceMenuButton")!;
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => Header(item) == name);
            }
            Assert.Contains((await catalog.LoadAsync()).Workspaces, item => item.Name == name);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            if (other is not null)
            {
                other.Close();
                await HeadlessTests.WaitForAsync(() => !other.IsVisible);
            }
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    private static string Header(MenuItem item) => item.Header is TextBlock text ? text.Text! : (string)item.Header!;

    private static Button FindButton(Window dialog, string caption) => dialog.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.Content, caption));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Task InvokeAsync(MainWindow window, string method, params object[] arguments) =>
        (Task)typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, arguments)!;

    private static object Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
}
