using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using RustText.Demo;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
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

    [Fact]
    public Task LargeLogPageNumbersFitAndWheelCanMoveBackAcrossThePreviousPage() => HeadlessTests.RunAsync(async profile =>
    {
        const int totalLines = 376_146;
        var path = Path.Combine(profile, "numbered-pages.log");
        await using (var file = new StreamWriter(path))
        {
            for (var line = 0; line < totalLines; line++)
            {
                await file.WriteLineAsync("row");
            }
        }

        var log = new LogView(path, state: new LogTabState(EditPages: true, AutoRefresh: false));
        var window = new Window { Width = 900, Height = 700, Content = log };
        window.Show();
        try
        {
            await log.InitialPageLoad;
            await HeadlessTests.WaitForAsync(() => !Field<bool>(log, "_busy")
                && Field<long>(log, "_indexedTo") == new FileInfo(path).Length);
            var reader = Field<LogDataReader>(log, "_reader");
            await (Task)Invoke(log, "NavigateAsync", reader.GetEndOffset(), true)!;
            await HeadlessTests.WaitForAsync(() => !Field<bool>(log, "_busy"));
            window.UpdateLayout();
            await HeadlessTests.WaitForAsync(() => Field<TextEditor?>(log, "_pageEditor") is not null);
            var editor = Field<TextEditor>(log, "_pageEditor");
            Assert.True(editor.IsVisible, log.StatusText);
            var margin = Assert.Single(editor.TextArea.LeftMargins.OfType<GroupedLineNumberMargin>());

            await HeadlessTests.WaitForAsync(() => margin.IsMeasureValid && margin.Bounds.Width > 0
                && editor.TextArea.TextView.VisualLinesValid
                && editor.TextArea.TextView.VisualLines.Count > 0);
            window.UpdateLayout();
            Assert.Equal(376_067, margin.FirstLineNumber);

            var lastLabel = LineNumbers.Format(totalLines);
            using var label = new TextLayout(lastLabel, new Typeface(editor.FontFamily), editor.FontSize, editor.LineNumbersForeground);
            var expectedWidth = label.Width;
            Assert.True(margin.Bounds.Width >= expectedWidth,
                $"Line-number margin is {margin.Bounds.Width:F1} DIP wide, but {lastLabel} needs {expectedWidth:F1} DIP.");

            var scroll = Field<ScrollViewer>(log, "_logScrollViewer");
            await HeadlessTests.WaitForAsync(() => scroll.Extent.Height > scroll.Viewport.Height + 1);
            scroll.Offset = new Vector(scroll.Offset.X, 0);
            var point = editor.TranslatePoint(new Point(100, 80), window)!.Value;
            window.MouseWheel(point, new Vector(0, 1));
            await HeadlessTests.WaitForAsync(() => margin.FirstLineNumber == 375_987
                && !Field<bool>(log, "_busy"));
        }
        finally
        {
            log.Dispose();
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static object? Invoke(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
}
