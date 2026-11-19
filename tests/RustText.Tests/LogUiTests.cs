using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using RustText.Demo;
using RustText.Demo.Logs;
using Xunit;
using Xunit.Abstractions;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class LogUiTests
{
    private readonly ITestOutputHelper _output;

    public LogUiTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task LongLineFragmentsKeepOneGutterNumberAndPreserveFollowingLines() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "long-line.log");
        var longLine = new string('x', 20_000);
        await File.WriteAllTextAsync(path, longLine + "\r\nsecond\r\nthird\r\n");
        var window = new MainWindow();
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            await InvokeTask(window, "OpenPathAsync", path, true, null);
            var log = ActiveLog(window);
            await HeadlessTests.WaitForAsync(() => Page(log) is not null && !(bool)Field(log, "_busy"));
            var gutter = (TextBlock)Field(log, "_lineGutterText");
            Assert.Equal(string.Join(Environment.NewLine, "1", "2", "3"), gutter.Text);
            Assert.Equal("Строки 1–3", ((TextBlock)Field(log, "_lineNumberLabel")).Text);
            Assert.Equal(string.Join(Environment.NewLine, longLine, "second", "third"), ((TextBox)Field(log, "_logText")).Text);
        }
        finally
        {
            await CloseMainWindowAsync(window);
        }
    });

    [Fact]
    public async Task CompactLogKeepsThreeRowsVisibleWithSearchResultsAt760By360()
    {
        await HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "compact.log");
            var rows = Enumerable.Range(1, 96).Select(row => row % 12 == 0
                ? $"2026-11-19 12:{row / 60:D2}:{row % 60:D2}.000 ERROR needle id={row:D3}"
                : $"2026-11-19 12:{row / 60:D2}:{row % 60:D2}.000 INFO request completed id={row:D3}");
            await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, rows) + Environment.NewLine);

            var window = new MainWindow { Width = 760, Height = 360 };
            window.Show();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            await InvokeTask(window, "OpenPathAsync", path, true, null);
            var log = ActiveLog(window);
            var text = (TextBox)Field(log, "_logText");
            await HeadlessTests.WaitForAsync(() => Page(log) is not null && !(bool)Field(log, "_busy"));

            try
            {
                log.GetType().GetMethod("FocusSearch")!.Invoke(log, null);
                ((TextBox)Field(log, "_searchBox")).Text = "needle";
                await InvokeTask(log, "SearchAsync", false);
                await HeadlessTests.WaitForAsync(() => !(bool)Field(log, "_busy")
                    && ((System.Collections.ICollection)Field(log, "_searchResults")).Count == 8);
                await HeadlessTests.WaitForAsync(() => text.Bounds.Height > 0);

                _output.WriteLine($"Compact log viewport at 760×360 with search: {text.Bounds.Height:0.#}px.");
                Assert.True(text.Bounds.Height >= 58,
                    $"Expected at least about three 12px text rows, but the log viewport is {text.Bounds.Height:0.#}px high.");
            }
            finally
            {
                await CloseMainWindowAsync(window);
            }
        });
    }
    [Fact]
    public async Task MinimapWheelTrackAndDragNavigateLiveAcrossLog()
    {
        await HeadlessTests.RunAsync(async _ =>
        {
            using var directory = new TempDirectory();
            var path = Path.Combine(directory.Path, "long.log");
            await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 2000).Select(row => $"row-{row:D4}")) + "\n");
            var window = new MainWindow { Width = 1400, Height = 900 };
            window.Show();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            await InvokeTask(window, "OpenPathAsync", path, true, null);
            var log = ActiveLog(window);
            var text = (TextBox)Field(log, "_logText");
            var minimap = (ListBox)Field(log, "_minimapList");
            await HeadlessTests.WaitForAsync(() => Page(log) is not null && minimap.Bounds.Height > 100
                && ((System.Collections.ICollection)Field(log, "_miniSamples")).Count == 64);
            await HeadlessTests.WaitForAsync(() => !(bool)Field(log, "_busy"));
            Assert.Contains("Строки 1–80", ((TextBlock)Field(log, "_lineNumberLabel")).Text);
            var gutter = (TextBlock)Field(log, "_lineGutterText");
            Assert.StartsWith("1", gutter.Text);
            Assert.Contains(Environment.NewLine + "80", gutter.Text);

            var origin = minimap.TranslatePoint(new Point(0, 0), window) ?? throw new InvalidOperationException("Minimap is not attached.");
            Point At(double ratio) => origin + new Point(minimap.Bounds.Width / 2, minimap.Bounds.Height * ratio);
            Assert.Equal("Прокрутка лога", Avalonia.Automation.AutomationProperties.GetName(minimap));

            var beforeWheel = FirstRowNumber(text.Text);
            window.MouseWheel(At(0.5), new Vector(0, -1));
            await HeadlessTests.WaitForAsync(() => FirstRowNumber(text.Text) > beforeWheel);
            var afterWheel = FirstRowNumber(text.Text);

            window.MouseMove(At(0.2));
            window.MouseDown(At(0.2), MouseButton.Left, RawInputModifiers.None);
            await HeadlessTests.WaitForAsync(() => FirstRowNumber(text.Text) is > 200 and < 700);
            var afterTrackPress = FirstRowNumber(text.Text);
            window.MouseMove(At(0.8), RawInputModifiers.LeftMouseButton);
            await HeadlessTests.WaitForAsync(() => FirstRowNumber(text.Text) > afterTrackPress + 500);
            var whileDragging = FirstRowNumber(text.Text);
            window.MouseUp(At(0.8), MouseButton.Left, RawInputModifiers.None);
            Assert.True(whileDragging > afterTrackPress + 500);

            window.MouseMove(At(0.98));
            window.MouseDown(At(0.98), MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(At(0.98), MouseButton.Left, RawInputModifiers.None);
            await HeadlessTests.WaitForAsync(() => FirstRowNumber(text.Text) > 1850);
            await HeadlessTests.WaitForAsync(() => !(bool)Field(log, "_busy"));
            var reader = Field(log, "_reader");
            var endOffset = (long)reader.GetType().GetMethod("GetEndOffset")!.Invoke(reader, [CancellationToken.None])!;
            await InvokeTask(log, "NavigateAsync", endOffset, true);
            await HeadlessTests.WaitForAsync(() => !(bool)Field(log, "_busy"));
            Assert.Equal(1921, FirstRowNumber(text.Text));
            Assert.Equal("Строки 1.921–2.000", ((TextBlock)Field(log, "_lineNumberLabel")).Text);
            Assert.StartsWith("1.921", gutter.Text);
            window.MouseMove(At(0.02));
            window.MouseDown(At(0.02), MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(At(0.02), MouseButton.Left, RawInputModifiers.None);
            await HeadlessTests.WaitForAsync(() => FirstRowNumber(text.Text) < 100);

            await CloseMainWindowAsync(window);
        });
    }

    [Fact]
    public Task RestoredDraftShowsGroupedSourceLinesAndScrollsBackToTheStart() => HeadlessTests.RunAsync(async profile =>
    {
        const string row = "line-data\n";
        const int count = 81_000;
        const int startLine = 60_001;
        var original = new SessionTab(5, null, TextEncodingKind.Utf8, LineCount: count)
        {
            UntitledName = "Большой черновик",
            DraftOffset = (startLine - 1L) * row.Length
        };
        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSessionAsync(new SessionState([original]),
            new Dictionary<long, string> { [original.Id] = string.Concat(Enumerable.Repeat(row, count)) });
        var window = new MainWindow { Width = 850, Height = 480 };
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            var log = ActiveLog(window);
            var lineLabel = (TextBlock)Field(log, "_lineNumberLabel");
            var countLabel = (TextBlock)Field(log, "_positionLabel");
            await HeadlessTests.WaitForAsync(() => lineLabel.Text == "Строки 60.001–60.080");
            Assert.Equal("Всего строк · 81.000", countLabel.Text);
            Assert.Equal("Большой черновик", ((TextBlock)Field(log, "_fileLabel")).Text);
            Assert.StartsWith("60.001", ((TextBlock)Field(log, "_lineGutterText")).Text);

            var text = (TextBox)Field(log, "_logText");
            text.Focus();
            window.KeyPress(Key.PageUp, RawInputModifiers.None, PhysicalKey.PageUp, "");
            window.KeyRelease(Key.PageUp, RawInputModifiers.None, PhysicalKey.PageUp, "");
            await HeadlessTests.WaitForAsync(() => Page(log)?.StartOffset == (startLine - 81L) * row.Length);

            window.KeyPress(Key.Home, RawInputModifiers.Control, PhysicalKey.Home, "");
            window.KeyRelease(Key.Home, RawInputModifiers.Control, PhysicalKey.Home, "");
            await HeadlessTests.WaitForAsync(() => Page(log)?.StartOffset == 0 && lineLabel.Text == "Строки 1–80");
            await HeadlessTests.WaitForAsync(() => text.GetVisualDescendants().OfType<ScrollViewer>().Any());
            var scroll = text.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            var pointer = text.TranslatePoint(new Point(145, 60), window)!.Value;
            window.MouseWheel(pointer, new Vector(0, -1));
            await HeadlessTests.WaitForAsync(() => Page(log)?.StartOffset == 80L * row.Length);
        }
        finally
        {
            await CloseMainWindowAsync(window);
        }
    });

    [Fact]
    public async Task SearchNavigatesAndHighlightsLongLineHitsWithF3InBothDirections()
    {
        await HeadlessTests.RunAsync(async _ =>
        {
            using var directory = new TempDirectory();
            var path = Path.Combine(directory.Path, "long-hit.log");
            var lines = new[]
            {
                new string('x', 160 * 1024 - 3) + "Needle-one",
                "short Needle-two",
                string.Join('\n', Enumerable.Range(1, 120).Select(row => $"row-{row:D3} Needle-{row:D3}"))
            };
            await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n");
            var window = new MainWindow { Width = 1200, Height = 800 };
            window.Show();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            await InvokeTask(window, "OpenPathAsync", path, true, null);
            var log = ActiveLog(window);
            var text = (TextBox)Field(log, "_logText");
            var search = (TextBox)Field(log, "_searchBox");
            search.Text = "needle";
            await HeadlessTests.WaitForAsync(() => !(bool)Field(log, "_busy"));
            await InvokeTask(log, "SearchAsync", false);
            await HeadlessTests.WaitForAsync(() => ((System.Collections.ICollection)Field(log, "_searchResults")).Count > 0);
            await InvokeTask(log, "NavigateMatchAsync", false);
            await AssertSelectedNeedleAfterAsync(text, 128 * 1024);

            text.Focus();
            var firstHitStart = SelectionStart(text);
            window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, "F3");
            await HeadlessTests.WaitForAsync(() => SelectionStart(text) != firstHitStart && SelectedText(text) == "Needle");
            var secondHitOffset = SelectionStart(text);
            Assert.InRange(secondHitOffset, 0, 16 * 1024);

            window.KeyPress(Key.F3, RawInputModifiers.Shift, PhysicalKey.F3, "F3");
            await HeadlessTests.WaitForAsync(() => SelectionStart(text) != secondHitOffset && SelectionStart(text) > 128 * 1024);
            Assert.Equal("Needle", SelectedText(text));
            Assert.True(SelectionStart(text) > 128 * 1024);

            for (var hit = 0; hit < 31; hit++)
            {
                await InvokeTask(log, "NavigateMatchAsync", false);
            }
            Assert.Equal(32, ((System.Collections.ICollection)Field(log, "_searchResults")).Count);
            var thirtySecondHit = Page(log)!.StartOffset;
            window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, "F3");
            await HeadlessTests.WaitForAsync(() => Page(log)!.StartOffset > thirtySecondHit
                && ((System.Collections.ICollection)Field(log, "_searchResults")).Count > 32);
            Assert.Equal("Needle", SelectedText(text));
            window.KeyPress(Key.F3, RawInputModifiers.Shift, PhysicalKey.F3, "F3");
            await HeadlessTests.WaitForAsync(() => Page(log)!.StartOffset == thirtySecondHit);
            Assert.Equal("Needle", SelectedText(text));
            await CloseMainWindowAsync(window);
        });
    }

    [Fact]
    public async Task SearchFindsCompletedAppendedUtf8AndNullBytesRemainReadable()
    {
        await HeadlessTests.RunAsync(async _ =>
        {
            using var directory = new TempDirectory();
            var path = Path.Combine(directory.Path, "utf8-growth.log");
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("start\n"));
            var window = new MainWindow { Width = 1100, Height = 760 };
            window.Show();
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            await InvokeTask(window, "OpenPathAsync", path, true, null);
            var log = ActiveLog(window);
            var search = (TextBox)Field(log, "_searchBox");
            search.Text = "😀";
            await HeadlessTests.WaitForAsync(() => !(bool)Field(log, "_busy"));
            await WriteAppend(path, [0xF0, 0x9F]);
            await InvokeTask(log, "SearchAsync", false);
            Assert.Empty((System.Collections.ICollection)Field(log, "_searchResults"));

            await WriteAppend(path, [0x98, 0x80, (byte)'\n', (byte)'n', (byte)'u', (byte)'l', (byte)'l', 0, (byte)'a', (byte)'f', (byte)'t', (byte)'e', (byte)'r', (byte)'\n']);
            await InvokeTask(log, "SearchAsync", false);
            await HeadlessTests.WaitForAsync(() => ((System.Collections.ICollection)Field(log, "_searchResults")).Count == 1);
            await InvokeTask(log, "NavigateMatchAsync", false);
            Assert.Equal("😀", SelectedText((TextBox)Field(log, "_logText")));
            var text = (TextBox)Field(log, "_logText");
            var page = Page(log)!;
            Assert.Contains(page.Rows, row => row.Text.Contains('\0') && row.Text.Contains("after", StringComparison.Ordinal));
            Assert.Contains('\0', text.Text!);
            await CloseMainWindowAsync(window);
        });
    }

    [Fact]
    public async Task LogBookmarkRestoresAppendedFileAndResetsAfterRewrite()
    {
        await HeadlessTests.RunAsync(async _ =>
        {
            using var directory = new TempDirectory();
            var path = Path.Combine(directory.Path, "bookmark.log");
            var initial = string.Join('\n', Enumerable.Range(1, 500).Select(row => $"row-{row:D4}")) + "\n";
            await File.WriteAllTextAsync(path, initial, new UTF8Encoding(false));
            var first = await ShowLogAsync(path, null);
            var offset = Encoding.UTF8.GetByteCount(string.Join('\n', Enumerable.Range(1, 250).Select(row => $"row-{row:D4}")) + "\n");
            await InvokeTask(first.Log, "NavigateAsync", (long)offset, false);
            var bookmark = first.Log.CaptureState();
            Assert.NotNull(bookmark.Identity);
            Assert.NotNull(bookmark.Fingerprint);
            Assert.True(bookmark.Offset > 0);
            await CloseLogAsync(first.Window, first.Log);

            await WriteAppend(path, Encoding.UTF8.GetBytes("appended\n"));
            var appended = await ShowLogAsync(path, bookmark);
            Assert.Equal(bookmark.Offset, Page(appended.Log)!.StartOffset);
            Assert.Equal(bookmark.HiddenOffset, FieldLong(appended.Log, "_hiddenOffset"));
            var appendedBookmark = appended.Log.CaptureState();
            await CloseLogAsync(appended.Window, appended.Log);

            var rewritten = "changed" + initial[7..];
            Assert.Equal(initial.Length, rewritten.Length);
            await File.WriteAllTextAsync(path, rewritten, new UTF8Encoding(false));
            var afterRewrite = await ShowLogAsync(path, appendedBookmark);
            Assert.Equal(0, Page(afterRewrite.Log)!.StartOffset);
            Assert.Equal(0, FieldLong(afterRewrite.Log, "_hiddenOffset"));
            Assert.StartsWith("changed", ((TextBox)Field(afterRewrite.Log, "_logText")).Text);
            await CloseLogAsync(afterRewrite.Window, afterRewrite.Log);
        });
    }

    private static async Task<(Window Window, LogView Log)> ShowLogAsync(string path, LogTabState? state)
    {
        var log = new LogView(path, state: state);
        var window = new Window { Width = 900, Height = 700, Content = log };
        window.Show();
        await HeadlessTests.WaitForAsync(() => Page(log) is not null);
        return (window, log);
    }

    private static async Task CloseLogAsync(Window window, LogView log)
    {
        log.Dispose();
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
    }

    private static async Task CloseMainWindowAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_closed"));
    }

    private static LogPage? Page(object log) => (LogPage?)Field(log, "_page");
    private static object ActiveLog(MainWindow window)
    {
        var active = Active(window);
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return active.GetType().GetProperty("Log", flags)!.GetValue(active)!;
    }
    private static object Active(MainWindow window) => typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(window)!;
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static long FieldLong(object owner, string name) => (long)Field(owner, name);
    private static Task InvokeTask(object owner, string name, params object?[] arguments) =>
        (Task)(owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(owner, arguments) ?? throw new InvalidOperationException($"{name} returned null."));

    private static int FirstRowNumber(string? text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text ?? "", "row-(\\d{4})");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static int SelectionStart(TextBox text) => (int)text.GetType().GetProperty("SelectionStart")!.GetValue(text)!;
    private static int SelectionEnd(TextBox text) => (int)text.GetType().GetProperty("SelectionEnd")!.GetValue(text)!;
    private static string SelectedText(TextBox text) => text.Text!.Substring(SelectionStart(text), SelectionEnd(text) - SelectionStart(text));

    private static async Task AssertSelectedNeedleAfterAsync(TextBox text, int threshold)
    {
        await HeadlessTests.WaitForAsync(() => SelectedText(text) == "Needle" && SelectionStart(text) > threshold);
        Assert.True(SelectionStart(text) > threshold);
        Assert.Equal("Needle", SelectedText(text));
        Assert.Equal(SelectionEnd(text), text.CaretIndex);
    }

    private static async Task WriteAppend(string path, byte[] bytes)
    {
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await file.WriteAsync(bytes);
        await file.FlushAsync();
    }
}
