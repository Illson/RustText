using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RustText.Demo;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class LogLevelTests
{
    [Theory]
    [InlineData("[ERR] диск заполнен", "Error")]
    [InlineData("2026-11-19 12:58:42.001 [worker] WARN request", "Warning")]
    [InlineData("[12:10:50 INF] found an error", "Info")]
    [InlineData("logger | DEBUG | retry", "Debug")]
    [InlineData("""{"msg":"[ERR] was recovered","severityText":"Information"}""", "Info")]
    [InlineData("time=t1 level=critical message=exit", "Fatal")]
    [InlineData("""{"msg":"error message"}""", "Other")]
    [InlineData("no error occurred", "Other")]
    [InlineData("<131>1 2026-11-19T10:01:22Z app host - - - disk full", "Error")]
    [InlineData("fail: Microsoft.Hosting[1]", "Error")]
    public void ClassifiesLevelFromRecordHeader(string row, string expected) =>
        Assert.Equal(expected, LogLevels.Classify(row).ToString());

    [Fact]
    public void FilterKeepsMultiFrameRowsAcrossPagesAndFileEncodings()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "mixed.log");
        var longError = "[ERR] " + new string('Ж', 150_000);
        File.WriteAllText(path, "[INF] skip\r\n" + longError + "\r\nwarn: warning\r\nno error here", Encoding.Unicode);
        using var file = LogDataReader.Open(path);
        var reader = new LogDataReader(file, TextEncodingKind.Utf16LittleEndian);
        var errors = new StringBuilder();
        var warnings = new List<string>();
        var pages = 0;
        FilterCursor? cursor = new FilterCursor();
        while (cursor is { } start)
        {
            var page = LogLevelFilter.Read((offset, token) => LogLevelFilter.FileRows(reader, offset, file.Length, token),
                start, LogLevel.Error | LogLevel.Warning, default);
            foreach (var row in page.Rows)
            {
                if (row.Level == LogLevel.Error) errors.Append(row.Text);
                else { Assert.Equal(LogLevel.Warning, row.Level); warnings.Add(row.Text); }
            }
            pages++;
            cursor = page.Next;
        }
        Assert.True(pages > 1);
        Assert.Equal(longError, errors.ToString());
        Assert.Equal(new[] { "warn: warning" }, warnings);
    }

    [Fact]
    public Task FilterButtonsPageThroughWholeLogAndLeaveSourceUntouched() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "filter.log");
        var lines = Enumerable.Range(1, 110).SelectMany(n => new[] { $"INFO info-{n}", $"[ERR] error-{n}" });
        var original = string.Join('\n', lines);
        await File.WriteAllTextAsync(path, original);
        using var log = new LogView(path);
        var owner = new Window { Content = log, Width = 850, Height = 600 };
        owner.Show();
        try
        {
            await log.InitialPageLoad;
            await HeadlessTests.WaitForAsync(() => log.GetVisualDescendants().OfType<TextPresenter>()
                .Any(presenter => presenter.Text?.StartsWith("INFO info-1", StringComparison.Ordinal) == true && presenter.Bounds.Height > 0));
            void CheckFirstTwoLineColors()
            {
                var presenter = log.GetVisualDescendants().OfType<TextPresenter>().Single(p =>
                    p.IsEffectivelyVisible && p.Text?.StartsWith("INFO info-1") == true);
                var origin = presenter.TranslatePoint(default, log)!.Value;
                var height = presenter.TextLayout.TextLines[0].Height;
                AssertSeverityBands(log, (int)origin.X + 220, (int)(origin.Y + height * .5), (int)(origin.Y + height * 1.5));
            }
            CheckFirstTwoLineColors();
            Click(log, "Уровни…");
            Assert.Empty(owner.OwnedWindows);
            var panel = Assert.Single(log.GetVisualDescendants().OfType<LogLevelFilterPanel>());
            Assert.Equal(LogLevel.All, panel.SelectedLevels);
            var fullText = log.GetVisualDescendants().OfType<TextBox>().Single(box => AutomationProperties.GetAutomationId(box) == "LogText");
            Assert.Contains("INFO info-1", fullText.Text);
            Assert.Contains("[ERR] error-1", fullText.Text);

            await HeadlessTests.WaitForAsync(() => panel.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Снять все")));
            Click(panel, "Снять все");
            var error = panel.GetVisualDescendants().OfType<ToggleButton>().Single(button => Equals(button.Content, "Error"));
            error.IsChecked = true;
            await HeadlessTests.WaitForAsync(() => FilteredLines(log).Length == 100);
            Assert.Equal(Enumerable.Range(1, 100).Select(n => $"[ERR] error-{n}"), FilteredLines(log));
            Click(log, "Дальше");
            await HeadlessTests.WaitForAsync(() => FilteredLines(log).Length == 10);
            Assert.Equal(Enumerable.Range(101, 10).Select(n => $"[ERR] error-{n}"), FilteredLines(log));
            Click(log, "Назад");
            await HeadlessTests.WaitForAsync(() => FilteredLines(log).Length == 100);
            Assert.Equal("[ERR] error-1", FilteredLines(log)[0]);

            error.IsChecked = false;
            Assert.Empty(FilteredLines(log));
            panel.GetVisualDescendants().OfType<ToggleButton>().Single(button => Equals(button.Content, "Info")).IsChecked = true;
            await HeadlessTests.WaitForAsync(() => FilteredLines(log).FirstOrDefault() == "INFO info-1");
            error.IsChecked = true;
            await HeadlessTests.WaitForAsync(() => FilteredLines(log).ElementAtOrDefault(1) == "[ERR] error-1");
            CheckFirstTwoLineColors();
            Click(panel, "Выбрать все");
            Assert.True(fullText.IsEffectivelyVisible);
            Assert.Contains("[ERR] error-1", fullText.Text);
            Assert.Empty(owner.OwnedWindows);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task EditableLogPaintsSeverityPastTheEndOfEachRow() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "editable-bands.log");
        await File.WriteAllTextAsync(path, "plain line\nINFO ok\n[ERR] disk full");
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)typeof(MainWindow)
                .GetField("_restoreFinished", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            await (Task)typeof(MainWindow).GetMethod("OpenPathsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new[] { path }, false, null])!;
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>()
                .Any(editor => editor.TextArea.TextView is { VisualLinesValid: true } view && view.VisualLines.Count >= 3));
            var view = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single().TextArea.TextView;
            int CenterY(int index) => (int)(view.VisualLines[index].VisualTop - view.VerticalOffset + view.VisualLines[index].Height / 2);
            AssertSeverityBands(view, 220, CenterY(1), CenterY(2));
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    private static void AssertSeverityBands(Visual visual, int x, int infoY, int errorY)
    {
        var size = new PixelSize((int)visual.Bounds.Width, (int)visual.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        using var pixels = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = pixels.Lock();
        bitmap.Render(visual);
        bitmap.CopyPixels(frame);
        var info = ReadPixel(infoY);
        var error = ReadPixel(errorY);
        Assert.True(error.R > info.R && error.G < info.G, $"Error band {error}, info band {info}");

        Color ReadPixel(int y)
        {
            var location = frame.Address + y * frame.RowBytes + x * 4;
            return Color.FromRgb(Marshal.ReadByte(location, 2), Marshal.ReadByte(location, 1), Marshal.ReadByte(location));
        }
    }

    private static void Click(Control scope, string caption) => scope.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.Content, caption)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string[] FilteredLines(LogView log)
    {
        var result = log.GetVisualDescendants().OfType<TextBox>().Single(box =>
            AutomationProperties.GetAutomationId(box) == "LogLevelFilterResults");
        return result.IsEffectivelyVisible
            ? (result.Text ?? "").Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries) : [];
    }
}
