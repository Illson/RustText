using System.Reflection;
using Avalonia.Controls;
using AvaloniaEdit;
using RustText.Demo;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class ConcurrentPageSaveTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task PageSaveUsesTheCooperativeLockAndPreservesEditsOnConflict() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "shared.log");
        var original = "original page\nsecond row\n";
        await File.WriteAllTextAsync(path, original);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var originalStamp = await DocumentFiles.GetStampAsync(path);
        var log = new LogView(path, state: new LogTabState(AutoRefresh: false));
        var window = new Window { Width = 900, Height = 700, Content = log };
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => Page(log) is not null);
            await InvokeTask(log, "TogglePageEditAsync");
            var editor = PageEditor(log);
            editor.Text = "edited page\nsecond row\n";
            Assert.True(log.HasPendingEdit);

            using (DocumentFiles.AcquireSaveLock(path))
            {
                await log.SavePageEditAsync();
                Assert.True(log.HasPendingEdit);
                Assert.Equal("edited page\nsecond row\n", editor.Text);
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
                Assert.Contains("занят", log.StatusText, StringComparison.OrdinalIgnoreCase);
            }

            const string normalSaveText = "normal save from another profile\n";
            var normalSave = DocumentFiles.SaveAsync(path, normalSaveText, TextEncodingKind.Utf8,
                LineEndingKind.Lf, originalStamp);
            await log.SavePageEditAsync();
            var normalError = await Record.ExceptionAsync(() => normalSave);
            if (log.HasPendingEdit)
            {
                Assert.Null(normalError);
                Assert.Equal("edited page\nsecond row\n", editor.Text);
                Assert.Equal(normalSaveText, await File.ReadAllTextAsync(path));
                await log.SavePageEditAsync();
                Assert.True(log.HasPendingEdit);
                Assert.Contains("изменился", log.StatusText, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(normalSaveText, await File.ReadAllTextAsync(path));
            }
            else
            {
                Assert.IsType<IOException>(normalError);
                Assert.Equal("edited page\nsecond row\n", await File.ReadAllTextAsync(path));
            }
            Assert.Empty(Directory.EnumerateFiles(profile, ".rusttext-page-*.pending"));
            Assert.Empty(Directory.EnumerateFiles(profile, ".rusttext-save-*.lock"));
        }
        finally
        {
            log.Dispose();
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    private static LogPage? Page(LogView log) => (LogPage?)Field(log, "_page");
    private static TextEditor PageEditor(LogView log) => (TextEditor)Field(log, "_pageEditor");
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static Task InvokeTask(object owner, string name, params object?[] arguments) =>
        (Task)(owner.GetType().GetMethod(name, Private)!.Invoke(owner, arguments)
            ?? throw new InvalidOperationException($"{name} returned null."));
}
