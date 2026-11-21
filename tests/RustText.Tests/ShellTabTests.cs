using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class ShellTabTests
{
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
