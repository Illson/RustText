using System.Collections.Specialized;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class ProxyCheckerTabTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task LargeResultBacklogYieldsToInputBeforeEnablingAnotherRun() => HeadlessTests.RunAsync(async _ =>
    {
        using var view = new ProxyCheckerView();
        var window = new Window { Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            var input = Assert.Single(view.GetVisualDescendants().OfType<TextEditor>());
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>());
            var start = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Проверить"));
            const int count = 10_000;
            input.Text = string.Join('\n', Enumerable.Repeat("invalid", count));
            var processedAtInput = -1;
            var canRestartAtInput = false;
            ((INotifyCollectionChanged)list.ItemsSource!).CollectionChanged += OnFirstResult;
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => start.IsEnabled && processedAtInput != -1);
            Assert.InRange(processedAtInput, 1, count - 1);
            Assert.False(canRestartAtInput);
            Assert.Equal(count, list.Items.Count);
            input.Text = "invalid";
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => start.IsEnabled);
            Assert.Single(list.Items);

            void OnFirstResult(object? sender, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action != NotifyCollectionChangedAction.Add) return;
                ((INotifyCollectionChanged)sender!).CollectionChanged -= OnFirstResult;
                Dispatcher.UIThread.Post(() =>
                {
                    processedAtInput = list.Items.Count;
                    canRestartAtInput = start.IsEnabled;
                }, DispatcherPriority.Input);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MenuSelectsOneTransientTabAndClosingReturnsToTheDocument() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "sample.txt");
        const string originalText = "Текст открытого файла";
        await File.WriteAllTextAsync(path, originalText);
        var window = new MainWindow([path]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
            var tabs = Assert.Single(window.GetVisualDescendants().OfType<WorkspaceTabs>());
            var document = Assert.IsType<TabItem>(tabs.SelectedItem);
            var editor = Assert.Single(window.GetVisualDescendants().OfType<TextEditor>());
            Assert.Equal(originalText, editor.Text);

            var topMenu = window.FindControl<Menu>("TopMenu")!;
            var tools = FindMenuItem(topMenu, "_Инструменты");
            var file = FindMenuItem(topMenu, "_Файл");
            var openChecker = FindMenuItem(tools, "Прокси-чекер…");
            Click(openChecker);

            var checker = Assert.IsType<TabItem>(tabs.SelectedItem);
            var view = Assert.IsType<ProxyCheckerView>(checker.Content);
            Assert.Equal(2, tabs.Items.Count);
            await HeadlessTests.WaitForAsync(() => view.GetVisualDescendants().OfType<TextEditor>().Any());
            var input = Assert.Single(view.GetVisualDescendants().OfType<TextEditor>());
            var protocol = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
            var region = Assert.Single(view.GetVisualDescendants().OfType<CheckBox>(), c => c.Name == "ProxyCheckerLookupRegion");
            var parallelism = Assert.Single(view.GetVisualDescendants().OfType<NumericUpDown>(), c => c.Name == "ProxyCheckerParallelism");
            Assert.Equal((decimal?)8, parallelism.Value);
            parallelism.Value = 128;
            Assert.Equal((decimal?)128, parallelism.Value);
            Assert.False(region.IsChecked);
            Assert.Equal(new[] { "HTTP", "HTTPS", "SOCKS5" }, protocol.Items.Cast<string>().ToArray());
            Assert.Equal("HTTP", protocol.SelectedItem);
            const string proxyList = "192.0.2.10:1080\nhttp://192.0.2.20:3128";
            input.Text = proxyList;
            protocol.SelectedIndex = 2;

            tabs.SelectedItem = document;
            Click(openChecker);
            Assert.Same(checker, tabs.SelectedItem);
            Assert.Same(view, checker.Content);
            Assert.Equal(2, tabs.Items.Count);
            Assert.Equal(proxyList, input.Text);
            Assert.Equal("SOCKS5", protocol.SelectedItem);

            await SaveSessionAsync(window);
            var storage = StorageService.OpenForTesting(profile);
            var saved = Assert.IsType<SessionState>(await storage.LoadSessionAsync());
            var savedDocument = Assert.Single(saved.Tabs);
            Assert.Equal(path, savedDocument.Path);
            Assert.Equal(new[] { savedDocument.Id }, saved.Layout?.Tabs);

            Click(FindMenuItem(file, "Закрыть вкладку   Ctrl+W"));
            await HeadlessTests.WaitForAsync(() => tabs.Items.Count == 1);
            Assert.Same(document, tabs.SelectedItem);
            Assert.Equal(originalText, editor.Text);

            await SaveSessionAsync(window);
            saved = Assert.IsType<SessionState>(await storage.LoadSessionAsync());
            Assert.Empty(saved.ClosedTabs ?? []);
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
        }
    });

    [Fact]
    public Task SwitchingFiltersAfterRowsAreRenderedRecyclesTheResultList() => HeadlessTests.RunAsync(async _ =>
    {
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
            var tools = FindMenuItem(window.FindControl<Menu>("TopMenu")!, "_Инструменты");
            Click(FindMenuItem(tools, "Прокси-чекер…"));
            window.UpdateLayout();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ProxyCheckerView>());
            var input = Assert.Single(view.GetVisualDescendants().OfType<TextEditor>());
            var results = Assert.Single(view.GetVisualDescendants().OfType<ListBox>());
            input.Text = "192.0.2.18\nftp://198.51.100.42:8080";
            ClickButton("Проверить");
            await HeadlessTests.WaitForAsync(() => results.Items.Count == 2);
            window.UpdateLayout();
            Assert.Contains(results.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Строка 1");

            ClickButton("Работают 0");
            Assert.Empty(results.Items);
            ClickButton("Ошибки 2");
            Assert.Equal(2, results.Items.Count);
            window.UpdateLayout();
            ClickButton("Задержка ↕");
            Assert.Equal(2, results.Items.Count);

            void ClickButton(string text) => Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
        }
    });

    private static bool ReadFlag(MainWindow window, string name) =>
        (bool)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;

    private static Task SaveSessionAsync(MainWindow window) =>
        (Task)typeof(MainWindow).GetMethod("SaveSessionAsync", Private)!.Invoke(window, null)!;

    private static MenuItem FindMenuItem(ItemsControl menu, string header) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, header));

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
}
