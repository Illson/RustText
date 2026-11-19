using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class HistoryMenuTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task FileAndToolsMenusSeparateClosedFilesFromUntitledDraftsAndKeepHomeSections() =>
        HeadlessTests.RunAsync(async _ =>
        {
            var window = new MainWindow([]);
            window.Show();
            await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
            try
            {
                var closed = Field<List<SessionTab>>(window, "_closedTabs");
                closed.Add(new SessionTab(8001, Path.Combine(Path.GetTempPath(), "report.txt"), TextEncodingKind.Utf8));
                closed.Add(new SessionTab(8002, null, TextEncodingKind.Utf8) { UntitledName = "ghost" });
                Invoke(window, "RefreshHistoryMenus");

                var closedFiles = Field<MenuItem>(window, "_closedFilesMenu");
                var closedDrafts = Field<MenuItem>(window, "_closedDraftsMenu");
                Assert.Contains(closedFiles.Items.OfType<MenuItem>(), item => item.Header?.ToString()?.StartsWith("report.txt", StringComparison.Ordinal) == true);
                Assert.DoesNotContain(closedFiles.Items.OfType<MenuItem>(), item => item.Header?.ToString()?.Contains("ghost", StringComparison.Ordinal) == true);
                Assert.Single(closedDrafts.Items.OfType<MenuItem>());
                Assert.StartsWith("ghost", closedDrafts.Items.OfType<MenuItem>().Single().Header?.ToString());

                var topMenu = window.FindControl<Menu>("TopMenu")!;
                var fileMenu = topMenu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.Contains("Файл", StringComparison.Ordinal) == true);
                var toolsMenu = topMenu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.Contains("Инструменты", StringComparison.Ordinal) == true);
                Assert.Contains(fileMenu.Items, item => ReferenceEquals(item, closedFiles));
                Assert.DoesNotContain(fileMenu.Items, item => ReferenceEquals(item, closedDrafts));
                Assert.Contains(toolsMenu.Items, item => ReferenceEquals(item, closedDrafts));

                await (Task)Invoke(window, "ShowHomeAsync")!;
                await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.Text == "Призрачные вкладки"));
                var headings = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
                Assert.Contains("Призрачные вкладки", headings);
                Assert.Contains("Закрытые файлы", headings);
                Assert.Contains("Недавние файлы", headings);
            }
            finally
            {
                window.Close();
                await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_closed"));
            }
        });

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static object? Invoke(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
}
