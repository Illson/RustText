using System.Reflection;
using Avalonia.Media;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class HyperlinkStyleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task EditorLinksUseTheCurrentThemeBrush() => HeadlessTests.RunAsync(async _ =>
    {
        var window = new MainWindow([]) { Width = 1000, Height = 720 };
        window.Show();
        await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
        try
        {
            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(window,
                ["https://example.com\ncontact@example.com"]);
            var tab = typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(window)!;
            var editor = (TextEditor)tab.GetType().GetProperty("Editor")!.GetValue(tab)!;
            var textView = editor.TextArea.TextView;

            await HeadlessTests.WaitForAsync(() =>
                ReferenceEquals(FocusTheme.Link, textView.LinkTextForegroundBrush)
                && textView.VisualLinesValid && textView.VisualLines.Count >= 2);

            Assert.Same(FocusTheme.Link, textView.LinkTextForegroundBrush);
            Assert.Equal(Color.Parse("#72C7E6"), ((SolidColorBrush)textView.LinkTextForegroundBrush).Color);

            var before = textView.VisualLines[0];
            FocusTheme.Apply(AppTheme.Neon);
            await HeadlessTests.WaitForAsync(() => textView.VisualLinesValid && textView.VisualLines.Count >= 2
                && !ReferenceEquals(before, textView.VisualLines[0]));

            Assert.Same(FocusTheme.Link, textView.LinkTextForegroundBrush);
            Assert.Equal(Color.Parse("#76CFFF"), ((SolidColorBrush)textView.LinkTextForegroundBrush).Color);
        }
        finally
        {
            FocusTheme.Apply(AppTheme.Focus);
            window.Close();
            await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_closed"));
        }
    });

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
}
