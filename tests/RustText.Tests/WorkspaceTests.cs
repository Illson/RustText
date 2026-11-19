using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Editing;
using RustText.Demo;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class WorkspaceTests
{
    [Fact]
    public Task UpdateNoticeKeepsEditorActiveAndOpensReleaseNotesOnDemand() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "editor.txt");
        await File.WriteAllTextAsync(path, "text");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            editor.TextArea.Focus();
            editor.CaretOffset = editor.Text.Length;
            var banner = window.FindControl<Border>("UpdateNotice")!;
            Assert.False(banner.IsVisible);
            var release = new UpdateRelease("2.1.0", "v2.1.0",
                new Uri("https://github.com/Illson/RustText/releases/tag/v2.1.0"),
                new Uri("https://github.com/Illson/RustText/releases/download/v2.1.0/RustText.NET-win-x64.zip"),
                new Uri("https://github.com/Illson/RustText/releases/download/v2.1.0/RustText.NET-win-x64.zip.sha256"),
                123, null, "## Исправлено\n\n- Текст из GitHub.");
            Invoke(window, "PresentUpdate", release);
            Assert.True(banner.IsVisible);
            Assert.Equal("Доступна версия 2.1.0", window.FindControl<TextBlock>("UpdateNoticeText")!.Text);
            Assert.Empty(window.OwnedWindows);
            window.KeyTextInput("!");
            Assert.Equal("text!", editor.Text);

            window.FindControl<Button>("UpdateNoticeDismiss")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(banner.IsVisible);
            Invoke(window, "PresentUpdate", release);
            window.FindControl<Button>("UpdateNoticeDetails")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = Assert.IsType<Window>(Assert.Single(window.OwnedWindows));
            await HeadlessTests.WaitForAsync(() => dialog.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.Text?.Contains("Текст из GitHub.", StringComparison.Ordinal) == true ||
                    block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(run =>
                        run.Text?.Contains("Текст из GitHub.", StringComparison.Ordinal) == true) == true));
            dialog.Close(false);
            await HeadlessTests.WaitForAsync(() => !banner.IsVisible);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task UpdateHistoryOpensWithoutAvailableUpdateAndRetriesGitHubFailure() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            var requests = 0;
            Func<CancellationToken, Task<IReadOnlyList<ReleaseNotes>>> fetch = _ =>
            {
                if (++requests == 1)
                    throw new HttpRequestException("GitHub недоступен");
                return Task.FromResult<IReadOnlyList<ReleaseNotes>>([
                    new("0.2.2", new Uri("https://github.com/Illson/RustText/releases/tag/v0.2.2"), "## Исправлены черновики"),
                    new("0.2.1", new Uri("https://github.com/Illson/RustText/releases/tag/v0.2.1"), null)
                ]);
            };
            var pending = (Task<bool>)Invoke(window, "ShowUpdateDialogAsync", null, fetch)!;
            var dialog = Assert.IsType<Window>(Assert.Single(window.OwnedWindows));
            await HeadlessTests.WaitForAsync(() => dialog.GetVisualDescendants().OfType<Button>()
                .Any(button => Equals(button.Content, "Повторить")));
            var retry = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Повторить"));
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => dialog.GetVisualDescendants().OfType<Expander>().Count() == 2);
            var versions = dialog.GetVisualDescendants().OfType<Expander>().ToArray();
            Assert.StartsWith("0.2.2", Assert.IsType<TextBlock>(versions[0].Header).Text);
            Assert.StartsWith("0.2.1", Assert.IsType<TextBlock>(versions[1].Header).Text);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Скачать и перезапустить"));
            versions[1].IsExpanded = true;
            await HeadlessTests.WaitForAsync(() => versions[1].GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text == "Описание изменений пока не опубликовано."));
            dialog.Close(false);
            Assert.False(await pending);
            Assert.Equal(2, requests);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task RecentLogKeepsItsTargetWhenSelectionChanges() => HeadlessTests.RunAsync(async profile =>
    {
        var first = Path.Combine(profile, "first.log");
        var second = Path.Combine(profile, "second.log");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", second, true, null);
            var selected = ActiveTab(window);
            var storage = Field(window, "_storage");
            var gate = (SemaphoreSlim)typeof(StorageService).GetField("_recentGate", Private)!.GetValue(storage)!;
            await gate.WaitAsync();
            Task opening;
            try
            {
                opening = CallAsync(window, "OpenRecentAsync", new RecentFileEntry(first, TextEncodingKind.Utf8, true));
                Invoke(window, "Select", selected);
            }
            finally { gate.Release(); }
            await opening;

            var log = Read<LogView>(ActiveTab(window), "Log");
            await log.InitialPageLoad;
            await EditLogPageAsync(log, "edited second");
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal("first", await File.ReadAllTextAsync(first));
            Assert.Equal("edited second", await File.ReadAllTextAsync(second));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task AutoDetectedLogEncodingSurvivesHistoryAndRestart() => HeadlessTests.RunAsync(async profile =>
    {
        const string text = "Первая строка";
        var path = Path.Combine(profile, "utf16.log");
        await File.WriteAllTextAsync(path, text, System.Text.Encoding.Unicode);
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", path, true, null);
            var recent = Assert.Single(await StorageService.OpenForTesting(profile).GetRecentFilesAsync());
            await CallAsync(window, "CloseTabAsync", ActiveTab(window));
            await CallAsync(window, "RestoreLastClosedAsync");
            await AssertPageTextAsync();
            await CallAsync(window, "CloseTabAsync", ActiveTab(window));
            await CallAsync(window, "OpenRecentAsync", recent);
            await AssertPageTextAsync();
            await CloseAsync(window);
            window = await OpenWorkspaceAsync();
            await AssertPageTextAsync();
        }
        finally { if (window.IsVisible) await CloseAsync(window); }

        async Task AssertPageTextAsync()
        {
            var log = Read<LogView>(ActiveTab(window), "Log");
            await log.InitialPageLoad;
            Assert.Equal(text, LogText(log).Text);
        }
    });

    [Fact]
    public Task RestoredFileAboveEditorLimitStaysEditableByPage() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "growing.txt");
        await File.WriteAllTextAsync(path, "first row\n");
        var window = await OpenWorkspaceAsync(path);
        await CloseAsync(window);
        using (var file = File.OpenWrite(path)) file.SetLength(DocumentFiles.MaximumDocumentBytes + 1);
        window = await OpenWorkspaceAsync();
        try
        {
            var log = Assert.IsType<LogView>(Property(ActiveTab(window), "Log"));
            await log.InitialPageLoad;
            Assert.StartsWith("first row", LogText(log).Text);
            Assert.False(log.IsReadOnly);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task EditorSearchSeedsLiteralSelectionAndRefreshesAsTheQueryChanges() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "search.txt");
        await File.WriteAllTextAsync(path, "a+b one\nother\nA+B two\nA+Bthree");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var input = (TextBox)Field(window, "_searchInput");
            var regex = (ToggleButton)Field(window, "_regexOption");
            var results = (ListBox)Field(window, "_searchResults");
            regex.IsChecked = true;
            editor.TextArea.Focus();
            editor.Select(0, 4);
            editor.CaretOffset = 4;
            Press(window, Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");

            Assert.Equal("a+b ", input.Text);
            Assert.False(regex.IsChecked);
            await HeadlessTests.WaitForAsync(() => results.Items.Count == 2);
            Assert.Equal(4, editor.SelectionLength);

            ((ToggleButton)Field(window, "_caseOption")).IsChecked = true;
            await HeadlessTests.WaitForAsync(() => results.Items.Count == 1);
            Assert.Contains("a+b one", ((ListBoxItem)results.Items[0]!).Content!.ToString());
            input.Text = "other";
            input.Text = "A+Bthree";
            await HeadlessTests.WaitForAsync(() => results.Items.Count == 1
                && ((ListBoxItem)results.Items[0]!).Content!.ToString()!.Contains("A+Bthree", StringComparison.Ordinal));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task StatusBarShowsTheCaretLineAndSelectionLengthWithoutMovingTheCaret() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "position.txt");
        const int line = 12_345;
        const int selected = 1_234;
        await File.WriteAllTextAsync(path, new string('\n', line - 1) + new string('я', selected));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var status = window.FindControl<TextBlock>("FormatText")!;
            editor.CaretOffset = editor.Document.TextLength;
            Assert.StartsWith("Строка 12.345 · Выделено 0 симв.", status.Text);
            editor.Select(editor.CaretOffset - selected, selected);
            Assert.StartsWith("Строка 12.345 · Выделено 1.234 симв.", status.Text);
            editor.TextArea.ClearSelection();
            Assert.StartsWith("Строка 12.345 · Выделено 0 симв.", status.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StatusBarTracksTheCurrentFileLineInPagedTabs(bool editable) => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "paged-position.log");
        const string row = "row with text\r\n";
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat(row, 170)));
        var window = await OpenWorkspaceAsync();
        TextBox? pageText = null;
        string? original = null;
        try
        {
            if (editable) await CallAsync(window, "OpenSinglePathAsync", path, false, null, true);
            else await CallAsync(window, "OpenPathAsync", path, true, null);
            var log = Read<LogView>(ActiveTab(window), "Log");
            await log.InitialPageLoad;
            await (Task)typeof(LogView).GetMethod("NavigateAsync", Private)!.Invoke(log, [80L * row.Length, true])!;
            pageText = LogText(log);
            original = pageText.Text;
            var status = window.FindControl<TextBlock>("FormatText")!;
            if (editable)
            {
                const string previousLines = "первая\rвторая\nтретья\r\n";
                pageText.Text = previousLines + "четвёртая";
                pageText.CaretIndex = previousLines.Length + 4;
                pageText.SelectionStart = previousLines.Length;
                pageText.SelectionEnd = previousLines.Length + 4;
                Assert.StartsWith("Строка 84 · Выделено 4 симв.", status.Text);
            }
            else
            {
                pageText.CaretIndex = row.Length + 3;
                pageText.SelectionStart = row.Length;
                pageText.SelectionEnd = row.Length + 3;
                Assert.StartsWith("Строка 82 · Выделено 3 симв.", status.Text);
            }

            pageText.SelectionStart = pageText.SelectionEnd;
            Assert.Contains("Выделено 0 симв.", status.Text);
        }
        finally
        {
            if (editable && pageText is not null) pageText.Text = original;
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task FindAllSelectsBeyondThePreviewListAndTheListCanBeCollapsed() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "find-all.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 140).Select(i => $"v{i:D3} keep")));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            window.Width = 650;
            window.Height = 360;
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            Invoke(window, "OpenSearchPanel", true);
            var input = (TextBox)Field(window, "_searchInput");
            var replacement = (TextBox)Field(window, "_replacement");
            var results = (ListBox)Field(window, "_searchResults");
            var findAll = (Button)Field(window, "_findAll");
            ((ToggleButton)Field(window, "_regexOption")).IsChecked = true;
            input.Text = @"v\d+";
            await HeadlessTests.WaitForAsync(() => results.Items.Count == 100
                && input.Bounds.Width > 100 && replacement.IsVisible && replacement.Bounds.Width > 100);
            Assert.Equal(input.Bounds.Width, replacement.Bounds.Width);
            var toggle = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == "Скрыть список результатов");
            Click(toggle);
            Assert.False(results.IsVisible);
            Assert.Equal(100, results.Items.Count);
            Click(toggle);
            Assert.True(results.IsVisible);

            Click(findAll);
            await HeadlessTests.WaitForAsync(() => MultiCaretEditing.Count(editor.TextArea) == 140);
            Assert.True(editor.TextArea.IsKeyboardFocusWithin);
            var status = window.FindControl<TextBlock>("FormatText")!;
            Assert.Contains("Выделено 560 симв.", status.Text);
            window.KeyTextInput("X");
            Assert.Equal(string.Join('\n', Enumerable.Repeat("X keep", 140)), editor.Text);
            Assert.Contains("Выделено 0 симв.", status.Text);
            editor.Undo();
            Assert.StartsWith("v001 keep", editor.Text);
            Assert.EndsWith("v140 keep", editor.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task LargeFileChoiceIsAppliedAndRememberedAcrossRestart(bool openAsLog) =>
        HeadlessTests.RunAsync(async profile =>
    {
        var first = Path.Combine(profile, "first-large.txt");
        var second = Path.Combine(profile, "second-large.txt");
        await CreateFileWithLengthAsync(first, 128L * 1024 * 1024);
        await CreateFileWithLengthAsync(second, 128L * 1024 * 1024);
        var window = await OpenWorkspaceAsync();
        try
        {
            var open = CallAsync(window, "OpenPathAsync", first, false, null);
            var dialog = await WaitForLargeFileDialogAsync(window);
            Assert.Empty(Tabs(window));
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Click(DialogButton(dialog, openAsLog ? "Открыть как лог" : "Открыть в редакторе"));
            await open;
            var log = Read<LogView>(ActiveTab(window), "Log");
            await log.InitialPageLoad;
            Assert.Equal(openAsLog, log.IsReadOnly);
            Assert.Equal(openAsLog, (await StorageService.OpenForTesting(profile).LoadSettingsAsync()).OpenLargeFilesAsLog);
        }
        finally { await CloseAsync(window); }

        window = await OpenWorkspaceAsync();
        try
        {
            var restored = Read<LogView>(ActiveTab(window), "Log");
            await restored.InitialPageLoad;
            Assert.Equal(openAsLog, restored.IsReadOnly);
            await CallAsync(window, "OpenPathAsync", second, false, null).WaitAsync(TimeSpan.FromSeconds(5));
            var remembered = Read<LogView>(ActiveTab(window), "Log");
            await remembered.InitialPageLoad;
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(second, remembered.Path);
            Assert.Equal(openAsLog, remembered.IsReadOnly);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task LargeFilePromptIsStrictlyAboveThresholdAndCancelDoesNotRemember() => HeadlessTests.RunAsync(async profile =>
    {
        const long threshold = 100L * 1024 * 1024;
        var exact = Path.Combine(profile, "exact.txt");
        var above = Path.Combine(profile, "above.txt");
        await CreateFileWithLengthAsync(exact, threshold);
        await CreateFileWithLengthAsync(above, threshold + 1);

        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", exact, false, null).WaitAsync(TimeSpan.FromSeconds(5));
            var editor = Read<LogView>(ActiveTab(window), "Log");
            await editor.InitialPageLoad;
            Assert.False(editor.IsReadOnly);
            Assert.Empty(window.OwnedWindows);

            var open = CallAsync(window, "OpenPathAsync", above, false, null);
            var dialog = await WaitForLargeFileDialogAsync(window);
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Click(DialogButton(dialog, "Отмена"));
            await open;
            Assert.Single(Tabs(window));
            Assert.Null((await StorageService.OpenForTesting(profile).LoadSettingsAsync()).OpenLargeFilesAsLog);

            await CallAsync(window, "OpenPathAsync", above, true, null).WaitAsync(TimeSpan.FromSeconds(5));
            var log = Read<LogView>(ActiveTab(window), "Log");
            await log.InitialPageLoad;
            Assert.True(log.IsReadOnly);
            Assert.Empty(window.OwnedWindows);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task SavingNamedPagedEditorKeepsItEditableAndPreservesTheRestOfTheFile() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "paged-editor.txt");
        var original = string.Concat(Enumerable.Repeat("строка\r\n", 200));
        await File.WriteAllTextAsync(path, original);
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenSinglePathAsync", path, false, null, true);
            var tab = ActiveTab(window);
            var editor = Read<LogView>(tab, "Log");
            await editor.InitialPageLoad;
            var page = LogText(editor);
            Assert.False(page.IsReadOnly);
            await CallAsync(window, "RememberClosedAsync", tab, false, false);
            var closed = Assert.Single((List<SessionTab>)Field(window, "_closedTabs"));
            page.Text = "правка\r\n" + page.Text!["строка\r\n".Length..];
            await CallAsync(window, "RestoreClosedAsync", closed);
            Assert.Same(editor, Read<LogView>(ActiveTab(window), "Log"));
            Assert.True(editor.HasPendingEdit);
            await editor.SavePageEditAsync();
            Assert.False(page.IsReadOnly);
            Assert.False(editor.HasPendingEdit);
            Assert.StartsWith("правка\r\n", page.Text);
            Assert.Equal("правка\r\n" + original["строка\r\n".Length..], await File.ReadAllTextAsync(path));
        }
        finally { await CloseAsync(window); }
    });

    private static async Task CreateFileWithLengthAsync(string path, long length)
    {
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("test row\n", 200)));
        using var file = File.OpenWrite(path);
        file.SetLength(length);
    }

    private static async Task<Window> WaitForLargeFileDialogAsync(MainWindow window)
    {
        await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog =>
            dialog.GetVisualDescendants().OfType<CheckBox>().Any(box => Equals(box.Content, "Запомнить выбор"))));
        return Assert.Single(window.OwnedWindows);
    }

    private static Button DialogButton(Window dialog, string caption) =>
        Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, caption));

    [Fact]
    public Task IdleLogReusesCheckpointUntilBookmarkChanges() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "stationary.log");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("line\n", 200)));
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", path, true, null);
            var log = Read<LogView>(ActiveTab(window), "Log");
            await log.InitialPageLoad;
            var pointer = Path.Combine(profile, "dotnet-demo", "session", "latest.json");
            await CallAsync(window, "SaveSessionAsync");
            var first = await File.ReadAllTextAsync(pointer);
            await CallAsync(window, "SaveSessionAsync");
            Assert.Equal(first, await File.ReadAllTextAsync(pointer));

            await (Task)typeof(LogView).GetMethod("NavigateAsync", Private)!.Invoke(log, [100L, true])!;
            await CallAsync(window, "SaveSessionAsync");
            Assert.NotEqual(first, await File.ReadAllTextAsync(pointer));
            var saved = await StorageService.OpenForTesting(profile).LoadSessionAsync();
            Assert.Equal(100L, Assert.Single(saved!.Tabs).Log!.Offset);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task UpdateChecksAllWindowsAndSavesTheirTabsBeforeStartingHelper() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "page.log");
        await File.WriteAllTextAsync(path, "original");
        var window = await OpenWorkspaceAsync();
        MainWindow? detached = null;
        try
        {
            Invoke(window, "NewTab", "unsaved draft");
            await CallAsync(window, "OpenPathAsync", path, true, null);
            Invoke(window, "OpenTabWindow", ActiveTab(window), null);
            detached = Assert.IsType<MainWindow>(window.FindOtherWindow());
            await HeadlessTests.WaitForAsync(() => Property(ActiveTab(detached), "Log") is LogView);
            var log = Read<LogView>(ActiveTab(detached), "Log");
            await log.InitialPageLoad;
            await EditLogPageAsync(log, "pending edit");
            var starts = 0;
            Func<Task> start = () => { starts++; return Task.CompletedTask; };
            await Assert.ThrowsAsync<IOException>(() => CallAsync(window, "RestartForUpdateAsync", start));
            Assert.Equal(0, starts);
            Assert.Equal("pending edit", LogText(log).Text);
            Assert.True(window.IsVisible && detached.IsVisible);

            await log.SavePageEditAsync();
            Func<Task> failed = () => Task.FromException(new IOException("helper unavailable"));
            await Assert.ThrowsAsync<IOException>(() => CallAsync(window, "RestartForUpdateAsync", failed));
            Assert.True(window.IsEnabled && detached.IsEnabled);
            var storage = StorageService.OpenForTesting(profile);
            await CallAsync(window, "RestartForUpdateAsync", (Func<Task>)(async () =>
            {
                starts++;
                var saved = Assert.IsType<SessionState>(await storage.LoadSessionAsync());
                Assert.Equal(2, saved.Tabs.Count);
                Assert.Equal("unsaved draft", await storage.LoadDraftAsync(saved.Tabs.Single(tab => tab.Path is null)));
                Assert.Contains(saved.Tabs, tab => tab.Path == path);
            }));
            Assert.Equal(1, starts);
            Assert.False(window.IsVisible);
            Assert.False(detached.IsVisible);
            Assert.Equal("pending edit", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (detached is { IsVisible: true }) await CloseAsync(detached);
            if (window.IsVisible) await CloseAsync(window);
        }
    });

    private static TextBox LogText(LogView log) => (TextBox)typeof(LogView).GetField("_logText", Private)!.GetValue(log)!;

    private static async Task EditLogPageAsync(LogView log, string content)
    {
        await (Task)typeof(LogView).GetMethod("TogglePageEditAsync", Private)!.Invoke(log, null)!;
        Assert.False(log.IsReadOnly);
        LogText(log).Text = content;
    }

    [Fact]
    public Task ExternalChangesReloadAnInactiveFileAndKeepTheView() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "watched.txt");
        var other = Path.Combine(profile, "other.txt");
        await File.WriteAllTextAsync(path, "before: retained selection");
        await File.WriteAllTextAsync(other, "active document");
        var window = await OpenWorkspaceAsync(path, other);
        try
        {
            var watched = Tabs(window).Single(tab => Equals(Property(tab, "Path"), path));
            var original = Read<TextEditor>(watched, "Editor");
            original.Select(8, 8);
            var temporary = Path.Combine(profile, "replacement.tmp");
            await File.WriteAllTextAsync(temporary, "after:  retained selection");
            File.Move(temporary, path, overwrite: true);
            await HeadlessTests.WaitForAsync(() => Read<TextEditor>(watched, "Editor").Text == "after:  retained selection");
            Assert.Equal(other, Property(ActiveTab(window), "Path"));
            Assert.Equal(8, Read<TextEditor>(watched, "Editor").SelectionStart);
            Assert.Equal(8, Read<TextEditor>(watched, "Editor").SelectionLength);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task AutoRefreshMenuPersistsTheFlagAndMovesUnsavedChangesToDraft() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "toggle.txt");
        await File.WriteAllTextAsync(path, "before");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var tab = ActiveTab(window);
            var context = Assert.IsType<ContextMenu>(Read<TabItem>(tab, "Item").ContextMenu);
            var refresh = Assert.Single(context.Items.OfType<MenuItem>(), item => Equals(item.Header, "Автообновление с диска"));
            Assert.Equal(MenuItemToggleType.CheckBox, refresh.ToggleType);
            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(Read<bool>(tab, "AutoRefresh"));
            await File.WriteAllTextAsync(path, "changed while paused");
            await Task.Delay(700);
            Assert.Equal("before", Read<TextEditor>(tab, "Editor").Text);
            await CallAsync(window, "SaveSessionAsync");
            var saved = await StorageService.OpenForTesting(profile).LoadSessionAsync();
            Assert.False(Assert.Single(saved!.Tabs).AutoRefresh);

            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessTests.WaitForAsync(() => Read<TextEditor>(tab, "Editor").Text == "changed while paused");
            var editor = Read<TextEditor>(tab, "Editor");
            editor.Document.Insert(editor.Document.TextLength, " + local");
            await File.WriteAllTextAsync(path, "another external value");
            await HeadlessTests.WaitForAsync(() => Read<TextEditor>(tab, "Editor").Text == "another external value");
            var draft = Assert.Single(Tabs(window), candidate => Property(candidate, "Path") is null);
            Assert.Equal("changed while paused + local", Read<TextEditor>(draft, "Editor").Text);
            Assert.True(Read<bool>(draft, "Dirty"));
            Assert.Same(tab, ActiveTab(window));
            Assert.Equal(MainWindow.ExternalDraftStatus, Property(tab, "DiskStatus"));
            Assert.True(Read<bool>(tab, "AutoRefresh"));

            await File.AppendAllTextAsync(path, "\nnext external line");
            await HeadlessTests.WaitForAsync(() => Read<TextEditor>(tab, "Editor").Text.EndsWith("next external line"));
            Assert.Single(Tabs(window), candidate => Property(candidate, "Path") is null);
            await CallAsync(window, "SaveSessionAsync");
            var storage = StorageService.OpenForTesting(profile);
            var session = await storage.LoadSessionAsync();
            var savedDraft = Assert.Single(session!.Tabs, candidate => candidate.Path is null);
            Assert.Equal("changed while paused + local", await File.ReadAllTextAsync(storage.GetDraftPath(savedDraft)!));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task RepeatedClipboardPasteKeepsFocusAndAdvancesTheCaret() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "paste.txt");
        await File.WriteAllTextAsync(path, "αoldω");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            editor.TextArea.Focus();
            editor.Select(1, 3);
            editor.CaretOffset = 4;
            await window.Clipboard!.SetTextAsync("Ж");

            // Hold Control and repeat the same key-down event before releasing V.
            for (var index = 0; index < 4; index++)
                window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            window.KeyRelease(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");

            await HeadlessTests.WaitForAsync(() => editor.Text == "αЖЖЖЖω");
            Assert.True(editor.TextArea.IsKeyboardFocusWithin);
            Assert.Equal(5, editor.CaretOffset);
            Assert.Equal(0, editor.SelectionLength);
            window.KeyTextInput("!");
            Assert.Equal("αЖЖЖЖ!ω", editor.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public Task MultiCaretPasteKeepsJsonStringsValidWithoutInsertingLineBreaks(string separator) =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string original = """
                [
                  { "name": "old1", "type": "A" },
                  { "name": "old2", "type": "B" },
                  { "name": "old3", "type": "C" }
                ]
                """;
            var path = Path.Combine(profile, "multiple.json");
            await File.WriteAllTextAsync(path, original);
            var window = await OpenWorkspaceAsync(path);
            try
            {
                var editor = Read<TextEditor>(ActiveTab(window), "Editor");
                editor.TextArea.Focus();
                var before = editor.Text;
                editor.CaretOffset = before.IndexOf("old1", StringComparison.Ordinal);
                for (var index = 0; index < 2; index++)
                    Press(window, Key.Down, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.ArrowDown);
                for (var index = 0; index < 4; index++)
                    Press(window, Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight);
                Assert.Equal(new[] { 4, 4, 4 }, editor.TextArea.Selection.Segments.Select(segment => segment.Length));
                await window.Clipboard!.SetTextAsync(string.Join(separator, ["new1", "new2", "new3"]));
                Press(window, Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
                var expected = before.Replace("old", "new", StringComparison.Ordinal);
                await HeadlessTests.WaitForAsync(() => editor.Text == expected);
                using var parsed = JsonDocument.Parse(editor.Text);
                Assert.Equal(new[] { "new1", "new2", "new3" },
                    parsed.RootElement.EnumerateArray().Select(item => item.GetProperty("name").GetString()));
            }
            finally { await CloseAsync(window); }
        });

    [Fact]
    public Task VerticalCaretsEditUnicodeAndUndoAsOneOperation() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "columns.txt");
        await File.WriteAllTextAsync(path, "abc\nx\nabc");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            editor.TextArea.Focus();
            editor.CaretOffset = 2;
            const RawInputModifiers controlAlt = RawInputModifiers.Control | RawInputModifiers.Alt;
            Press(window, Key.Down, controlAlt, PhysicalKey.ArrowDown);
            Press(window, Key.Down, controlAlt, PhysicalKey.ArrowDown);
            Assert.Equal(3, MultiCaretEditing.Count(editor.TextArea));
            Assert.Equal(8, editor.CaretOffset);

            window.KeyTextInput("🙂");
            Assert.Equal("ab🙂c\nx🙂\nab🙂c", editor.Text);
            Press(window, Key.Back, RawInputModifiers.None, PhysicalKey.Backspace);
            Assert.Equal("abc\nx\nabc", editor.Text);

            editor.Undo();
            Assert.Equal("ab🙂c\nx🙂\nab🙂c", editor.Text);
            Assert.Equal(3, MultiCaretEditing.Count(editor.TextArea));
            editor.Undo();
            Assert.Equal("abc\nx\nabc", editor.Text);
            editor.Redo();
            Assert.Equal("ab🙂c\nx🙂\nab🙂c", editor.Text);
            Assert.Equal(14, editor.CaretOffset);

            Press(window, Key.Left, RawInputModifiers.Shift, PhysicalKey.ArrowLeft);
            Assert.Equal(new[] { 2, 2, 2 }, editor.TextArea.Selection.Segments.Select(segment => segment.Length));
            Press(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
            Assert.Equal(1, MultiCaretEditing.Count(editor.TextArea));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task OccurrenceSelectionsCopyCutAndPasteByDocumentOrder() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "occurrences.txt");
        await File.WriteAllTextAsync(path, "foo\nfoo and foo");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            editor.TextArea.Focus();
            editor.CaretOffset = 5;
            Press(window, Key.D, RawInputModifiers.Control, PhysicalKey.D, "d");
            Assert.Equal("foo", editor.SelectedText);
            Press(window, Key.D, RawInputModifiers.Control, PhysicalKey.D, "d");
            Assert.Equal(2, MultiCaretEditing.Count(editor.TextArea));
            Press(window, Key.L, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.L, "L");
            Assert.Equal(3, MultiCaretEditing.Count(editor.TextArea));

            ApplicationCommands.Copy.Execute(null, editor.TextArea);
            var copied = await window.Clipboard!.TryGetDataAsync();
            Assert.NotNull(copied);
            Assert.Equal(string.Join(Environment.NewLine, new[] { "foo", "foo", "foo" }), await copied.TryGetTextAsync());

            ApplicationCommands.Cut.Execute(null, editor.TextArea);
            Assert.Equal("\n and ", editor.Text);
            Assert.Equal(3, MultiCaretEditing.Count(editor.TextArea));
            await window.Clipboard.SetTextAsync("1\r\n2\r\n3");
            Press(window, Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await HeadlessTests.WaitForAsync(() => editor.Text == "1\n2 and 3");
            Assert.True(editor.TextArea.IsKeyboardFocusWithin);

            await window.Clipboard.SetTextAsync("!");
            Press(window, Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await HeadlessTests.WaitForAsync(() => editor.Text == "1!\n2! and 3!");
            Assert.Equal(3, MultiCaretEditing.Count(editor.TextArea));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task AltClickTogglesCaretsAndUpAddsOneOnThePreviousLine() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "mouse.txt");
        await File.WriteAllTextAsync(path, "aaa\nbbb\nccc");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            editor.TextArea.Focus();
            editor.CaretOffset = 9;
            Press(window, Key.Up, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.ArrowUp);
            Assert.Equal(2, MultiCaretEditing.Count(editor.TextArea));
            var view = editor.TextArea.TextView;
            view.EnsureVisualLines();
            var location = new TextViewPosition(1, 3);
            var point = view.TranslatePoint(view.GetVisualPosition(location, VisualYPosition.LineMiddle) - view.ScrollOffset, window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.Alt);
            Assert.Equal(3, MultiCaretEditing.Count(editor.TextArea));
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.Alt);
            Assert.Equal(2, MultiCaretEditing.Count(editor.TextArea));
            window.KeyTextInput("!");
            Assert.Equal("aaa\nb!bb\nc!cc", editor.Text);
            var dragStart = AtTextPosition(1, 1);
            var dragEnd = AtTextPosition(3, 2);
            window.MouseDown(dragStart, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseMove(dragEnd, RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
            window.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.Alt);
            var rectangle = Assert.IsType<RectangleSelection>(editor.TextArea.Selection);
            Assert.Equal(3, rectangle.Segments.Count());
            Assert.All(rectangle.Segments, segment => Assert.Equal(1, segment.Length));

            Point AtTextPosition(int line, int column) =>
                view.TranslatePoint(view.GetVisualPosition(new TextViewPosition(line, column),
                    VisualYPosition.LineMiddle) - view.ScrollOffset, window)!.Value;
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ExternalLogAppendKeepsRefreshingAfterPageTyping() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "live.log");
        await File.WriteAllTextAsync(path, "initial\n");
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", path, true, null);
            var tab = ActiveTab(window);
            var log = Read<LogView>(tab, "Log");
            await log.InitialPageLoad;
            await EditLogPageAsync(log, "unedited on disk, edited here");
            Assert.True(log.HasPendingEdit);
            await File.AppendAllTextAsync(path, "appended\n");

            await HeadlessTests.WaitForAsync(() => log.IsReadOnly && LogText(log).Text!.Contains("appended"));
            Assert.True(log.AutoRefresh);
            var draft = Assert.Single(Tabs(window), candidate => Property(candidate, "Path") is null);
            Assert.Equal("unedited on disk, edited here", Read<TextEditor>(draft, "Editor").Text);
            Assert.Same(tab, ActiveTab(window));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ExternalRefreshKeepsADirtyDraftWhenTheFileSwitchesToLogView() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "growing.txt");
        await File.WriteAllTextAsync(path, "short");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            editor.Document.Insert(5, " + local");
            await File.WriteAllTextAsync(path, new string('x', 65_537));

            await HeadlessTests.WaitForAsync(() => Property(tab, "Log") is LogView);
            var log = Read<LogView>(tab, "Log");
            await log.InitialPageLoad;
            Assert.False(Read<bool>(tab, "Dirty"));
            Assert.True(log.AutoRefresh);
            Assert.StartsWith("xxx", LogText(log).Text);
            var draft = Assert.Single(Tabs(window), candidate => Property(candidate, "Path") is null);
            Assert.Equal("short + local", Read<TextEditor>(draft, "Editor").Text);
            Assert.Same(tab, ActiveTab(window));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ReloadKeepsNewTypingAndUndo() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "reload.txt");
        await File.WriteAllTextAsync(path, "исходный файл");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            var loaded = await DocumentFiles.ReadAsync(path);
            var pending = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reload = CallAsync(window, "ApplyEditorReloadAsync", tab, editor, pending.Task);

            editor.TextArea.Focus();
            editor.CaretOffset = editor.Document.TextLength;
            window.KeyTextInput(" + правка");
            Assert.EndsWith(" + правка", editor.Text);
            pending.SetResult(loaded);
            await reload;

            Assert.Same(editor, Read<TextEditor>(ActiveTab(window), "Editor"));
            Assert.Equal("исходный файл + правка", editor.Text);
            Assert.True(Read<bool>(tab, "Dirty"));
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Equal("исходный файл", editor.Text);
            Assert.False(Read<bool>(tab, "Dirty"));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task InactiveTabHoverChangesOnlyHoveredSurface() => HeadlessTests.RunAsync(async profile =>
    {
        var firstPath = Path.Combine(profile, "first.txt");
        var secondPath = Path.Combine(profile, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first document");
        await File.WriteAllTextAsync(secondPath, "second document");
        var window = await OpenWorkspaceAsync(firstPath, secondPath);
        try
        {
            var group = Read<object>(ActiveTab(window), "Group");
            var tabs = Read<TabControl>(group, "Tabs");
            var items = tabs.Items.OfType<TabItem>().ToArray();
            Assert.Equal(2, items.Length);
            var selected = items[0];
            var inactive = items[1];
            tabs.SelectedItem = selected;
            window.MouseMove(new Point(-10, -10));
            await Task.Delay(30);

            var selectedSurface = TabSurface(selected);
            var inactiveSurface = TabSurface(inactive);
            var selectedBackground = SolidColor(selectedSurface.Background);
            var selectedAccent = SolidColor(selectedSurface.BorderBrush);
            var inactiveBackground = SolidColor(inactiveSurface.Background);
            var hoverPoint = inactiveSurface.TranslatePoint(
                new Point(inactiveSurface.Bounds.Width / 2, inactiveSurface.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("Inactive tab surface is not attached.");

            window.MouseMove(hoverPoint);
            await Task.Delay(30);

            Assert.NotEqual(inactiveBackground, SolidColor(inactiveSurface.Background));
            Assert.Equal(selectedBackground, SolidColor(selectedSurface.Background));
            Assert.Equal(selectedAccent, SolidColor(selectedSurface.BorderBrush));
            Assert.Same(selected, tabs.SelectedItem);
        }
        finally
        {
            await CloseAsync(window);
        }
    });
    [Fact]
    public Task ReloadCompletionDoesNotReopenClosedTab() => HeadlessTests.RunAsync(async profile =>
    {
        var first = Path.Combine(profile, "active.txt");
        var second = Path.Combine(profile, "closing.txt");
        await File.WriteAllTextAsync(first, "остаётся в окне");
        await File.WriteAllTextAsync(second, "закрывается при чтении");
        var window = await OpenWorkspaceAsync(first, second);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            var loaded = await DocumentFiles.ReadAsync(second);
            var pending = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reload = CallAsync(window, "ApplyEditorReloadAsync", tab, editor, pending.Task);
            await CallAsync(window, "CloseTabAsync", tab);
            pending.SetResult(loaded);
            await reload;

            Assert.Single(Tabs(window));
            Assert.Equal("остаётся в окне", Read<TextEditor>(ActiveTab(window), "Editor").Text);
            Assert.Null(Property(tab, "Editor"));
            Assert.Equal(0, editor.Document.TextLength);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task ClosingDetachedWindowKeepsDirtyTabsInHistoryInsteadOfTransferringThem() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "move.txt");
            await File.WriteAllTextAsync(path, "исходный документ");
            var original = await OpenWorkspaceAsync(path);
            MainWindow? detached = null;
            try
            {
                var tab = Tabs(original).Single(candidate => Equals(Property(candidate, "Path"), path));
                var editor = Read<TextEditor>(tab, "Editor");
                var document = editor.Document;
                document.Insert(document.TextLength, " + правка");
                Assert.True(document.UndoStack.CanUndo);
                Invoke(original, "NewTab", "сохранённая вкладка исходного окна");
                Invoke(original, "Select", tab);

                var item = Read<TabItem>(tab, "Item");
                var move = item.ContextMenu!.Items.OfType<MenuItem>()
                    .Single(menu => Equals(menu.Header, "Переместить в новое окно"));
                move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                detached = Assert.IsType<MainWindow>(original.FindOtherWindow());
                Assert.Single(Tabs(original));
                var movedTab = ActiveTab(detached);
                Assert.Same(document, Read<TextEditor>(movedTab, "Editor").Document);
                Assert.Equal("исходный документ + правка", document.Text);
                Assert.True(Read<bool>(movedTab, "Dirty"));
                Invoke(detached, "NewTab", "несохранённый черновик отдельного окна");

                detached.Close();
                await HeadlessTests.WaitForAsync(() => (bool)Field(detached, "_closed"));
                Assert.False(detached.IsVisible);
                Assert.True(original.IsVisible);
                Assert.Single(Tabs(original));
                Assert.Equal("сохранённая вкладка исходного окна", Read<TextEditor>(ActiveTab(original), "Editor").Text);

                var closed = ((System.Collections.IEnumerable)Field(original, "_closedTabs")).Cast<object>().ToArray();
                var closedFile = Assert.Single(closed, saved => Equals(Property(saved, "Path"), path));
                Assert.NotNull(Property(closedFile, "DraftRef"));
                var closedUntitled = Assert.Single(closed, saved =>
                    Property(saved, "UntitledName") is string name && name.StartsWith("несохранён", StringComparison.Ordinal));
                Assert.NotNull(Property(closedUntitled, "DraftRef"));

                await CallAsync(original, "SaveSessionAsync");
                await CloseAsync(original);
                original = await OpenWorkspaceAsync();

                closed = ((System.Collections.IEnumerable)Field(original, "_closedTabs")).Cast<object>().ToArray();
                closedFile = Assert.Single(closed, saved => Equals(Property(saved, "Path"), path));
                closedUntitled = Assert.Single(closed, saved =>
                    Property(saved, "UntitledName") is string name && name.StartsWith("несохранён", StringComparison.Ordinal));
                await CallAsync(original, "RestoreClosedAsync", closedFile);
                await HeadlessTests.WaitForAsync(() => Tabs(original).Any(candidate =>
                    Equals(Property(candidate, "Path"), path)
                    && Property(candidate, "Editor") is TextEditor restored && restored.Text == "исходный документ + правка"));
                var restoredFile = Tabs(original).Single(candidate => Equals(Property(candidate, "Path"), path));
                Assert.True(Read<bool>(restoredFile, "Dirty"));

                await CallAsync(original, "RestoreClosedAsync", closedUntitled);
                await HeadlessTests.WaitForAsync(() => Tabs(original).Any(candidate =>
                    Property(candidate, "Editor") is TextEditor restored && restored.Text == "несохранённый черновик отдельного окна"));
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (original.IsVisible) await CloseAsync(original);
            }
        });

    [Fact]
    public Task MovingFinalTabToNewWindowClosesEmptySourceAndLastCloseRestoresSession() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "last-tab.txt");
            await File.WriteAllTextAsync(path, "финальный документ");
            var original = await OpenWorkspaceAsync(path);
            MainWindow? detached = null;
            MainWindow? reopened = null;
            try
            {
                var tab = ActiveTab(original);
                var move = Read<TabItem>(tab, "Item").ContextMenu!.Items.OfType<MenuItem>()
                    .Single(menu => Equals(menu.Header, "Переместить в новое окно"));
                move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                detached = Assert.IsType<MainWindow>(original.FindOtherWindow());
                await HeadlessTests.WaitForAsync(() => (bool)Field(original, "_closed"));
                Assert.False(original.IsVisible);
                Assert.Single(Tabs(detached));
                Assert.Equal("финальный документ", Read<TextEditor>(ActiveTab(detached), "Editor").Text);

                await CloseAsync(detached);
                reopened = await OpenWorkspaceAsync();
                await HeadlessTests.WaitForAsync(() => Tabs(reopened).Any(candidate =>
                    Equals(Property(candidate, "Path"), path)
                    && Property(candidate, "Editor") is TextEditor editor && editor.Text == "финальный документ"));
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (reopened is { IsVisible: true }) await CloseAsync(reopened);
                if (original.IsVisible) await CloseAsync(original);
            }
        });

    [Fact]
    public Task SplitButtonsAndClosingNestedGroupsPreserveDraftOrderAndActiveTabAfterRestart() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "original.txt");
            await File.WriteAllTextAsync(path, "оригинал");
            var window = await OpenWorkspaceAsync(path);
            var left = Field(window, "_active");
            Invoke(window, "NewTab", "первый черновик");
            var across = GroupButton(left, "│");
            Assert.Equal("Разделить слева и справа", ToolTip.GetTip(across));
            Click(across);
            Assert.Equal<Orientation?>(Orientation.Horizontal, Read<Orientation?>(Field(window, "_root"), "Axis"));
            var right = Field(window, "_active");
            Invoke(window, "NewTab", "второй черновик");
            Invoke(window, "NewTab", "третий черновик");
            var moved = ActiveTab(window);

            var below = GroupButton(right, "─");
            Assert.Equal("Разделить сверху и снизу", ToolTip.GetTip(below));
            Click(below);
            Assert.Equal<Orientation?>(Orientation.Vertical,
                Read<Orientation?>(Read<object>(Field(window, "_root"), "Second"), "Axis"));
            var bottom = Field(window, "_active");
            Assert.Same(moved, ActiveTab(window));
            Invoke(window, "MoveTab", moved, right, null);
            Assert.Empty(Read<TabControl>(bottom, "Tabs").Items);
            Click(GroupButton(bottom, "Закрыть группу"));
            Assert.Equal(2, Groups(window).Length);
            Click(GroupButton(right, "Закрыть группу"));
            Assert.Single(Groups(window));
            Assert.Same(moved, ActiveTab(window));
            Assert.False(GroupButton(left, "Закрыть группу").IsEnabled);
            Assert.Equal(["оригинал", "первый черновик", "второй черновик", "третий черновик"], TabTexts(window, left));
            await CloseAsync(window);

            var reopened = await OpenWorkspaceAsync();
            try
            {
                var group = Assert.Single(Groups(reopened));
                await HeadlessTests.WaitForAsync(() => Property(ActiveTab(reopened), "Editor") is TextEditor);
                Assert.Equal("третий черновик", Read<TextEditor>(ActiveTab(reopened), "Editor").Text);
                foreach (var item in Read<TabControl>(group, "Tabs").Items.OfType<TabItem>())
                {
                    var tab = Tabs(reopened).Single(tab => ReferenceEquals(Read<TabItem>(tab, "Item"), item));
                    await CallAsync(reopened, "ActivateDeferredAsync", tab);
                }
                Assert.Equal(["оригинал", "первый черновик", "второй черновик", "третий черновик"], TabTexts(reopened, group));
            }
            finally
            {
                await CloseAsync(reopened);
            }
        });

    [Fact]
    public Task ClosingLeftGroupKeepsTabsInVisualOrder() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "left.txt");
        await File.WriteAllTextAsync(path, "первый");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var left = Field(window, "_active");
            Invoke(window, "NewTab", "второй");
            Click(GroupButton(left, "│"));
            var right = Field(window, "_active");
            var activeDraft = ActiveTab(window);
            Invoke(window, "NewTab", "третий");
            var lastDraft = ActiveTab(window);

            Click(GroupButton(left, "Закрыть группу"));
            Assert.Same(right, Assert.Single(Groups(window)));
            Assert.Same(lastDraft, ActiveTab(window));
            Assert.True(Read<bool>(activeDraft, "Dirty"));
            Assert.Equal(["первый", "второй", "третий"], TabTexts(window, right));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task ClosedHistoryAndDeferredDraftSurviveFourChangedSessions() => HeadlessTests.RunAsync(async _ =>
    {
        var earlierText = "ранний\n" + string.Join('\n', Enumerable.Range(1, 220).Select(index => $"строка {index}"));
        const string recentText = "последний\nпоследний закрытый черновик";
        const string backgroundText = "отложенный\nтекст фоновой вкладки";
        var initial = await OpenWorkspaceAsync();
        Invoke(initial, "ShowGroups");
        Invoke(initial, "NewTab", earlierText);
        var earlier = Read<TextEditor>(ActiveTab(initial), "Editor");
        await HeadlessTests.WaitForAsync(() => earlier.GetVisualDescendants().OfType<ScrollViewer>()
            .Any(view => view.Extent.Height > view.Viewport.Height));
        var scroll = earlier.GetVisualDescendants().OfType<ScrollViewer>().First();
        earlier.Select(42, 9);
        earlier.CaretOffset = 42;
        scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, 320));
        await CallAsync(initial, "CloseTabAsync", ActiveTab(initial));

        Invoke(initial, "ShowGroups");
        Invoke(initial, "NewTab", recentText);
        await CallAsync(initial, "CloseTabAsync", ActiveTab(initial));
        Invoke(initial, "ShowGroups");
        Invoke(initial, "NewTab", backgroundText);
        Invoke(initial, "NewTab", "активный");
        await CloseAsync(initial);

        var working = await OpenWorkspaceAsync();
        await HeadlessTests.WaitForAsync(() => Property(ActiveTab(working), "Editor") is TextEditor);
        var deferred = Tabs(working).Single(tab => Property(tab, "Deferred") is SessionTab);
        var active = Read<TextEditor>(ActiveTab(working), "Editor");
        for (var generation = 1; generation <= 4; generation++)
        {
            active.Document.Insert(active.Document.TextLength, $" · {generation}");
            await CallAsync(working, "SaveSessionAsync");
            Assert.Null(Property(deferred, "Editor"));
        }
        await CloseAsync(working);

        var final = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => Property(ActiveTab(final), "Editor") is TextEditor);
            var background = Tabs(final).Single(tab => Property(tab, "Deferred") is SessionTab);
            await CallAsync(final, "ActivateDeferredAsync", background);
            Assert.Equal(backgroundText, Read<TextEditor>(background, "Editor").Text);

            const RawInputModifiers shortcut = RawInputModifiers.Control | RawInputModifiers.Shift;
            final.KeyPress(Key.T, shortcut, PhysicalKey.T, "t");
            final.KeyRelease(Key.T, shortcut, PhysicalKey.T, "t");
            await HeadlessTests.WaitForAsync(() => Property(ActiveTab(final), "Editor") is TextEditor editor && editor.Text == recentText);

            var closedMenu = (MenuItem)Field(final, "_closedDraftsMenu");
            var older = Assert.Single(closedMenu.Items.OfType<MenuItem>(), item =>
                item.Header is string title && title.StartsWith("ранний", StringComparison.Ordinal));
            older.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessTests.WaitForAsync(() => Property(ActiveTab(final), "Editor") is TextEditor editor && editor.Text == earlierText);
            earlier = Read<TextEditor>(ActiveTab(final), "Editor");
            await HeadlessTests.WaitForAsync(() => Math.Abs(earlier.VerticalOffset - 320) < 1);
            Assert.Equal(42, earlier.SelectionStart);
            Assert.Equal(9, earlier.SelectionLength);
            Assert.Equal(42, earlier.CaretOffset);
        }
        finally
        {
            await CloseAsync(final);
        }
    });

    [Fact]
    public Task NulInTextStartupDoesNotPreventOpeningAnotherFile() => HeadlessTests.RunAsync(async profile =>
    {
        var bad = Path.Combine(profile, "nul.txt");
        var good = Path.Combine(profile, "following.txt");
        await File.WriteAllBytesAsync(bad, [0x61, 0, 0x62]);
        await File.WriteAllTextAsync(good, "окно работает");
        var window = await OpenWorkspaceAsync(bad, good);
        try
        {
            Assert.Single(Tabs(window));
            Assert.Equal("окно работает", Read<TextEditor>(ActiveTab(window), "Editor").Text);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task TextMinimapClickDragAndWheelKeepCaretAndSelection() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "scroll.txt");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 1500).Select(index => $"Строка {index} · пример текста"));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var minimap = Assert.Single(editor.GetVisualParent()!.GetVisualChildren().OfType<DocumentMinimap>());
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            await HeadlessTests.WaitForAsync(() => scroll.Extent.Height > scroll.Viewport.Height && minimap.Bounds.Height > 10);
            editor.Select(3, 5);
            editor.CaretOffset = 8;
            var top = minimap.TranslatePoint(new Point(20, 1), window)!.Value;
            var middle = minimap.TranslatePoint(new Point(20, minimap.Bounds.Height / 2), window)!.Value;
            var bottom = minimap.TranslatePoint(new Point(20, minimap.Bounds.Height + 2), window)!.Value;
            var maximum = scroll.Extent.Height - scroll.Viewport.Height;

            window.MouseDown(middle, MouseButton.Left);
            Assert.InRange(scroll.Offset.Y, maximum * .45, maximum * .55);
            window.MouseMove(bottom, RawInputModifiers.LeftMouseButton);
            Assert.InRange(scroll.Offset.Y, maximum - 1, maximum + 1);
            window.MouseUp(bottom, MouseButton.Left);
            window.MouseWheel(middle, new Vector(0, 1));
            Assert.True(scroll.Offset.Y < maximum - 1);
            window.MouseDown(top, MouseButton.Left);
            window.MouseUp(top, MouseButton.Left);
            Assert.InRange(scroll.Offset.Y, 0, maximum * .01);
            Assert.Equal(3, editor.SelectionStart);
            Assert.Equal(5, editor.SelectionLength);
            Assert.Equal(8, editor.CaretOffset);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public void JsonTokensDistinguishKeysValuesAndEscapedQuotes()
    {
        const string line = """{"meta": {"say": "a\"b", "n": -1.25e+2, "flags": [true, false, null]}}""";
        var tokens = JsonColorizer.Tokenize(line)
            .Where(token => token.Kind != JsonTokenKind.Punctuation)
            .Select(token => (line.Substring(token.Start, token.Length), token.Kind)).ToArray();
        Assert.Equal(
            [("meta", JsonTokenKind.Key), ("say", JsonTokenKind.Key), ("""a\"b""", JsonTokenKind.String),
             ("n", JsonTokenKind.Key), ("-1.25e+2", JsonTokenKind.Number), ("flags", JsonTokenKind.Key),
             ("true", JsonTokenKind.Literal), ("false", JsonTokenKind.Literal), ("null", JsonTokenKind.Literal)],
            tokens);
        Assert.Contains(JsonColorizer.Tokenize(""" "key"  """, () => true), token => token.Kind == JsonTokenKind.Key);
        Assert.Contains(JsonColorizer.Tokenize(""" {"value": "unterminated"""), token => token.Kind == JsonTokenKind.String);
    }

    [Theory]
    [InlineData("events.jsonl", true)]
    [InlineData("events.NDJSON", true)]
    [InlineData("events.log", false)]
    public Task JsonLinesValidatesAndFormatsEachRecord(string name, bool highlight) => HeadlessTests.RunAsync(async profile =>
    {
        const string first = """{ "kind": "start" }""";
        const string valid = """{ "kind": "имя", "value": true }""";
        const string invalid = """{"kind":"имя🚀","value": }""";
        var path = Path.Combine(profile, name);
        await File.WriteAllTextAsync(path, first + "\r\n" + valid + "\r\n");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            await HeadlessTests.WaitForAsync(() => Property(tab, "JsonCancellation") is null);
            Assert.Null(Property(tab, "JsonError"));
            Assert.Equal(highlight, Assert.Single(editor.TextArea.TextView.LineTransformers.OfType<JsonColorizer>()).Enabled);

            editor.Document.Replace(first.Length + 1, valid.Length, invalid);
            await HeadlessTests.WaitForAsync(() => Property(tab, "JsonError") is not null);
            var error = Read<JsonProblem>(tab, "JsonError");
            Assert.Equal(2, error.Line);
            Assert.Equal(invalid.Length, error.Column);
            Assert.Equal(first.Length + 1 + invalid.Length - 1, error.Offset);

            editor.Document.Replace(first.Length + 1, invalid.Length, valid);
            await CallAsync(window, "FormatJsonAsync");
            Assert.Equal("{\"kind\":\"start\"}\n{\"kind\":\"имя\",\"value\":true}\n", editor.Text);
            await HeadlessTests.WaitForAsync(() => Property(tab, "JsonCancellation") is null);
            Assert.Null(Property(tab, "JsonError"));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task AnonymousJsonKeepsHighlightingWithValidationSelectionAndSearch() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", """{"enabled": true, "labels": ["имя", "🚀"]}""");
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            Assert.True(Assert.Single(editor.TextArea.TextView.LineTransformers.OfType<JsonColorizer>()).Enabled);
            editor.Select(2, 3);
            editor.CaretOffset = 5;
            editor.Document.Insert(editor.Document.TextLength - 1, ",");
            await HeadlessTests.WaitForAsync(() => Property(tab, "JsonError") is not null);
            ((TextBox)Field(window, "_searchInput")).Text = "labels";
            await CallAsync(window, "FindAsync");
            Assert.Single(((ListBox)Field(window, "_searchResults")).Items);
            Assert.True(Assert.Single(editor.TextArea.TextView.LineTransformers.OfType<JsonColorizer>()).Enabled);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task DialogUsesTheWorkspacePalette() => HeadlessTests.RunAsync(async _ =>
    {
        var main = await OpenWorkspaceAsync();
        try
        {
            var dialogTask = CallAsync(main, "ShowSettingsAsync");
            var dialog = Assert.Single(main.OwnedWindows);
            Assert.Equal(SolidColor(FocusTheme.Editor), SolidColor(main.Background));
            Assert.Equal(SolidColor(FocusTheme.Panel), SolidColor(dialog.Background));
            Assert.Equal(((ISolidColorBrush)main.Foreground!).Color, ((ISolidColorBrush)dialog.Foreground!).Color);
            Assert.NotEqual(Colors.Black, ((ISolidColorBrush)dialog.Background!).Color);
            dialog.Close();
            await dialogTask;
        }
        finally
        {
            await CloseAsync(main);
        }
    });

    private static Border TabSurface(TabItem item) => Assert.Single(item.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabSurface");
    private static void Press(MainWindow window, Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? symbol = null)
    {
        window.KeyPress(key, modifiers, physicalKey, symbol);
        window.KeyRelease(key, modifiers, physicalKey, symbol);
    }

    private static Color SolidColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static async Task<MainWindow> OpenWorkspaceAsync(params string[] paths)
    {
        var window = new MainWindow(paths);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible ||
            window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Не удалось записать сессию.", StringComparison.Ordinal) == true);
        Assert.False(window.IsVisible, window.FindControl<TextBlock>("StatusText")?.Text);
    }

    private static object ActiveTab(MainWindow window) => Property(window, "ActiveTab", Private)!;
    private static object[] Groups(MainWindow window) => ((System.Collections.IEnumerable)Invoke(window, "Groups", [null])!).Cast<object>().ToArray();
    private static IEnumerable<object> Tabs(MainWindow window) => ((System.Collections.IDictionary)Field(window, "_tabs")).Values.Cast<object>();

    private static string[] TabTexts(MainWindow window, object group) => Read<TabControl>(group, "Tabs")
        .Items.OfType<TabItem>().Select(item => Read<TextEditor>(
            Tabs(window).Single(tab => ReferenceEquals(Read<TabItem>(tab, "Item"), item)), "Editor").Text).ToArray();

    private static Button GroupButton(object group, string name)
    {
        var tools = Assert.IsType<StackPanel>(Read<WorkspaceTabs>(group, "Tabs").HeaderActions);
        return Assert.Single(tools.Children.OfType<Button>(), button => Equals(button.Content, name)
            || string.Equals(Avalonia.Automation.AutomationProperties.GetName(button), name, StringComparison.Ordinal));
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);

    private static Task CallAsync(MainWindow window, string name, params object?[] args) => (Task)Invoke(window, name, args)!;
    private static object? Property(object target, string name, BindingFlags binding = BindingFlags.Instance | BindingFlags.Public) =>
        target.GetType().GetProperty(name, binding)!.GetValue(target);

    private static T Read<T>(object target, string name) => (T)Property(target, name)!;
}
