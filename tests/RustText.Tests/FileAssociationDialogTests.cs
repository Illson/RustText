using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class FileAssociationDialogTests
{
    [Fact]
    public Task ReturnsSpecificFormatsOnlyOnConfirmation() => HeadlessTests.RunAsync(async _ =>
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var original = new HashSet<string> { ".txt" };
            Assert.Null(await ChooseAsync("Отмена"));
            var selected = Assert.IsAssignableFrom<IReadOnlySet<string>>(await ChooseAsync("Сохранить и открыть параметры"));
            Assert.Equal(".jsonl", Assert.Single(selected));
            Assert.Equal(".txt", Assert.Single(original));

            async Task<IReadOnlySet<string>?> ChooseAsync(string action)
            {
                var pending = MainWindow.ChooseFileAssociationsAsync(owner, original);
                var dialog = Assert.Single(owner.OwnedWindows);
                dialog.UpdateLayout();
                var formats = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
                var txt = Assert.Single(formats, format => Equals(format.Content, ".txt"));
                var jsonl = Assert.Single(formats, format => Equals(format.Content, ".jsonl"));
                Assert.True(txt.IsChecked);
                Assert.False(jsonl.IsChecked);
                txt.IsChecked = false;
                jsonl.IsChecked = true;
                var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, action));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return await pending;
            }
        }
        finally
        {
            foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close();
            owner.Close();
        }
    });
}
