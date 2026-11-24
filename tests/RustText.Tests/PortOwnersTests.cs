using System.Diagnostics;
using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class PortOwnersTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void MatchesExactLocalPortInsteadOfRemotePortAndDeduplicatesBindings()
    {
        var rows = PortOwners.Parse("""
            TCP  127.0.0.1:18080  127.0.0.1:8080   ESTABLISHED  10
            TCP  [::1]:8080       [::]:0           LISTENING    20
            TCP  0.0.0.0:8080     0.0.0.0:0        BOUND        21
            UDP  0.0.0.0:8080     *:*                           30
            UDP  0.0.0.0:8080     *:*                           30
            """, 8080);

        Assert.Equal([20, 21, 30], rows.Select(row => row.Pid));
        Assert.Equal(IPAddress.IPv6Loopback, rows[0].Local.Address);
        Assert.Equal("BOUND", rows[1].State);
        Assert.Equal("UDP", rows[2].Protocol);
    }

    [Fact]
    public Task FindsAndStopsOnlyAConfirmedCurrentOwnerInTheSameWindow() => HeadlessTests.RunAsync(async profile =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "RustText.Tests.runtimeconfig.json"),
            typeof(LockFixture.Program).Assembly.Location, "--ports"
        })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            var line = await child.StandardOutput.ReadLineAsync(timeout.Token);
            var ports = Assert.IsType<string>(line).Split(' ').Select(int.Parse).ToArray();
            Assert.Equal(4, ports.Length);
            var path = Path.Combine(profile, "sample.txt");
            await File.WriteAllTextAsync(path, "Открытый файл", timeout.Token);
            var window = new MainWindow([path]);
            window.Show();
            try
            {
                await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
                var tabs = Assert.Single(window.GetVisualDescendants().OfType<WorkspaceTabs>());
                var document = Assert.IsType<TabItem>(tabs.SelectedItem);
                var menu = window.FindControl<Menu>("TopMenu")!;
                var open = Menu(Menu(menu, "_Инструменты"), "Поиск процессов");
                Click(open);
                var tab = Assert.IsType<TabItem>(tabs.SelectedItem);
                var finder = Assert.IsType<ProcessFinderView>(tab.Content);
                var modes = Assert.IsType<TabControl>(finder.Content);
                var modeItems = modes.Items.Cast<TabItem>().ToArray();
                Assert.Equal(new[] { "По порту", "По заголовку окна" }, modeItems.Select(item => item.Header));
                Assert.Same(modeItems[0], modes.SelectedItem);
                var view = Assert.IsType<PortOwnersView>(modeItems[0].Content);
                tabs.SelectedItem = document;
                Click(open);
                Assert.Same(tab, tabs.SelectedItem);
                Assert.Equal(2, tabs.Items.Count);
                window.UpdateLayout();

                var input = Assert.Single(view.GetVisualDescendants().OfType<TextBox>());
                var results = Assert.Single(view.GetVisualDescendants().OfType<ListBox>());
                var find = Button("Найти");
                var stop = Button("Завершить процесс…");
                Assert.False(stop.IsEnabled);
                input.Text = "65536";
                Click(find);
                await HeadlessTests.WaitForAsync(() => StatusContains("Неверный номер порта"));
                Assert.Empty(results.Items);

                var addresses = new[] { IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Loopback, IPAddress.IPv6Loopback };
                for (var i = 3; i >= 0; i--)
                {
                    var match = await SearchAsync(ports[i]);
                    Assert.Equal(i < 2 ? "TCP" : "UDP", match.Binding.Protocol);
                    Assert.Equal(addresses[i], match.Binding.Local.Address);
                    Assert.Equal(child.ProcessName, match.Owner.Name);
                    Assert.True(match.Owner.CanClose);
                }

                var portResult = results.SelectedItem;
                modes.SelectedItem = modeItems[1];
                window.UpdateLayout();
                var titleSearch = Assert.IsAssignableFrom<Control>(modeItems[1].Content);
                var titleQuery = Assert.Single(titleSearch.GetVisualDescendants().OfType<TextBox>());
                titleQuery.Text = $"RustText test title {Guid.NewGuid()}";
                Click(Assert.Single(titleSearch.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Найти")));
                Assert.Contains(titleSearch.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Окон найдено · 0");
                Assert.Empty(window.OwnedWindows);
                Assert.Same(tab, tabs.SelectedItem);
                modes.SelectedItem = modeItems[0];
                window.UpdateLayout();
                Assert.Equal(ports[0].ToString(), input.Text);
                Assert.Same(portResult, results.SelectedItem);

                Click(stop);
                Assert.Empty(window.OwnedWindows);
                Click(Button("Отмена"));
                Assert.False(child.HasExited);
                Assert.True(stop.IsEnabled);

                Click(stop);
                await child.StandardInput.WriteLineAsync("release");
                Assert.Equal("released", await child.StandardOutput.ReadLineAsync(timeout.Token));
                Click(Button("Завершить"));
                await HeadlessTests.WaitForAsync(() => StatusContains("больше не использует этот порт"));
                Assert.False(child.HasExited);

                var selected = await SearchAsync(ports[2]);
                await Assert.ThrowsAsync<InvalidOperationException>(() => PortOwners.StopAsync(
                    selected with { Owner = selected.Owner with { Started = selected.Owner.Started - 1 } }, timeout.Token));
                Assert.False(child.HasExited);
                Click(stop);
                Click(Button("Завершить"));
                await HeadlessTests.WaitForAsync(() => find.IsEnabled);
                Assert.True(child.HasExited);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    label => label.Text?.StartsWith($"Процесс с PID {child.Id} завершён.", StringComparison.Ordinal) == true);
                Assert.DoesNotContain(results.Items.OfType<PortOwners.Match>(), match => match.Owner.Id == child.Id);

                await ((Task)typeof(MainWindow).GetMethod("SaveSessionAsync", Private)!.Invoke(window, null)!);
                var storage = StorageService.OpenForTesting(profile);
                var session = Assert.IsType<SessionState>(await storage.LoadSessionAsync());
                Assert.Equal(path, Assert.Single(session.Tabs).Path);
                Click(Menu(Menu(menu, "_Файл"), "Закрыть вкладку   Ctrl+W"));
                await HeadlessTests.WaitForAsync(() => tabs.Items.Count == 1);
                Assert.Same(document, tabs.SelectedItem);
                Assert.Equal("Открытый файл", Assert.Single(window.GetVisualDescendants().OfType<TextEditor>()).Text);
                await ((Task)typeof(MainWindow).GetMethod("SaveSessionAsync", Private)!.Invoke(window, null)!);
                Assert.Empty((await storage.LoadSessionAsync())?.ClosedTabs ?? []);

                Button Button(string content) => Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                    button => Equals(button.Content, content));
                bool StatusContains(string text) => view.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.Text?.Contains(text, StringComparison.Ordinal) == true);
                async Task<PortOwners.Match> SearchAsync(int port)
                {
                    input.Text = port.ToString();
                    Click(find);
                    await HeadlessTests.WaitForAsync(() => results.Items.OfType<PortOwners.Match>()
                        .Any(match => match.Binding.Local.Port == port && match.Owner.Id == child.Id));
                    var match = Assert.Single(results.Items.OfType<PortOwners.Match>(), row => row.Owner.Id == child.Id);
                    results.SelectedItem = match;
                    return match;
                }
            }
            finally
            {
                window.Close();
                await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
            }
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.HasExited && !child.WaitForExit(5000)) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    private static bool ReadFlag(MainWindow window, string name) => (bool)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static MenuItem Menu(ItemsControl parent, string header) =>
        Assert.Single(parent.Items.OfType<MenuItem>(), item => Equals(item.Header, header));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
}
