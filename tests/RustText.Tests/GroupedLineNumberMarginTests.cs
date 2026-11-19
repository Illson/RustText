using System.Reflection;
using Avalonia.Headless;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using RustText.Demo;
using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class GroupedLineNumberMarginTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task EditorUsesGroupedMarginAndInheritsLineSelectionHandlers() =>
        HeadlessTests.RunAsync(async _ =>
        {
            var window = new MainWindow([]) { Width = 1000, Height = 720 };
            window.Show();
            await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
            try
            {
                Invoke(window, "NewTab", "first line\nsecond line\nthird line");
                var tab = typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(window)!;
                var editor = (TextEditor)tab.GetType().GetProperty("Editor")!.GetValue(tab)!;
                await HeadlessTests.WaitForAsync(() => editor.TextArea.LeftMargins.OfType<GroupedLineNumberMargin>().Any()
                    && editor.TextArea.TextView.VisualLinesValid
                    && editor.TextArea.TextView.VisualLines.Count >= 3);

                var margin = Assert.Single(editor.TextArea.LeftMargins.OfType<GroupedLineNumberMargin>());
                Assert.IsAssignableFrom<LineNumberMargin>(margin);
                Assert.Same(editor.TextArea.TextView, margin.TextView);
                foreach (var name in new[] { "OnPointerPressed", "OnPointerMoved", "OnPointerReleased" })
                {
                    var handler = typeof(GroupedLineNumberMargin).GetMethod(name, Private)!;
                    Assert.Equal(typeof(LineNumberMargin), handler.DeclaringType);
                }

                Assert.Equal("1.234.567", LineNumbers.Format(1_234_567));
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
