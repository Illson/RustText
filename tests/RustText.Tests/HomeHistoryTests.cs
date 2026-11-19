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
            Assert.Contains(HomeButtons(window), button => AutomationProperties.GetName(button) == caption);

            await CloseAsync(window);
            window = await OpenWorkspaceAsync();

            await HeadlessTests.WaitForAsync(() => HasButton(window, caption));
            var closedDraft = FindButton(window, caption);
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

    [Fact]
    public Task HistoryCanCollapseAndRemoveEntriesWithoutDeletingAnOrdinaryFile() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        var path = Path.Combine(profile, "recent.txt");
        await File.WriteAllBytesAsync(path, new byte[2048]);
        await storage.AddRecentAsync(new RecentFileEntry(path, TextEncodingKind.Utf8));
        await storage.SaveSessionAsync(new SessionState([], [new SessionTab(11, path, TextEncodingKind.Utf8)]));
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<Expander>().Count() == 3);
            var headings = window.GetVisualDescendants().OfType<Expander>().ToArray();
            Assert.Equal(new[] { "Недавние файлы", "Закрытые файлы", "Призрачные вкладки" }, headings.Select(AutomationProperties.GetName));
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "2 КиБ"));
            headings[0].IsExpanded = false;
            await CallAsync(window, "ShowHomeAsync");
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<Expander>().Count() == 3);
            var recent = window.GetVisualDescendants().OfType<Expander>().First();
            Assert.False(recent.IsExpanded);
            recent.IsExpanded = true;
            await HeadlessTests.WaitForAsync(() => HasButton(window, "Удалить из недавних: recent.txt"));
            Click(FindButton(window, "Удалить из недавних: recent.txt"));
            await HeadlessTests.WaitForAsync(() => !HasButton(window, "Удалить из недавних: recent.txt"));
            Assert.Empty(await storage.GetRecentFilesAsync());
            Click(FindButton(window, "Удалить из истории: recent.txt"));
            await HeadlessTests.WaitForAsync(() => !HasButton(window, "Удалить из истории: recent.txt"));
            Assert.Empty((await storage.LoadSessionAsync())!.ClosedTabs!);
            Assert.Equal(2048, new FileInfo(path).Length);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ConfirmedDraftDeletionReleasesItsFileAndSurvivesCheckpointRecovery() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        var deleted = await storage.SaveClosedDraftAsync(new SessionTab(27, null, TextEncodingKind.Utf8) { UntitledName = "Удаляемый" }, new string('x', 4096));
        var kept = await storage.SaveClosedDraftAsync(new SessionTab(28, null, TextEncodingKind.Utf8) { UntitledName = "Соседний" }, "keep this draft");
        var saved = await storage.SaveSessionAsync(new SessionState([], [deleted, kept]));
        var file = storage.GetDraftPath(saved.ClosedTabs!.Single(item => item.Id == deleted.Id))!;
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "4 КиБ"));
            Click(FindButton(window, "Удалить сохранённый черновик: Удаляемый"));
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count > 0);
            var dialog = Assert.Single(window.OwnedWindows);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Отмена")));
            Assert.True(File.Exists(file));
            var operations = (HashSet<long>)Property(Field(window, "_workspace")!, "ClosedTabOperations")!;
            await HeadlessTests.WaitForAsync(() => !operations.Contains(deleted.Id));

            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", "Несохранённый текст из последней сессии");
            await CallAsync(window, "ShowHomeAsync");
            await HeadlessTests.WaitForAsync(() => HasButton(window, "Удалить сохранённый черновик: Удаляемый"));
            Click(FindButton(window, "Удалить сохранённый черновик: Удаляемый"));
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count > 0);
            dialog = Assert.Single(window.OwnedWindows);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Удалить")));
            await HeadlessTests.WaitForAsync(() => !HasButton(window, "Удалить сохранённый черновик: Удаляемый"));
            Assert.False(File.Exists(file));
            var checkpoints = Path.Combine(storage.RootDirectory, "session");
            var current = (await File.ReadAllTextAsync(Path.Combine(checkpoints, "latest.json"))).Trim();
            Assert.Single((await storage.LoadSessionAsync())!.Tabs);
            var manifest = Path.Combine(checkpoints, current, "manifest.json");
            await File.WriteAllTextAsync(manifest, "corrupt checkpoint");
            var backup = (await storage.LoadSessionAsync())!;
            Assert.Empty(backup.Tabs);
            var recovered = Assert.Single(backup.ClosedTabs!);
            Assert.Equal(kept.Id, recovered.Id);
            Assert.Equal("keep this draft", await storage.LoadDraftAsync(recovered));

            await CloseAsync(window);
            var reopened = (await storage.LoadSessionAsync())!;
            Assert.Equal("Несохранённый текст из последней сессии", await storage.LoadDraftAsync(Assert.Single(reopened.Tabs)));
            Assert.Equal(kept.Id, Assert.Single(reopened.ClosedTabs!).Id);
        }
        finally { if (window.IsVisible) await CloseAsync(window); }
    });

    private static IEnumerable<Button> HomeButtons(MainWindow window) =>
        window.GetVisualDescendants().OfType<Button>();

    private static Button FindButton(MainWindow window, string label) =>
        Assert.Single(HomeButtons(window), button => AutomationProperties.GetName(button) == label);

    private static bool HasButton(MainWindow window, string label) =>
        HomeButtons(window).Any(button => AutomationProperties.GetName(button) == label);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
