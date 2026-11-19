using System.Reflection;
using Avalonia.Controls;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class IdleUnloadTests
{
    [Fact]
    public Task IdleUnloadPersistsAndRehydratesDirtyBackgroundEditor() => HeadlessTests.RunAsync(async profile =>
    {
        var firstPath = Path.Combine(profile, "first.txt");
        var secondPath = Path.Combine(profile, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first document");
        await File.WriteAllTextAsync(secondPath, "visible document");
        var window = await OpenWorkspaceAsync(firstPath, secondPath);
        try
        {
            var tab = Tabs(window).Single(item => Equals(Property(item, "Path"), firstPath));
            var editor = (TextEditor)Property(tab, "Editor")!;
            editor.Text += " with unsaved edits";
            SetProperty(tab, "LastWorked", DateTimeOffset.Now - TimeSpan.FromMinutes(10));

            await CallAsync(window, "UnloadIdleEditorsAsync");

            Assert.Null(Property(tab, "Editor"));
            Assert.Equal(0, editor.Document.TextLength);
            var deferred = Assert.IsType<SessionTab>(Property(tab, "Deferred"));
            Assert.NotNull(deferred.DraftRef);
            var storage = StorageService.OpenForTesting(profile);
            Assert.Equal("first document with unsaved edits", await storage.LoadDraftAsync(deferred));

            await CallAsync(window, "ActivateDeferredAsync", tab);
            var restored = Assert.IsType<TextEditor>(Property(tab, "Editor"));
            Assert.Equal("first document with unsaved edits", restored.Text);
            Assert.True((bool)Property(tab, "Dirty")!);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    private static async Task<MainWindow> OpenWorkspaceAsync(params string[] paths)
    {
        var window = new MainWindow(paths);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished")!);
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
    }

    private static IEnumerable<object> Tabs(MainWindow window) =>
        ((System.Collections.IDictionary)Field(window, "_tabs")!).Values.Cast<object>();

    private static object? Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(target);

    private static void SetProperty(object target, string name, object value) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(target, value);

    private static Task CallAsync(MainWindow window, string name, params object?[] args) =>
        (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
}
