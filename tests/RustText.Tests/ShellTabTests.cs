using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class ShellTabTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task EarlyShellDoesNotReplaceSavedDraft() => HeadlessTests.RunAsync(async profile =>
    {
        const string text = "Несохранённый черновик";
        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSessionAsync(new SessionState([new SessionTab(2, null, TextEncodingKind.Utf8)]),
            new Dictionary<long, string> { [2] = text });

        for (var start = 0; start < 2; start++)
        {
            var window = new MainWindow([]);
            window.Show();
            try
            {
                if (start == 0)
                {
                    Assert.False(Flag("_restoreFinished"));
                    await (Task)typeof(MainWindow).GetMethod("OpenShellAsync", Private)!.Invoke(window, [ShellKind.Cmd])!;
                }
                await HeadlessTests.WaitForAsync(() => Flag("_restoreFinished"));
                Assert.Equal(text, Assert.Single(window.GetVisualDescendants().OfType<TextEditor>()).Text);
            }
            finally
            {
                window.Close();
                await HeadlessTests.WaitForAsync(() => Flag("_closed"));
            }

            bool Flag(string field) => (bool)typeof(MainWindow).GetField(field, Private)!.GetValue(window)!;
        }
    });

    [Fact]
    public Task TrimmingOutputDoesNotRetainItForUndo() => HeadlessTests.RunAsync(profile =>
    {
        using var shell = new ShellTab(ShellKind.Cmd, profile, 14);
        var document = Assert.Single(shell.GetLogicalDescendants().OfType<TextEditor>()).Document;
        var append = typeof(ShellTab).GetMethod("Append", Private)!;
        var chunk = new string('x', 1023) + "\n";
        append.Invoke(shell, ["first-marker\n"]);
        for (var i = 0; i < 800; i++) append.Invoke(shell, [chunk]);
        append.Invoke(shell, ["last-marker\n"]);
        Assert.DoesNotContain("first-marker", document.Text);
        Assert.EndsWith("last-marker\n", document.Text);
        Assert.False(document.UndoStack.CanUndo);
        return Task.CompletedTask;
    });

    [Fact]
    public Task CmdDoesNotExecuteLossyInputAndStillAcceptsRepresentableText() => HeadlessTests.RunAsync(async profile =>
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            using var shell = new ShellTab(ShellKind.Cmd, profile, 14);
            var window = new Window { Content = shell };
            window.Show();
            try
            {
                var output = Assert.Single(shell.GetLogicalDescendants().OfType<TextEditor>());
                var input = Assert.Single(shell.GetLogicalDescendants().OfType<TextBox>());
                await HeadlessTests.WaitForAsync(() => output.Text.Contains('>'));
                const string rejected = "echo 漢字>unexpected.txt";
                input.Text = rejected;
                shell.FocusInput();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
                Assert.Equal(rejected, input.Text);
                Assert.Contains("866", output.Text);
                input.Text = "mkdir Привет & exit 0";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
                await HeadlessTests.WaitForAsync(() => !input.IsEnabled);
                Assert.True(Directory.Exists(Path.Combine(profile, "Привет")));
                Assert.False(File.Exists(Path.Combine(profile, "unexpected.txt")));
            }
            finally { window.Close(); }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    });

    [Fact]
    public Task SessionExitIsReportedAfterTheShellOutput() => HeadlessTests.RunAsync(async profile =>
    {
        const string startupMessage = "Сеанс запущен";
        const string exitMessage = "[Терминал] Сеанс завершён · код 0";

        using var shell = new ShellTab(ShellKind.Cmd, profile, 14);
        var window = new Window { Content = shell };
        window.Show();
        try
        {
            var output = Assert.Single(shell.GetLogicalDescendants().OfType<TextEditor>());
            var input = Assert.Single(shell.GetLogicalDescendants().OfType<TextBox>());
            await HeadlessTests.WaitForAsync(() => output.Text.Contains('>'));
            Assert.DoesNotContain(startupMessage, output.Text);
            Assert.DoesNotContain(shell.GetLogicalDescendants().OfType<TextBlock>(),
                label => label.Text == startupMessage);

            input.Text = "echo rusttext-shell-marker & exit 0";
            shell.FocusInput();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");

            await HeadlessTests.WaitForAsync(() => !input.IsEnabled);
            var lines = output.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("rusttext-shell-marker", lines[^2]);
            Assert.Equal(exitMessage, lines[^1]);
        }
        finally
        {
            window.Close();
        }
    });
}
