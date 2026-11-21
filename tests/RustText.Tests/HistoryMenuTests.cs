using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
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
                var fileMenu = topMenu.Items.OfType<MenuItem>().Single(item => MenuHeader(item).Contains("Файл", StringComparison.Ordinal));
                var toolsMenu = topMenu.Items.OfType<MenuItem>().Single(item => MenuHeader(item).Contains("Инструменты", StringComparison.Ordinal));
                Assert.Contains(fileMenu.Items, item => ReferenceEquals(item, closedFiles));
                Assert.DoesNotContain(fileMenu.Items, item => ReferenceEquals(item, closedDrafts));

                var toolsGroups = toolsMenu.Items.OfType<MenuItem>().ToArray();
                Assert.Equal(new[]
                {
                    "Редактор и просмотр", "Форматирование и данные", "История и черновики", "Обновления",
                    "Найти окно процесса…", "Прокси-чекер…", "Настройки…"
                }, toolsGroups.Select(MenuHeader));

                var editorGroup = toolsGroups.Single(item => MenuHeader(item) == "Редактор и просмотр");
                Assert.Equal(new[] { "Перейти к строке…   Ctrl+G", "Перенос строк", "Масштаб текста" }, MenuLabels(editorGroup));
                var zoomMenu = editorGroup.Items.OfType<MenuItem>().Single(item => MenuHeader(item) == "Масштаб текста");
                Assert.Equal(new[] { "Увеличить", "Уменьшить", "Сбросить" }, MenuLabels(zoomMenu));

                var dataGroup = toolsGroups.Single(item => MenuHeader(item) == "Форматирование и данные");
                Assert.Equal(new[] { "Форматировать JSON   Ctrl+Shift+J", "Включить проверку JSON", "Генератор строк…", "Сравнить списки…" },
                    MenuLabels(dataGroup));
                var historyGroup = toolsGroups.Single(item => MenuHeader(item) == "История и черновики");
                Assert.Contains(historyGroup.Items, item => ReferenceEquals(item, closedDrafts));
                Assert.Equal(new[] { "Версии файла…", "Призрачные вкладки", "Архив черновиков…" }, MenuLabels(historyGroup));
                var updatesGroup = toolsGroups.Single(item => MenuHeader(item) == "Обновления");
                Assert.Equal(new[] { "Проверить обновления…", "История выпусков…" }, MenuLabels(updatesGroup));

                var finderCommand = toolsGroups.Single(item => MenuHeader(item) == "Найти окно процесса…");
                finderCommand.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog => dialog.Title == "Найти окно процесса"));
                var finderDialog = window.OwnedWindows.Single(dialog => dialog.Title == "Найти окно процесса");
                finderDialog.Close();
                await HeadlessTests.WaitForAsync(() => !finderDialog.IsVisible);

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

    private static string MenuHeader(MenuItem item) => item.Header switch
    {
        TextBlock text => text.Text ?? string.Empty,
        _ => item.Header?.ToString() ?? string.Empty
    };

    private static string[] MenuLabels(MenuItem group) => group.Items.OfType<MenuItem>().Select(MenuHeader).ToArray();
}
