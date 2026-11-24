using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class HomeHistoryTests
{
    [Fact]
    public Task RefreshingRelativeTimeKeepsTheExistingHistoryRow() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "recent.txt");
        var worked = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        await StorageService.OpenForTesting(profile).SaveSessionAsync(new SessionState([],
            [new SessionTab(4, path, TextEncodingKind.Utf8, LineCount: 3, LastWorkedMs: worked)]));
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => HasButton(window, "recent.txt"));
            var row = FindButton(window, "recent.txt");
            var details = row.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Text == "3 строк · 5 мин назад");
            details.Text = "";
            ((Action)Field(window, "_refreshHomeTimes")!)();
            Assert.Same(row, FindButton(window, "recent.txt"));
            Assert.Equal("3 строк · 5 мин назад", details.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ClosedUntitledDraftAppearsOnHomeAndRestoresAfterRestart() => HeadlessTests.RunAsync(async _ =>
    {
        const string caption = "Черновик";
        const string draft = caption + "\nнесохранённый текст";
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", draft);
            await CallAsync(window, "CloseTabAsync", ActiveTab(window));

            await HeadlessTests.WaitForAsync(() =>
                window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Призрачные вкладки"));
            Assert.Contains(HomeButtons(window), button => AutomationProperties.GetName(button) == caption);

            await CloseAsync(window);
            window = await OpenWorkspaceAsync();

            await HeadlessTests.WaitForAsync(() => HasButton(window, caption));
            var closedDraft = FindButton(window, caption);
            closedDraft.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await HeadlessTests.WaitForAsync(() => ActiveEditor(window) is { Text: draft });
            Assert.Empty((System.Collections.IEnumerable)Field(window, "_closedTabs")!);
        }
        finally
        {
            if (window.IsVisible)
            {
                await CloseAsync(window);
            }
        }
    });

    [Fact]
    public Task ArchivedGhostStaysSearchableAndRestorableAcrossRestart() => HeadlessTests.RunAsync(async profile =>
    {
        const string title = "Старая заметка";
        const string text = title + "\nВторая строка";
        const string label = "📦 " + title + " · архив";
        var storage = StorageService.OpenForTesting(profile);
        var saved = await storage.SaveClosedDraftAsync(new SessionTab(41, null, TextEncodingKind.Utf8)
        {
            CustomTitle = title, ClosedAtMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()
        }, text);
        await storage.SaveSessionAsync(new SessionState([], [saved]));
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => HasButton(window, title));
            await CallAsync(window, "ArchiveClosedDraftsAsync");
            await HeadlessTests.WaitForAsync(() => HasButton(window, label));
            Assert.False(HasButton(window, title));
            Assert.False(File.Exists(storage.GetDraftPath(saved)));
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Поиск по имени или пути");
            search.Text = "нет совпадений";
            await HeadlessTests.WaitForAsync(() => !HasButton(window, label));
            search.Text = "заметка";
            await HeadlessTests.WaitForAsync(() => HasButton(window, label));

            void Toggle() => ((MenuItem)Field(window, "_showArchivedDrafts")!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Toggle();
            await HeadlessTests.WaitForAsync(() => !HasButton(window, label));
            Assert.Single(await storage.DraftArchive.GetEntriesAsync());
            await CloseAsync(window);
            window = await OpenWorkspaceAsync();
            Assert.False(((MenuItem)Field(window, "_showArchivedDrafts")!).IsChecked);
            Assert.False(HasButton(window, label));
            Toggle();
            await HeadlessTests.WaitForAsync(() => HasButton(window, label));
            var menu = (MenuItem)Field(window, "_closedDraftsMenu")!;
            menu.IsSubMenuOpen = true;
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString()?.StartsWith(label) == true);
            Click(FindButton(window, label));
            await HeadlessTests.WaitForAsync(() => Property(window, "ActiveTab") is { } tab && Property(tab, "Editor") is TextEditor { Text: text });
            await HeadlessTests.WaitForAsync(() => !menu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString()?.StartsWith(label) == true));
            Assert.Empty(await storage.DraftArchive.GetEntriesAsync());
        }
        finally { if (window.IsVisible) await CloseAsync(window); }
    });

    [Fact]
    public Task ArchiveTitlesRespectTheLimitBeforeAndAfterRestart() => HeadlessTests.RunAsync(async profile =>
    {
        const string text = "АБ🌿Гприватное-продолжение";
        const string title = "АБ🌿Г";
        const string label = "📦 " + title + " · архив";
        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSettingsAsync(new UserSettings(UntitledTabLimit: 4));
        var saved = await storage.SaveClosedDraftAsync(new SessionTab(51, null, TextEncodingKind.Utf8)
        {
            ClosedAtMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()
        }, text);
        await storage.SaveSessionAsync(new SessionState([], [saved]));
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => HasButton(window, title));
            await CallAsync(window, "ArchiveClosedDraftsAsync");
            await HeadlessTests.WaitForAsync(() => HasButton(window, label));
            await CloseAsync(window);
            window = await OpenWorkspaceAsync();
            await HeadlessTests.WaitForAsync(() => HasButton(window, label));
            var menu = (MenuItem)Field(window, "_closedDraftsMenu")!;
            menu.IsSubMenuOpen = true;
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString()?.StartsWith(label) == true);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString()?.Contains("приватное") == true);
            menu.IsSubMenuOpen = false;
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Поиск по имени или пути");
            search.Text = "приватное";
            await HeadlessTests.WaitForAsync(() => !HasButton(window, label));
        }
        finally { if (window.IsVisible) await CloseAsync(window); }
    });

    [Fact]
    public Task MissingArchiveDisplayNameDoesNotBreakHistoryOrRestoringEitherEntry() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        const string text = "архивный текст без метаданных";
        var draft = await storage.SaveClosedDraftAsync(new SessionTab(61, null, TextEncodingKind.Utf8), text);
        var entry = await storage.DraftArchive.ArchiveAsync(draft, storage.GetDraftPath(draft)!);
        var second = await storage.SaveClosedDraftAsync(new SessionTab(62, null, TextEncodingKind.Utf8) { CustomTitle = "Соседний" }, "другой");
        var neighbor = await storage.DraftArchive.ArchiveAsync(second, storage.GetDraftPath(second)!);
        var index = new DraftArchiveIndex([entry with
        {
            DisplayName = null!,
            Tab = entry.Tab with { UntitledName = null, LastWorkedMs = long.MaxValue }
        }, neighbor]);
        await File.WriteAllBytesAsync(Path.Combine(storage.RootDirectory, "drafts-archive", "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(index, StorageJsonContext.Default.DraftArchiveIndex));
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => HasButton(window, "📦 Без имени · архив") && HasButton(window, "📦 Соседний · архив"));
            Click(FindButton(window, "📦 Без имени · архив"));
            await HeadlessTests.WaitForAsync(() => Property(window, "ActiveTab") is { } tab && Property(tab, "Editor") is TextEditor { Text: text });
            Assert.Contains(await storage.DraftArchive.GetEntriesAsync(), archived => archived.Id == neighbor.Id);
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ArchivedTitleSuffixRemainsSearchableAfterRestart(bool hasSavedTitle) => HeadlessTests.RunAsync(async profile =>
    {
        var title = new string('а', 44) + "ноябрьская заметка";
        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSettingsAsync(new UserSettings(UntitledTabLimit: 70));
        var draft = await storage.SaveClosedDraftAsync(new SessionTab(52, null, TextEncodingKind.Utf8)
        {
            UntitledName = hasSavedTitle ? title : null, ClosedAtMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()
        }, title + "\nтекст");
        await storage.SaveSessionAsync(new SessionState([], [draft]));
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => HasButton(window, title));
            await CallAsync(window, "ArchiveClosedDraftsAsync");
            await CloseAsync(window);
            window = await OpenWorkspaceAsync();
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Поиск по имени или пути");
            search.Text = "ноябрьская";
            await HeadlessTests.WaitForAsync(() => HasButton(window, $"📦 {title} · архив"));
        }
        finally { if (window.IsVisible) await CloseAsync(window); }
    });

    [Fact]
    public Task LargeArchiveDoesNotBuildMenuItemsUntilOpeningAndOlderEntriesStaySearchable() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSettingsAsync(new UserSettings { ArchivedDraftRetentionDays = 0 });
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = Enumerable.Range(1, 6000).Select(i => new DraftArchiveEntry(Guid.NewGuid().ToString("N"),
            new SessionTab(i, null, TextEncodingKind.Utf8) { CustomTitle = $"archive-{i:D4}" },
            stamp - i, 4, new string('0', 64), "unused prefix", stamp)).ToList();
        _ = storage.DraftArchive; // Creates the index directory.
        await File.WriteAllBytesAsync(Path.Combine(storage.RootDirectory, "drafts-archive", "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(new DraftArchiveIndex(entries), StorageJsonContext.Default.DraftArchiveIndex));
        var window = await OpenWorkspaceAsync();
        try
        {
            var menu = (MenuItem)Field(window, "_closedDraftsMenu")!;
            var placeholder = Assert.Single(menu.Items);
            Assert.False(menu.IsSubMenuOpen);
            Invoke(window, "RefreshHistoryMenus");
            Assert.Same(placeholder, Assert.Single(menu.Items));
            menu.IsSubMenuOpen = true;
            Assert.Equal(17, menu.Items.Count);
            Assert.Contains("archive-0001", ((MenuItem)menu.Items[0]!).Header?.ToString());
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString()?.Contains("archive-6000") == true);
            Assert.Equal("Вся история", ((MenuItem)menu.Items[^1]!).Header);
            menu.IsSubMenuOpen = false;
            Assert.Single(menu.Items);
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Поиск по имени или пути");
            search.Text = "archive-6000";
            await HeadlessTests.WaitForAsync(() => HasButton(window, "📦 archive-6000 · архив"));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ArchivedRowDeletionRequiresConfirmationAndLeavesLiveDraftAlone() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        var tab = await storage.SaveClosedDraftAsync(new SessionTab(7, null, TextEncodingKind.Utf8) { CustomTitle = "Архивный" }, "архив");
        await storage.DraftArchive.ArchiveAsync(tab, storage.GetDraftPath(tab)!);
        var live = await storage.SaveClosedDraftAsync(new SessionTab(8, null, TextEncodingKind.Utf8) { CustomTitle = "Свежий" }, "текст");
        await storage.SaveSessionAsync(new SessionState([], [live]));
        var window = await OpenWorkspaceAsync();
        try
        {
            const string remove = "Удалить из архива: 📦 Архивный · архив";
            foreach (var choice in new[] { "Отмена", "Удалить" })
            {
                await HeadlessTests.WaitForAsync(() => HasButton(window, remove));
                Click(FindButton(window, remove));
                await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
                var dialog = window.OwnedWindows.Single();
                Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, choice)));
                await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 0);
                if (choice == "Отмена") Assert.Single(await storage.DraftArchive.GetEntriesAsync());
            }
            await HeadlessTests.WaitForAsync(() => !HasButton(window, remove));
            Assert.Empty(await storage.DraftArchive.GetEntriesAsync());
            Assert.True(HasButton(window, "Свежий"));
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnreadableDraftAtStartupDoesNotReplaceCheckpointOrPruneSibling(bool hasFallback) => HeadlessTests.RunAsync(async _ =>
    {
        var storage = StorageService.OpenDefault();
        if (hasFallback)
            await storage.SaveSessionAsync(new SessionState([new(101, null, TextEncodingKind.Utf8)]),
                new Dictionary<long, string> { [101] = "previous version" });
        var committed = await storage.SaveSessionAsync(
            new SessionState([new(101, null, TextEncodingKind.Utf8), new(102, null, TextEncodingKind.Utf8)]),
            new Dictionary<long, string> { [101] = "temporarily locked", [102] = "keep this note" });
        var latest = Path.Combine(storage.RootDirectory, "session", "latest.json");
        var pointer = await File.ReadAllTextAsync(latest);
        var retryStore = StorageService.OpenDefault();
        using (var locked = new FileStream(storage.GetDraftPath(committed.Tabs[0])!, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var window = await OpenWorkspaceAsync();
            try
            {
                if (hasFallback) Assert.Equal("previous version", ActiveEditor(window)?.Text);
                await CallAsync(window, "SaveSessionAsync");
                Assert.Contains("без изменений", window.FindControl<TextBlock>("StatusText")!.Text);
                await Assert.ThrowsAsync<InvalidDataException>(() => retryStore.SaveSessionAsync(new([])));
            }
            finally { await CloseAsync(window); }
            Assert.Equal(pointer, await File.ReadAllTextAsync(latest));
            Assert.Equal("keep this note", await storage.LoadDraftAsync(committed.Tabs[1]));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => retryStore.SaveSessionAsync(new([])));
        Assert.Equal(pointer, await File.ReadAllTextAsync(latest));
        var recovered = Assert.IsType<SessionState>(await storage.LoadSessionAsync());
        Assert.Equal("temporarily locked", await storage.LoadDraftAsync(recovered.Tabs[0]));
        Assert.Equal("keep this note", await storage.LoadDraftAsync(recovered.Tabs[1]));
    });

    [Fact]
    public Task ClosingBookmarksDoesNotEvictSavedDrafts() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenDefault();
        var bookmarks = Enumerable.Range(1, 100).Select(id =>
            new SessionTab(id, Path.Combine(profile, $"bookmark-{id}.txt"), TextEncodingKind.Utf8)).ToList();
        var draft = new SessionTab(101, null, TextEncodingKind.Utf8);
        bookmarks.Add(draft);
        await storage.SaveSessionAsync(new SessionState([], bookmarks), new Dictionary<long, string> { [101] = "keep the old note" });
        var path = Path.Combine(profile, "another.txt");
        await File.WriteAllTextAsync(path, "plain text");

        var window = await OpenWorkspaceAsync();
        try
        {
            await window.ReceivePathsAsync([path], activate: false);
            await CallAsync(window, "CloseTabAsync", ActiveTab(window));
            await CallAsync(window, "SaveSessionAsync");
        }
        finally { await CloseAsync(window); }

        var restored = (await storage.LoadSessionAsync())!.ClosedTabs!;
        Assert.Equal(100, restored.Count(tab => tab.DraftRef is null));
        Assert.DoesNotContain(restored, tab => tab.Id == 100);
        Assert.Equal("keep the old note", await storage.LoadDraftAsync(Assert.Single(restored, tab => tab.Id == draft.Id)));
    });

    [Fact]
    public Task ArchiveDialogRequiresConfirmationBeforeDeletingDraft() => HeadlessTests.RunAsync(async _ =>
    {
        var storage = StorageService.OpenDefault();
        var draft = await storage.SaveClosedDraftAsync(new SessionTab(25, null, TextEncodingKind.Utf8), "archived note");
        await storage.DraftArchive.ArchiveAsync(draft, storage.GetDraftPath(draft)!);
        var window = await OpenWorkspaceAsync();
        try
        {
            var showing = CallAsync(window, "ShowDraftArchiveAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
            var archive = Assert.Single(window.OwnedWindows);
            archive.UpdateLayout();
            var delete = archive.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Удалить"));

            foreach (var choice in new[] { "Отмена", "Удалить" })
            {
                Click(delete);
                var confirm = Assert.Single(archive.OwnedWindows);
                Assert.Single(await storage.DraftArchive.GetEntriesAsync());
                confirm.UpdateLayout();
                Click(confirm.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, choice)));
            }
            await HeadlessTests.WaitForAsync(() => !archive.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Удалить")));
            Assert.Empty(await storage.DraftArchive.GetEntriesAsync());
            archive.Close();
            await showing;
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task DuplicatingAnArchiveOnlyWorkspaceCopiesItsDraft() => HeadlessTests.RunAsync(async profile =>
    {
        var catalog = new WorkspaceCatalogStore(profile);
        var source = await catalog.CreateAsync("Archived only");
        var store = StorageService.OpenWorkspace(profile, source.Id, WorkspaceCatalogStore.PrimaryWindowId);
        var draft = await store.SaveClosedDraftAsync(new SessionTab(42, null, TextEncodingKind.Utf8), "only in the archive");
        var archived = await store.DraftArchive.ArchiveAsync(draft, store.GetDraftPath(draft)!);
        Assert.False(store.HasSessionCheckpoint);

        var window = await OpenWorkspaceAsync();
        try
        {
            var manage = CallAsync(window, "ShowWorkspacesAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
            var parent = Assert.Single(window.OwnedWindows);
            var duplicate = CallAsync(window, "DuplicateWorkspaceAsync", source, parent);
            var prompt = Assert.Single(window.OwnedWindows, item => !ReferenceEquals(item, parent));
            prompt.UpdateLayout();
            Click(prompt.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "OK")));
            await duplicate;
            await manage;

            var copy = Assert.Single((await catalog.LoadAsync()).Workspaces, entry => entry.Name == "Archived only (копия)");
            var destination = StorageService.OpenWorkspace(profile, copy.Id, WorkspaceCatalogStore.PrimaryWindowId);
            var restored = Assert.Single(await destination.DraftArchive.GetEntriesAsync());
            Assert.Equal(archived.Id, restored.Id);
            Assert.Equal("only in the archive", await destination.LoadDraftAsync(await destination.DraftArchive.RestoreAsync(restored)));
            Assert.Single(await store.DraftArchive.GetEntriesAsync());
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task DuplicatingFromSecondWindowCopiesBothArchivesAndKeepsItsTabs() => HeadlessTests.RunAsync(async profile =>
    {
        var catalog = new WorkspaceCatalogStore(profile);
        var source = await catalog.CreateAsync("Two archives");
        var primaryStore = StorageService.OpenWorkspace(profile, source.Id, WorkspaceCatalogStore.PrimaryWindowId);
        var primaryOnly = await ArchiveAsync(primaryStore, 42, "primary archive");
        var shared = await ArchiveAsync(primaryStore, 43, "shared archive");
        var primary = await OpenWorkspaceAsync();
        MainWindow? second = null;
        try
        {
            await CallAsync(primary, "SwitchWorkspaceAsync", source.Id);
            Invoke(primary, "NewTab", "primary tab");
            await CallAsync(primary, "OpenWorkspaceWindowAsync", source.Id);
            second = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));
            await HeadlessTests.WaitForAsync(() => (bool)Field(second, "_restoreFinished")!);
            var secondStore = Assert.IsType<StorageService>(Property(second, "SessionStorage"));
            Assert.Empty(await secondStore.DraftArchive.GetEntriesAsync());
            var secondOnly = await ArchiveAsync(secondStore, 44, "second archive");
            await primaryStore.DraftArchive.CopyEntryToAsync(shared, secondStore.DraftArchive);
            ActiveEditor(second)!.Text = "second tab";

            var manage = CallAsync(second, "ShowWorkspacesAsync");
            await HeadlessTests.WaitForAsync(() => second.OwnedWindows.Count == 1);
            var parent = Assert.Single(second.OwnedWindows);
            var duplicate = CallAsync(second, "DuplicateWorkspaceAsync", source, parent);
            var prompt = Assert.Single(second.OwnedWindows, item => !ReferenceEquals(item, parent));
            prompt.UpdateLayout();
            Click(prompt.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "OK")));
            await duplicate;
            await manage;

            var copy = Assert.Single((await catalog.LoadAsync()).Workspaces, entry => entry.Name == "Two archives (копия)");
            foreach (var destination in new[] { StorageService.OpenWorkspace(profile, copy.Id, WorkspaceCatalogStore.PrimaryWindowId),
                         Assert.IsType<StorageService>(Property(second, "SessionStorage")) })
            {
                var checkpoint = Assert.IsType<SessionState>(await destination.LoadSessionAsync());
                Assert.Equal("second tab", await destination.LoadDraftAsync(Assert.Single(checkpoint.Tabs)));
                var archive = await destination.DraftArchive.GetEntriesAsync();
                Assert.Equal(3, archive.Count);
                foreach (var (entry, content) in new[] { (primaryOnly, "primary archive"), (shared, "shared archive"), (secondOnly, "second archive") })
                {
                    var copied = Assert.Single(archive, item => item.Id == entry.Id);
                    Assert.Equal(content, await destination.LoadDraftAsync(await destination.DraftArchive.RestoreAsync(copied)));
                }
            }
            Assert.Equal(2, (await primaryStore.DraftArchive.GetEntriesAsync()).Count);
            Assert.Equal(2, (await secondStore.DraftArchive.GetEntriesAsync()).Count);
        }
        finally
        {
            if (second is { IsVisible: true })
            {
                foreach (var dialog in second.OwnedWindows.ToArray()) dialog.Close();
                await CloseAsync(second);
            }
            await CloseAsync(primary);
        }

        static async Task<DraftArchiveEntry> ArchiveAsync(StorageService storage, long id, string content)
        {
            var draft = await storage.SaveClosedDraftAsync(new SessionTab(id, null, TextEncodingKind.Utf8), content);
            return await storage.DraftArchive.ArchiveAsync(draft, storage.GetDraftPath(draft)!);
        }
    });

    [Fact]
    public Task HistoryCanCollapseAndRemoveEntriesWithoutDeletingAnOrdinaryFile() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        var path = Path.Combine(profile, "recent.txt");
        await File.WriteAllBytesAsync(path, new byte[2048]);
        await storage.AddRecentAsync(new RecentFileEntry(path, TextEncodingKind.Utf8));
        await storage.SaveSessionAsync(new SessionState([], [new SessionTab(11, path, TextEncodingKind.Utf8)]));
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<Expander>().Count() == 3);
            var headings = window.GetVisualDescendants().OfType<Expander>().ToArray();
            Assert.Equal(new[] { "Недавние файлы", "Закрытые файлы", "Призрачные вкладки" }, headings.Select(AutomationProperties.GetName));
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "2 КиБ"));
            headings[0].IsExpanded = false;
            await CallAsync(window, "ShowHomeAsync");
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<Expander>().Count() == 3);
            var recent = window.GetVisualDescendants().OfType<Expander>().First();
            Assert.False(recent.IsExpanded);
            recent.IsExpanded = true;
            await HeadlessTests.WaitForAsync(() => HasButton(window, "Удалить из недавних: recent.txt"));
            Click(FindButton(window, "Удалить из недавних: recent.txt"));
            await HeadlessTests.WaitForAsync(() => !HasButton(window, "Удалить из недавних: recent.txt"));
            Assert.Empty(await storage.GetRecentFilesAsync());
            Click(FindButton(window, "Удалить из истории: recent.txt"));
            await HeadlessTests.WaitForAsync(() => !HasButton(window, "Удалить из истории: recent.txt"));
            Assert.Empty((await storage.LoadSessionAsync())!.ClosedTabs!);
            Assert.Equal(2048, new FileInfo(path).Length);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ConfirmedDraftDeletionReleasesItsFileAndSurvivesCheckpointRecovery() => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        var deleted = await storage.SaveClosedDraftAsync(new SessionTab(27, null, TextEncodingKind.Utf8) { UntitledName = "Удаляемый" }, new string('x', 4096));
        var kept = await storage.SaveClosedDraftAsync(new SessionTab(28, null, TextEncodingKind.Utf8) { UntitledName = "Соседний" }, "keep this draft");
        var saved = await storage.SaveSessionAsync(new SessionState([], [deleted, kept]));
        var file = storage.GetDraftPath(saved.ClosedTabs!.Single(item => item.Id == deleted.Id))!;
        var window = await OpenWorkspaceAsync();
        try
        {
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "4 КиБ"));
            Click(FindButton(window, "Удалить сохранённый черновик: Удаляемый"));
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count > 0);
            var dialog = Assert.Single(window.OwnedWindows);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Отмена")));
            Assert.True(File.Exists(file));
            var operations = (HashSet<long>)Property(Field(window, "_workspace")!, "ClosedTabOperations")!;
            await HeadlessTests.WaitForAsync(() => !operations.Contains(deleted.Id));

            Invoke(window, "ShowGroups");
            Invoke(window, "NewTab", "Несохранённый текст из последней сессии");
            await CallAsync(window, "ShowHomeAsync");
            await HeadlessTests.WaitForAsync(() => HasButton(window, "Удалить сохранённый черновик: Удаляемый"));
            Click(FindButton(window, "Удалить сохранённый черновик: Удаляемый"));
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count > 0);
            dialog = Assert.Single(window.OwnedWindows);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Удалить")));
            await HeadlessTests.WaitForAsync(() => !HasButton(window, "Удалить сохранённый черновик: Удаляемый"));
            Assert.False(File.Exists(file));
            var checkpoints = Path.Combine(storage.RootDirectory, "session");
            var current = (await File.ReadAllTextAsync(Path.Combine(checkpoints, "latest.json"))).Trim();
            Assert.Single((await storage.LoadSessionAsync())!.Tabs);
            var manifest = Path.Combine(checkpoints, current, "manifest.json");
            await File.WriteAllTextAsync(manifest, "corrupt checkpoint");
            var backup = (await storage.LoadSessionAsync())!;
            Assert.Empty(backup.Tabs);
            var recovered = Assert.Single(backup.ClosedTabs!);
            Assert.Equal(kept.Id, recovered.Id);
            Assert.Equal("keep this draft", await storage.LoadDraftAsync(recovered));

            await CloseAsync(window);
            var reopened = (await storage.LoadSessionAsync())!;
            Assert.Equal("Несохранённый текст из последней сессии", await storage.LoadDraftAsync(Assert.Single(reopened.Tabs)));
            Assert.Equal(kept.Id, Assert.Single(reopened.ClosedTabs!).Id);
        }
        finally { if (window.IsVisible) await CloseAsync(window); }
    });

    private static IEnumerable<Button> HomeButtons(MainWindow window) =>
        window.GetVisualDescendants().OfType<Button>();

    private static Button FindButton(MainWindow window, string label) =>
        Assert.Single(HomeButtons(window), button => AutomationProperties.GetName(button) == label);

    private static bool HasButton(MainWindow window, string label) =>
        HomeButtons(window).Any(button => AutomationProperties.GetName(button) == label);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static TextEditor? ActiveEditor(MainWindow window) => Property(ActiveTab(window), "Editor") as TextEditor;

    private static async Task<MainWindow> OpenWorkspaceAsync()
    {
        var window = new MainWindow();
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished")!);
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible ||
            window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Не удалось записать сессию.", StringComparison.Ordinal) == true);
        Assert.False(window.IsVisible, window.FindControl<TextBlock>("StatusText")?.Text);
    }

    private static object ActiveTab(MainWindow window) => Property(window, "ActiveTab")!;

    private static object? Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static Task CallAsync(MainWindow window, string name, params object?[] args) =>
        (Task)Invoke(window, name, args)!;

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
}
