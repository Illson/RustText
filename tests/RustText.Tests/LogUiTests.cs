using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RegularScrollContinuesAtTheNextChunk(bool dragThumb) => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "continuous.log");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Range(1, 240).Select(row => $"row-{row:D4}\n")));
        var (window, log) = await ShowLogAsync(path, new LogTabState(AutoRefresh: false));
        try
        {
            window.Width = 760;
            window.Height = 470;
            var text = (TextBox)Field(log, "_logText");
            await HeadlessTests.WaitForAsync(() => Field(log, "_logScrollViewer") is ScrollViewer scroll
                && scroll.Viewport.Height > 0 && scroll.Extent.Height > scroll.Viewport.Height);
            var pointer = text.TranslatePoint(new Point(100, 80), window)!.Value;
            if (dragThumb)
            {
                var bar = text.GetVisualDescendants().OfType<ScrollBar>().Single(item => item.Orientation == Orientation.Vertical);
                var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
                await HeadlessTests.WaitForAsync(() => thumb.Bounds.Width > 0 && thumb.Bounds.Height > 0);
                Point ThumbCenter() => thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                window.MouseMove(ThumbCenter());
                await HeadlessTests.WaitForAsync(() => window.InputHitTest(ThumbCenter()) is Visual hit
                    && (ReferenceEquals(hit, thumb) || thumb.IsVisualAncestorOf(hit)));
                var start = ThumbCenter();
                var bottom = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height + 50), window)!.Value;
                window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
                window.MouseMove(bottom, RawInputModifiers.LeftMouseButton);
                window.MouseUp(bottom, MouseButton.Left, RawInputModifiers.None);
            }
            else
            {
                for (var i = 0; i < 60 && Page(log)?.StartOffset == 0; i++)
                {
                    window.MouseWheel(pointer, new Vector(0, -1));
                    await Task.Delay(12);
                }
            }
            await HeadlessTests.WaitForAsync(() => Page(log)?.StartOffset == 80 * 9);
            Assert.StartsWith("row-0081", text.Text);
            Assert.Equal("Строки 81–160", ((TextBlock)Field(log, "_lineNumberLabel")).Text);
            var viewer = (ScrollViewer)Field(log, "_logScrollViewer");
            await HeadlessTests.WaitForAsync(() => viewer.Offset.Y <= 1);
            window.MouseWheel(pointer, new Vector(0, 1));
            await HeadlessTests.WaitForAsync(() => Page(log)?.StartOffset == 0
                && viewer.Offset.Y >= viewer.Extent.Height - viewer.Viewport.Height - 1);
            Assert.EndsWith("row-0080", text.Text);
        }
        finally { await CloseLogAsync(window, log); }
    });

    [Fact]
    public Task PageEditPreservesTheBomAndSurroundingPagesAndRejectsExternalChanges() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "edit.log");
        const string row = "old\r\n";
        var old = string.Concat(Enumerable.Repeat(row, 200));
        await File.WriteAllTextAsync(path, old, new UTF8Encoding(true));
        var (window, log) = await ShowLogAsync(path, new LogTabState(AutoRefresh: false));
        try
        {
            await InvokeTask(log, "NavigateAsync", 3 + 80L * row.Length, true);
            await InvokeTask(log, "TogglePageEditAsync");
            Assert.False(log.IsReadOnly);
            var text = (TextBox)Field(log, "_logText");
            Assert.Equal(string.Concat(Enumerable.Repeat(row, 80)), text.Text);
            const string replacement = "страница 🚀\r\n";
            text.Text = replacement;
            Assert.True(log.HasPendingEdit);
            await log.SavePageEditAsync();
            Assert.True(log.IsReadOnly);
            var expected = old[..(80 * row.Length)] + replacement + old[(160 * row.Length)..];
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(new UTF8Encoding(true).GetPreamble(), bytes[..3]);
            Assert.Equal(expected, Encoding.UTF8.GetString(bytes.AsSpan(3)));

            await InvokeTask(log, "TogglePageEditAsync");
            text.Text = "local change";
            await File.AppendAllTextAsync(path, "external\r\n");
            await log.SavePageEditAsync();
            Assert.True(log.HasPendingEdit);
            Assert.Equal("local change", text.Text);
            Assert.EndsWith("external\r\n", await File.ReadAllTextAsync(path));
            Assert.Contains("измени", log.StatusText);
        }
        finally { await CloseLogAsync(window, log); }
    });

    [Fact]
    public Task EditingADraftPublishesANewRecoveryAndCopyLivesInTabMenu() => HeadlessTests.RunAsync(async profile =>
    {
        const string row = "draft row\n";
        const int count = 55_000;
        var before = string.Concat(Enumerable.Repeat(row, count));
        var storage = StorageService.OpenForTesting(profile);
        var draft = new SessionTab(3, null, TextEncodingKind.Utf8, LineCount: count) { DraftOffset = 0 };
        var original = Assert.Single((await storage.SaveSessionAsync(new SessionState([draft]),
            new Dictionary<long, string> { [draft.Id] = before })).Tabs);
        var window = new MainWindow();
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
            var log = Assert.IsType<LogView>(ActiveLog(window));
            await HeadlessTests.WaitForAsync(() => Page(log) is not null && !(bool)Field(log, "_busy"));
            var tab = (TabItem)Active(window).GetType().GetProperty("Item")!.GetValue(Active(window))!;
            Assert.Same(log, tab.Content);
            Assert.Contains(tab.ContextMenu!.Items.OfType<MenuItem>(), item => Equals(item.Header, "Сохранить копию…"));

            Assert.False(log.IsReadOnly);
            Assert.False(((Button)Field(log, "_editPageButton")).IsVisible);
            var next = (Button)Field(log, "_nextButton");
            Assert.True(next.IsEnabled);
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => log.CurrentOffset == 80 * row.Length);
            Assert.False(log.IsReadOnly);
            ((Button)Field(log, "_startButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => log.CurrentOffset == 0);
            ((TextBox)Field(log, "_logText")).Text = "updated draft\n";
            await HeadlessTests.WaitForAsync(() => !next.IsEnabled);
            await log.SavePageEditAsync();
            var editedLog = Assert.IsType<LogView>(ActiveLog(window));
            Assert.NotSame(log, editedLog);
            await editedLog.InitialPageLoad;
            Assert.False(editedLog.IsReadOnly);
            var saved = Assert.Single((await storage.LoadSessionAsync())!.Tabs);
            Assert.NotEqual(original.DraftSha256, saved.DraftSha256);
            var edited = "updated draft\n" + before[(80 * row.Length)..];
            Assert.Equal(edited, await File.ReadAllTextAsync(storage.GetDraftPath(saved)!));
            Assert.Equal(before, await File.ReadAllTextAsync(storage.GetDraftPath(original)!));
        }
        finally
        {
            if ((bool)Field(window, "_restoreFinished") && ActiveLog(window) is LogView { IsReadOnly: false } editor)
                await InvokeTask(editor, "EndPageEditAsync");
            await CloseMainWindowAsync(window);
        }
    });

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
    public Task LogSearchSeedsSelectionAndFindsAutomaticallyWithCollapsibleResults() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "auto-search.log");
        const string query = " needle ";
        await File.WriteAllTextAsync(path, "row needle one\nother\nrow needle two\n");
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        try
        {
            await InvokeTask(window, "OpenPathAsync", path, true, null);
            var log = Assert.IsType<LogView>(ActiveLog(window));
            await log.InitialPageLoad;
            var text = (TextBox)Field(log, "_logText");
            text.SelectionStart = 3;
            text.SelectionEnd = 3 + query.Length;
            log.FocusSearch();
            Assert.Equal(query, ((TextBox)Field(log, "_searchBox")).Text);
            var results = (StackPanel)Field(log, "_searchResultButtons");
            await HeadlessTests.WaitForAsync(() => results.Children.Count == 2);

            var toggle = Assert.Single(log.GetVisualDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == "Скрыть список результатов");
            var scroll = Assert.Single(log.GetVisualDescendants().OfType<ScrollViewer>(),
                viewer => ReferenceEquals(viewer.Content, results));
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(scroll.IsVisible);
            Assert.Equal(2, results.Children.Count);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(scroll.IsVisible);
        }
        finally { await CloseMainWindowAsync(window); }
    });

    [Fact]
    public Task GigabyteLogWaitsForTheFindCommand() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "gigabyte.log");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("needle\n", 60)));
        using (var file = File.OpenWrite(path)) file.SetLength(1024L * 1024 * 1024);
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        try
        {
            await InvokeTask(window, "OpenPathAsync", path, true, null);
            var log = Assert.IsType<LogView>(ActiveLog(window));
            await log.InitialPageLoad;
            log.FocusSearch();
            ((TextBox)Field(log, "_searchBox")).Text = "needle";
            var results = (StackPanel)Field(log, "_searchResultButtons");
            await Task.Delay(650);
            Assert.Empty(results.Children);
            ((Button)Field(log, "_searchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => results.Children.Count == 32);
        }
        finally { await CloseMainWindowAsync(window); }
    });

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
            var minimap = (Control)Field(log, "_minimap");
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
            var firstMarker = Marker(minimap);
            Assert.True(firstMarker.Height >= 14);

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

            // These input events can all arrive before the first background alignment completes.
            window.MouseMove(At(0.33));
            window.MouseDown(At(0.33), MouseButton.Left, RawInputModifiers.None);
            foreach (var ratio in new[] { .62, .11, .75, .21, .98 })
                window.MouseMove(At(ratio), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(0.98), MouseButton.Left, RawInputModifiers.None);
            await HeadlessTests.WaitForAsync(() => FirstRowNumber(text.Text) > 1850);
            await HeadlessTests.WaitForAsync(() => !(bool)Field(log, "_busy"));
            var reader = Field(log, "_reader");
            var endOffset = (long)reader.GetType().GetMethod("GetEndOffset")!.Invoke(reader, [CancellationToken.None])!;
            await InvokeTask(log, "NavigateAsync", endOffset, true);
            await HeadlessTests.WaitForAsync(() => !(bool)Field(log, "_busy"));
            Assert.Equal(1921, FirstRowNumber(text.Text));
            Assert.Equal("Строки 1.921–2.000", ((TextBlock)Field(log, "_lineNumberLabel")).Text);
            Assert.True(Marker(minimap).Y > firstMarker.Y && Marker(minimap).Bottom <= minimap.Bounds.Height);
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
            var minimap = (Control)Field(log, "_minimap");
            await HeadlessTests.WaitForAsync(() => Marker(minimap).Height >= 14);
            Assert.InRange(Marker(minimap).Center.Y / minimap.Bounds.Height, .7, .8);

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
    public Task SearchNumbersMatchPhysicalLinesAcrossResultPages() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "line-numbers.log");
        var lines = Enumerable.Range(1, 34).Select(n => n == 2 ? new string('x', 17000) + "hit" : $"hit {n}");
        await File.WriteAllTextAsync(path, string.Join("\n", lines), Encoding.Unicode);
        var (window, log) = await ShowLogAsync(path, null);
        try
        {
            ((TextBox)Field(log, "_searchBox")).Text = "hit";
            await InvokeTask(log, "SearchAsync", false);
            await InvokeTask(log, "SearchAsync", true);
            var buttons = (StackPanel)Field(log, "_searchResultButtons");
            var labels = buttons.Children.Cast<Button>().Select(button => ((TextBlock)button.Content!).Text!.Split(' ')[0]);
            Assert.Equal(Enumerable.Range(1, 34).Select(n => n.ToString()), labels);
        }
        finally { await CloseLogAsync(window, log); }
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
    private static Rect Marker(Control minimap) => (Rect)minimap.GetType().GetProperty("ViewportMarker", Private)!.GetValue(minimap)!;
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
