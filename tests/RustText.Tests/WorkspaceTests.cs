using System.Reflection;
using System.Runtime.InteropServices;
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
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
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
    [Theory]
    [InlineData(1, 700, 1)]
    [InlineData(20, 700, 20)]
    [InlineData(71, 700, 71)]
    [InlineData(300, 700, 300)]
    [InlineData(300, 1800, 300)]
    [InlineData(10_000_000, 1800, 512)]
    [InlineData(0, 700, 0)]
    [InlineData(4000, 120, 57)]
    [InlineData(20, 1, 0)]
    public void TextMinimapUsesAtMostOneSamplePerDocumentLine(int lineCount, double height, int expected) =>
        Assert.Equal(expected, DocumentMinimap.GetRowCount(lineCount, height));

    [Fact]
    public Task TypingInLargeDocumentUpdatesWatermarkWithoutCopyingTheText() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", "");
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            editor.Watermark = "Введите текст";
            await HeadlessTests.WaitForAsync(() => editor.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.Name == "PART_Watermark"));
            var watermark = editor.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "PART_Watermark");
            Assert.True(watermark.IsVisible);
            Assert.Equal(editor.Watermark, watermark.Text);

            editor.Text = string.Concat(Enumerable.Repeat(new string('я', 80) + "\n", 12_000));
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.PerformTextInput("ж");
            Assert.False(watermark.IsVisible);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 4; i++) editor.TextArea.PerformTextInput("ж");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < editor.Document.TextLength * sizeof(char), $"Allocated {allocated} bytes for four edits.");
            Assert.Equal("жжжжж", editor.Document.GetText(editor.CaretOffset - 5, 5));

            editor.Clear();
            Assert.True(watermark.IsVisible);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ClosingDetachedWindowRestoresInputInRemainingWindow() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "remaining.txt");
        await File.WriteAllTextAsync(path, "original");
        var primary = await OpenWorkspaceAsync(path);
        MainWindow? detached = null;
        try
        {
            var remainingEditor = Read<TextEditor>(ActiveTab(primary), "Editor");
            Invoke(primary, "NewTab", "detached tab");
            Invoke(primary, "OpenTabWindow", ActiveTab(primary), null);
            detached = Assert.IsType<MainWindow>(primary.FindOtherWindow());

            await CloseAsync(detached);

            Assert.True(primary.IsVisible);
            Assert.True(remainingEditor.IsEnabled);
        }
        finally
        {
            if (detached is { IsVisible: true }) await CloseAsync(detached);
            if (primary.IsVisible) await CloseAsync(primary);
        }
    });

    [Fact]
    public Task ClosingWindowReleasesCachedEditorsAndLogPages() => HeadlessTests.RunAsync(async profile =>
    {
        var textPath = Path.Combine(profile, "cached.txt");
        var logPath = Path.Combine(profile, "cached.log");
        await File.WriteAllTextAsync(textPath, "editor to release");
        await File.WriteAllTextAsync(logPath, "[INFO] cached log");
        var catalog = new WorkspaceCatalogStore(profile);
        var target = await catalog.CreateAsync("Other workspace");
        var window = await OpenWorkspaceAsync(textPath);
        try
        {
            var editorTab = ActiveTab(window);
            Assert.NotNull(Property(editorTab, "FileWatch"));
            await CallAsync(window, "OpenSinglePathAsync", logPath, true, null, false);
            var logTab = ActiveTab(window);
            var log = Read<LogView>(logTab, "Log");
            await log.InitialPageLoad;
            await CallAsync(window, "SwitchWorkspaceAsync", target.Id);
            Assert.Empty(Tabs(window));
            Assert.NotNull(Property(editorTab, "Editor"));

            await CloseAsync(window);

            Assert.Null(Property(editorTab, "Editor"));
            Assert.Null(Property(editorTab, "FileWatch"));
            Assert.Empty((IEnumerable<Action>)Property(editorTab, "EditorSubscriptions")!);
            Assert.Null(Property(logTab, "Log"));
            Assert.Empty(LogText(log).Text!);
        }
        finally { if (window.IsVisible) await CloseAsync(window); }
    });

    [Fact]
    public Task ExternalReloadKeepsRecoveryDraftInTheCachedWorkspace() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "external.txt");
        await File.WriteAllTextAsync(path, "original");
        var catalog = new WorkspaceCatalogStore(profile);
        var target = await catalog.CreateAsync("Independent workspace");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var source = ActiveTab(window);
            Read<TextEditor>(source, "Editor").Document.Insert(0, "local ");
            await SwitchKeepingDraftsAsync(window, target.Id);
            await File.WriteAllTextAsync(path, "external");
            await HeadlessTests.WaitForAsync(() => Read<TextEditor>(source, "Editor").Text == "external");

            Assert.Empty(Tabs(window));
            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            var recovery = Assert.Single(Tabs(window), tab => Property(tab, "Path") is null);
            Assert.Equal("local original", Read<TextEditor>(recovery, "Editor").Text);
            Assert.Equal("external", Read<TextEditor>(source, "Editor").Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task OversizedPageRecoveryUsesItsCachedWorkspaceStorage() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "external.log");
        await File.WriteAllTextAsync(path, "original page");
        var catalog = new WorkspaceCatalogStore(profile);
        var target = await catalog.CreateAsync("Independent workspace");
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenSinglePathAsync", path, true, null, false);
            var source = ActiveTab(window);
            await Read<LogView>(source, "Log").InitialPageLoad;
            await CallAsync(window, "SwitchWorkspaceAsync", target.Id);
            var recoveredText = new string('x', 64 * 1024 + 1);
            Invoke(window, "PreservePageDraft", source, recoveredText);

            Assert.Empty(Tabs(window));
            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            var recovery = Assert.Single(Tabs(window), tab => Property(tab, "Path") is null);
            await HeadlessTests.WaitForAsync(() => Property(recovery, "Recovery") is SessionTab);
            var saved = Read<SessionTab>(recovery, "Recovery");
            Assert.Equal(recoveredText, await StorageService.OpenForTesting(profile).LoadDraftAsync(saved));
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData(false, 21d, false, 21d)]
    [InlineData(null, null, true, 15d)]
    public Task WorkspaceRestoreUsesAppTextSettingsOrMigratesTheOldSession(
        bool? wordWrap, double? fontSize, bool expectedWrap, double expectedSize) => HeadlessTests.RunAsync(async profile =>
    {
        var storage = StorageService.OpenForTesting(profile);
        await storage.SaveSettingsAsync(new UserSettings { WordWrap = wordWrap, FontSize = fontSize, Theme = "neon" });
        await storage.SaveSessionAsync(new SessionState([], Wrap: true, FontSize: 15));
        var path = Path.Combine(profile, "appearance.txt");
        await File.WriteAllTextAsync(path, "workspace text");

        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(Assert.Single(Tabs(window)), "Editor");
            Assert.Equal(expectedWrap, editor.WordWrap);
            Assert.Equal(expectedSize, editor.FontSize);
            var saved = await storage.LoadSettingsAsync();
            Assert.Equal(expectedWrap, saved.WordWrap);
            Assert.Equal(expectedSize, saved.FontSize);
            Assert.Equal("neon", saved.Theme);

            var tools = window.FindControl<Menu>("TopMenu")!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "_Инструменты"));
            var wrap = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Редактор и просмотр"))
                .Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Перенос строк"));
            Assert.Equal(MenuItemToggleType.CheckBox, wrap.ToggleType);
            Assert.Equal(expectedWrap, wrap.IsChecked);
            wrap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(!expectedWrap, wrap.IsChecked);
            Assert.Equal(!expectedWrap, editor.WordWrap);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task SavedWorkspaceCopiesTabsAndLayoutAndReusesTheLiveViewAfterSwitching() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "workspace-file.txt");
        await File.WriteAllTextAsync(path, "before");
        var window = await OpenWorkspaceAsync(path);
        MainWindow? reopened = null;
        try
        {
            var fileTab = Assert.Single(Tabs(window));
            var originalEditor = Read<TextEditor>(fileTab, "Editor");
            originalEditor.Document.Insert(originalEditor.Document.TextLength, " + edited");
            Assert.True(Read<bool>(fileTab, "Dirty"));
            Invoke(window, "SplitGroup", Assert.Single(Groups(window)), Orientation.Horizontal);
            Invoke(window, "NewTab", "editable scratch text");
            var originalDocument = originalEditor.Document;
            var originalGroupCount = Groups(window).Length;

            var create = CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Project copy");
            await KeepDraftsDialogAsync(window);
            await create;
            var createdId = (string)Field(window, "_workspaceId");
            Assert.NotEqual(WorkspaceCatalogStore.DefaultWorkspaceId, createdId);
            Assert.Equal(originalGroupCount, Groups(window).Length);
            Assert.Equal(3, Tabs(window).Count());
            foreach (var tab in Tabs(window).Where(tab => Property(tab, "Deferred") is not null).ToArray())
                await CallAsync(window, "ActivateDeferredAsync", tab);
            Assert.Contains(Tabs(window), tab => Property(tab, "Path")?.Equals(path) == true
                && Read<TextEditor>(tab, "Editor").Text == "before + edited");
            Assert.Contains(Tabs(window), tab => Property(tab, "Path") is null
                && Read<TextEditor>(tab, "Editor").Text == "editable scratch text");

            await SwitchKeepingDraftsAsync(window, WorkspaceCatalogStore.DefaultWorkspaceId);
            var restoredOldFile = Tabs(window).Single(tab => Equals(Property(tab, "Path"), path));
            Assert.Same(originalDocument, Read<TextEditor>(restoredOldFile, "Editor").Document);
            Assert.Equal(originalGroupCount, Groups(window).Length);
            await SwitchKeepingDraftsAsync(window, createdId);
            Assert.Equal(originalGroupCount, Groups(window).Length);

            await CloseAsync(window);
            reopened = await OpenWorkspaceAsync();
            Assert.Equal(createdId, Field(reopened, "_workspaceId"));
            Assert.Equal(3, Tabs(reopened).Count());
            foreach (var tab in Tabs(reopened).Where(tab => Property(tab, "Deferred") is not null).ToArray())
                await CallAsync(reopened, "ActivateDeferredAsync", tab);
            Assert.Contains(Tabs(reopened), tab => Property(tab, "Path")?.Equals(path) == true
                && Read<TextEditor>(tab, "Editor").Text == "before + edited");
            Assert.Contains(Tabs(reopened), tab => Property(tab, "Path") is null
                && Read<TextEditor>(tab, "Editor").Text == "editable scratch text");
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
            if (reopened is { IsVisible: true }) await CloseAsync(reopened);
        }
    });

    [Fact]
    public Task ClosingThirdWorkspaceCheckpointsLatestSharedDocumentToHiddenWorkspaces() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "shared-hidden.txt");
            await File.WriteAllTextAsync(path, "original");
            var window = await OpenWorkspaceAsync(path);
            try
            {
                await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Workspace A");
                var firstWorkspace = (string)Field(window, "_workspaceId");
                await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Workspace B");
                var activeWorkspace = (string)Field(window, "_workspaceId");

                var allTabs = ((System.Collections.IEnumerable)Invoke(window, "LoadedWorkspaceTabs")!).Cast<object>()
                    .Where(tab => Equals(Property(tab, "Path"), path)).ToArray();
                Assert.Equal(3, allTabs.Length);
                var activeTab = Tabs(window).Single(tab => Equals(Property(tab, "Path"), path));
                var document = Read<TextEditor>(activeTab, "Editor").Document;
                Assert.All(allTabs, tab => Assert.Same(document, Read<TextEditor>(tab, "Editor").Document));
                foreach (var tab in allTabs) Invoke(window, "SetTabAutoRefresh", tab, false);

                document.Insert(document.TextLength, " + saved");
                await CallAsync(window, "SaveEditorAsync", activeTab, Read<TextEditor>(activeTab, "Editor"), false);
                var savedStamp = Property(activeTab, "Stamp");
                document.Insert(document.TextLength, " + dirty");
                Assert.All(allTabs, tab => Assert.True(Read<bool>(tab, "Dirty")));

                await CloseAsync(window);

                foreach (var workspaceId in new[] { WorkspaceCatalogStore.DefaultWorkspaceId, firstWorkspace, activeWorkspace })
                {
                    var storage = workspaceId == WorkspaceCatalogStore.DefaultWorkspaceId
                        ? StorageService.OpenForTesting(profile)
                        : StorageService.OpenWorkspace(profile, workspaceId, WorkspaceCatalogStore.PrimaryWindowId);
                    var session = await storage.LoadSessionAsync();
                    var savedTab = Assert.Single(session!.Tabs, tab => tab.Path == path);
                    Assert.Equal(savedStamp, savedTab.Stamp);
                    Assert.NotNull(savedTab.DraftRef);
                    var draftPath = storage.GetDraftPath(savedTab);
                    Assert.NotNull(draftPath);
                    Assert.Equal("original + saved + dirty", await File.ReadAllTextAsync(draftPath!));
                }
            }
            finally
            {
                if (window.IsVisible) await CloseAsync(window);
            }
        });

    [Fact]
    public Task DeletingAWorkspaceEvictsCachedEditorsAndUnsubscribesTheirViews() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "deleted-cache.txt");
            await File.WriteAllTextAsync(path, "shared");
            var window = await OpenWorkspaceAsync(path);
            try
            {
                await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Workspace to delete");
                var deletedWorkspace = (string)Field(window, "_workspaceId");
                await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Active workspace");
                var views = (System.Collections.IDictionary)Field(window, "_workspaceViews");
                var deletedView = views[deletedWorkspace]!;
                var deletedTab = ((System.Collections.IDictionary)Property(deletedView, "Tabs")!).Values.Cast<object>()
                    .Single(tab => Equals(Property(tab, "Path"), path));
                var revision = Read<long>(deletedTab, "Revision");

                Invoke(window, "ForgetCachedWorkspaceViews", deletedWorkspace);
                Assert.False(views.Contains(deletedWorkspace));
                Assert.Null(Property(deletedTab, "Editor"));
                var activeTab = Tabs(window).Single(tab => Equals(Property(tab, "Path"), path));
                Read<TextEditor>(activeTab, "Editor").Document.Insert(0, "changed ");
                Assert.Equal(revision, Read<long>(deletedTab, "Revision"));
            }
            finally
            {
                if (window.IsVisible) await CloseAsync(window);
            }
        });

    [Fact]
    public Task ClosingTabCannotRemoveSameIdFromWorkspaceSwitchedDuringClose() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            const string text = "same id draft";
            Invoke(window, "NewTab", text);
            var original = ActiveTab(window);
            var tabId = Property(original, "Id");
            var create = CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Collision copy");
            await create;
            var copiedWorkspaceId = (string)Field(window, "_workspaceId");
            Assert.NotEqual(WorkspaceCatalogStore.DefaultWorkspaceId, copiedWorkspaceId);

            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            var current = Tabs(window).Single(tab => Equals(Property(tab, "Id"), tabId));
            var workspace = Field(window, "_workspace");
            var saveGate = (SemaphoreSlim)Property(workspace, "SaveGate",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            await saveGate.WaitAsync();
            var closing = CallAsync(window, "CloseTabAsync", current);
            try
            {
                await HeadlessTests.WaitForAsync(() => Read<bool>(current, "Closing"));
                await CallAsync(window, "SwitchWorkspaceAsync", copiedWorkspaceId);
                Assert.Equal(WorkspaceCatalogStore.DefaultWorkspaceId, Field(window, "_workspaceId"));
            }
            finally
            {
                saveGate.Release();
            }

            await closing;
            Assert.DoesNotContain(Tabs(window), tab => Equals(Property(tab, "Id"), tabId));

            await CallAsync(window, "SwitchWorkspaceAsync", copiedWorkspaceId);
            var copied = Assert.Single(Tabs(window), tab => Equals(Property(tab, "Id"), tabId));
            if (Property(copied, "Deferred") is not null)
                await CallAsync(window, "ActivateDeferredAsync", copied);
            Assert.Equal(text, Read<TextEditor>(copied, "Editor").Text);
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
        }
    });

    [Fact]
    public Task WorkspaceSwitchBlocksNewCloseAndActivatesItsSelectedDeferredTab() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "switch-pending.txt");
        await File.WriteAllTextAsync(path, "original");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var sourceTab = ActiveTab(window);
            Read<TextEditor>(sourceTab, "Editor").Document.Insert(0, "edited ");
            var create = CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Pending copy");
            await KeepDraftsDialogAsync(window);
            await create;
            var copiedWorkspaceId = (string)Field(window, "_workspaceId");

            await SwitchKeepingDraftsAsync(window, WorkspaceCatalogStore.DefaultWorkspaceId);
            sourceTab = Tabs(window).Single(tab => Equals(Property(tab, "Path"), path));
            var switching = CallAsync(window, "SwitchWorkspaceAsync", copiedWorkspaceId);
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.OfType<Window>()
                .Any(dialog => dialog.Title == "Переключить рабочее пространство?"));

            await CallAsync(window, "CloseTabAsync", sourceTab);
            Assert.False(Read<bool>(sourceTab, "Closing"));
            Assert.Contains(Tabs(window), tab => ReferenceEquals(tab, sourceTab));
            var dialog = window.OwnedWindows.OfType<Window>()
                .Single(item => item.Title == "Переключить рабочее пространство?");
            Click(dialog.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Оставить черновики")));
            await switching;

            Assert.Equal(copiedWorkspaceId, Field(window, "_workspaceId"));
            var selected = ActiveTab(window);
            Assert.Equal("edited original", Read<TextEditor>(selected, "Editor").Text);
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
        }
    });

    [Fact]
    public Task SavingFromNamedWorkspaceKeepsSharedDefaultWindowsAndCachedViewsInOneCheckpoint() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var path = Path.Combine(profile, "shared-default.txt");
            await File.WriteAllTextAsync(path, "shared base");
            var primary = await OpenWorkspaceAsync(path);
            MainWindow? detached = null;
            try
            {
                Invoke(primary, "NewTab", "detached scratch");
                Invoke(primary, "OpenTabWindow", ActiveTab(primary), null);
                detached = Read<List<MainWindow>>(Field(primary, "_workspace"), "Windows").Single(window => window != primary);
                var windowId = (string)Field(detached, "_windowId");
                await CallAsync(detached, "OpenPathsAsync", new[] { path }, false, null);
                var primaryTab = Tabs(primary).Single(tab => Equals(Property(tab, "Path"), path));
                var detachedTab = Tabs(detached).Single(tab => Equals(Property(tab, "Path"), path));
                var document = Read<TextEditor>(primaryTab, "Editor").Document;
                Assert.Same(document, Read<TextEditor>(detachedTab, "Editor").Document);
                Invoke(primary, "SetTabAutoRefresh", primaryTab, false);
                Invoke(detached, "SetTabAutoRefresh", detachedTab, false);

                await CallAsync(detached, "CreateWorkspaceFromCurrentAsync", "Named workspace");
                var namedWorkspace = (string)Field(detached, "_workspaceId");
                foreach (var tab in Tabs(detached).Where(tab => Property(tab, "Deferred") is not null).ToArray())
                    await CallAsync(detached, "ActivateDeferredAsync", tab);
                Assert.All(Tabs(detached).Where(tab => Equals(Property(tab, "Path"), path)),
                    tab => Assert.Same(document, Read<TextEditor>(tab, "Editor").Document));
                document.Insert(document.TextLength, " + latest");
                Assert.Contains(Tabs(detached), tab => Equals(Property(tab, "Path"), path) && Read<bool>(tab, "Dirty"));
                await CallAsync(detached, "SaveSessionAsync");

                var defaultStorage = StorageService.OpenForTesting(profile);
                await CallAsync(primary, "SaveSessionAsync");
                var pointer = Path.Combine(defaultStorage.RootDirectory, "session", "latest.json");
                var unchanged = await File.ReadAllTextAsync(pointer);
                await CallAsync(primary, "SaveSessionAsync");
                await CallAsync(detached, "SaveSessionAsync");
                Assert.Equal(unchanged, await File.ReadAllTextAsync(pointer));
                var sharedDefault = await defaultStorage.LoadSessionAsync();
                Assert.Equal(2, sharedDefault!.Tabs.Count(tab => tab.Path == path));
                foreach (var saved in sharedDefault.Tabs.Where(tab => tab.Path == path))
                {
                    Assert.NotNull(saved.DraftRef);
                    Assert.Equal("shared base + latest", await File.ReadAllTextAsync(defaultStorage.GetDraftPath(saved)!));
                }

                var namedStorage = StorageService.OpenWorkspace(profile, namedWorkspace, windowId);
                var namedSession = await namedStorage.LoadSessionAsync();
                Assert.Equal(2, namedSession!.Tabs.Count(tab => tab.Path == path));
                var namedDrafts = namedSession.Tabs.Where(tab => tab.Path == path && tab.DraftRef is not null).ToArray();
                Assert.NotEmpty(namedDrafts);
                foreach (var saved in namedDrafts)
                {
                    Assert.Equal("shared base + latest", await File.ReadAllTextAsync(namedStorage.GetDraftPath(saved)!));
                }
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (primary.IsVisible) await CloseAsync(primary);
            }
        });

    [Fact]
    public Task EmptyStartupLeavesTheLastSavedWorkspaceAvailable() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "kept-session.txt");
        await File.WriteAllTextAsync(path, "saved document");
        var window = await OpenWorkspaceAsync(path);
        MainWindow? empty = null;
        try
        {
            await CallAsync(window, "SaveSessionAsync");
            var storage = StorageService.OpenForTesting(profile);
            await storage.SaveSettingsAsync(new UserSettings { WorkspaceStartupMode = WorkspaceStartupMode.Empty });
            await CloseAsync(window);

            empty = await OpenWorkspaceAsync();
            Assert.Empty(Tabs(empty));
            Assert.True((bool)Field(empty, "_startEmpty"));
            Assert.Null(Field(empty, "_workspaceLoadWarning"));
            var scratch = Assert.IsType<StorageService>(Property(empty, "SessionStorage", Private));
            Assert.NotSame(storage, scratch);
            Assert.False(scratch.HasSessionCheckpoint);
            await CloseAsync(empty);

            var session = await storage.LoadSessionAsync();
            Assert.Equal(path, Assert.Single(session!.Tabs).Path);
            Assert.False(scratch.HasSessionCheckpoint);
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
            if (empty is { IsVisible: true }) await CloseAsync(empty);
        }
    });

    [Fact]
    public Task EmptyStartupHomeSwitchKeepsBothEditorsAndTheirSavedDrafts() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", "named draft");
            var named = Read<TextEditor>(ActiveTab(window), "Editor").Document;
            var namedStorage = Assert.IsType<StorageService>(Property(window, "SessionStorage", Private));
            await CallAsync(window, "EnterEmptyStartupAsync");
            Assert.Empty(Tabs(window));
            Invoke(window, "NewTab", "scratch draft");
            var scratch = Read<TextEditor>(ActiveTab(window), "Editor").Document;
            var scratchStorage = Assert.IsType<StorageService>(Property(window, "SessionStorage", Private));

            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            Assert.Same(named, Read<TextEditor>(ActiveTab(window), "Editor").Document);
            await CallAsync(window, "EnterEmptyStartupAsync");
            Assert.Same(scratch, Read<TextEditor>(ActiveTab(window), "Editor").Document);

            await CallAsync(window, "SaveSessionAsync");
            var namedTab = Assert.Single((await namedStorage.LoadSessionAsync())!.Tabs);
            var scratchTab = Assert.Single((await scratchStorage.LoadSessionAsync())!.Tabs);
            Assert.Equal("named draft", await namedStorage.LoadDraftAsync(namedTab));
            Assert.Equal("scratch draft", await scratchStorage.LoadDraftAsync(scratchTab));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task EmptyStartupKeepsNewDraftsSeparateAndSameWorkspaceSelectionRestoresSavedSession() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "previous-workspace.txt");
        await File.WriteAllTextAsync(path, "previous workspace content");
        var previous = await OpenWorkspaceAsync(path);
        MainWindow? empty = null;
        MainWindow? restartedEmpty = null;
        try
        {
            await CallAsync(previous, "SaveSessionAsync");
            var primaryStorage = StorageService.OpenForTesting(profile);
            Assert.Equal(path, Assert.Single((await primaryStorage.LoadSessionAsync())!.Tabs).Path);
            await primaryStorage.SaveSettingsAsync(new UserSettings { WorkspaceStartupMode = WorkspaceStartupMode.Empty });
            await CloseAsync(previous);

            empty = await OpenWorkspaceAsync();
            Assert.Empty(Tabs(empty));
            Assert.Null(Field(empty, "_workspaceLoadWarning"));
            var scratchStorage = Assert.IsType<StorageService>(Property(empty, "SessionStorage", Private));
            Assert.NotSame(primaryStorage, scratchStorage);

            const string newDraftText = "new draft in the empty window";
            Invoke(empty, "NewTab", newDraftText);
            await CallAsync(empty, "SaveSessionAsync");
            var scratchState = Assert.IsType<SessionState>(await scratchStorage.LoadSessionAsync());
            var savedScratchTab = Assert.Single(scratchState.Tabs);
            Assert.Equal(newDraftText, await scratchStorage.LoadDraftAsync(savedScratchTab));
            Assert.Equal(path, Assert.Single((await primaryStorage.LoadSessionAsync())!.Tabs).Path);

            await CallAsync(empty, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            Assert.False((bool)Field(empty, "_startEmpty"));
            Assert.Single(Tabs(empty));
            Assert.Equal(path, Property(ActiveTab(empty), "Path"));
            Assert.Equal("previous workspace content", Read<TextEditor>(ActiveTab(empty), "Editor").Text);
            Assert.Equal(path, Assert.Single((await primaryStorage.LoadSessionAsync())!.Tabs).Path);
            await CloseAsync(empty);

            restartedEmpty = await OpenWorkspaceAsync();
            Assert.Empty(Tabs(restartedEmpty));
            Assert.Null(Field(restartedEmpty, "_workspaceLoadWarning"));
            var closedDraft = Assert.Single(((System.Collections.IEnumerable)Field(restartedEmpty, "_closedTabs")).Cast<SessionTab>(), tab => tab.DraftRef is not null && tab.Path is null);
            Assert.Equal(newDraftText, await ((StorageService)Property(restartedEmpty, "SessionStorage", Private)!).LoadDraftAsync(closedDraft));

            var scratchPointer = Path.Combine(scratchStorage.RootDirectory, "session", "latest.json");
            var pointerBeforeNoOpClose = await File.ReadAllTextAsync(scratchPointer);
            await CloseAsync(restartedEmpty);
            restartedEmpty = await OpenWorkspaceAsync();
            Assert.Equal(pointerBeforeNoOpClose, await File.ReadAllTextAsync(scratchPointer));
            var afterNoOpRestart = Assert.Single(((System.Collections.IEnumerable)Field(restartedEmpty, "_closedTabs")).Cast<SessionTab>(), tab => tab.DraftRef is not null && tab.Path is null);
            await CallAsync(restartedEmpty, "RestoreClosedAsync", afterNoOpRestart);
            Assert.Equal(newDraftText, Read<TextEditor>(ActiveTab(restartedEmpty), "Editor").Text);
            Assert.Equal(path, Assert.Single((await primaryStorage.LoadSessionAsync())!.Tabs).Path);
        }
        finally
        {
            if (previous.IsVisible) await CloseAsync(previous);
            if (empty is { IsVisible: true }) await CloseAsync(empty);
            if (restartedEmpty is { IsVisible: true }) await CloseAsync(restartedEmpty);
        }
    });

    [Fact]
    public Task EmptyStartupPreservesMoreThanOneHundredOpenDraftsAcrossCheckpoints() => HeadlessTests.RunAsync(async profile =>
    {
        await StorageService.OpenForTesting(profile).SaveSettingsAsync(new UserSettings { WorkspaceStartupMode = WorkspaceStartupMode.Empty });
        var seedWindow = await OpenWorkspaceAsync();
        StorageService scratchStorage;
        try
        {
            scratchStorage = Assert.IsType<StorageService>(Property(seedWindow, "SessionStorage", Private));
            await CloseAsync(seedWindow);
        }
        finally
        {
            if (seedWindow.IsVisible) await CloseAsync(seedWindow);
        }

        const int count = 105;
        var draftText = Enumerable.Range(1, count).ToDictionary(id => (long)id, id => $"draft {id}");
        var tabs = draftText.Keys.Select(id => new SessionTab(id, null, TextEncodingKind.Utf8)).ToList();
        await scratchStorage.SaveSessionAsync(new SessionState(tabs), draftText);

        var empty = await OpenWorkspaceAsync();
        MainWindow? restarted = null;
        try
        {
            Assert.Empty(Tabs(empty));
            Assert.Equal(count, ((System.Collections.IEnumerable)Field(empty, "_closedTabs")).Cast<SessionTab>().Count());
            Invoke(empty, "NewTab", "new scratch draft");
            await CallAsync(empty, "SaveSessionAsync");
            var checkpoint = Assert.IsType<SessionState>(await scratchStorage.LoadSessionAsync());
            Assert.Equal("new scratch draft", await scratchStorage.LoadDraftAsync(checkpoint.Tabs.Single()));
            Assert.Equal(count, checkpoint.ClosedTabs!.Count);
            Assert.Equal("draft 1", await scratchStorage.LoadDraftAsync(checkpoint.ClosedTabs.Single(tab => tab.Id == 1)));
            await CloseAsync(empty);

            restarted = await OpenWorkspaceAsync();
            Assert.Empty(Tabs(restarted));
            Assert.Equal(count + 1, ((System.Collections.IEnumerable)Field(restarted, "_closedTabs")).Cast<SessionTab>().Count());
            var lastRetainedDraft = ((System.Collections.IEnumerable)Field(restarted, "_closedTabs")).Cast<SessionTab>().Single(tab => tab.Id == 105);
            Assert.Equal("draft 105", await scratchStorage.LoadDraftAsync(lastRetainedDraft));
        }
        finally
        {
            if (empty.IsVisible) await CloseAsync(empty);
            if (restarted is { IsVisible: true }) await CloseAsync(restarted);
        }
    });
    [Fact]
    public Task CorruptNamedWorkspaceIsReportedAndItsFilesAreNotOverwritten() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "default-session.txt");
        await File.WriteAllTextAsync(path, "default remains");
        var window = await OpenWorkspaceAsync(path);
        MainWindow? reopened = null;
        try
        {
            await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Project");
            var workspaceId = (string)Field(window, "_workspaceId");
            await CloseAsync(window);

            var workspaceStorage = StorageService.OpenWorkspace(profile, workspaceId, WorkspaceCatalogStore.PrimaryWindowId);
            var pointer = Path.Combine(workspaceStorage.RootDirectory, "session", "latest.json");
            var backupPointer = Path.Combine(workspaceStorage.RootDirectory, "session", "previous.json");
            await File.WriteAllTextAsync(pointer, "missing-checkpoint");
            await File.WriteAllTextAsync(backupPointer, "missing-backup");

            reopened = await OpenWorkspaceAsync();
            Assert.Empty(Tabs(reopened));
            Assert.Contains("не удалось прочитать", reopened.FindControl<TextBlock>("StatusText")!.Text,
                StringComparison.OrdinalIgnoreCase);
            await CloseAsync(reopened);

            Assert.Equal("missing-checkpoint", await File.ReadAllTextAsync(pointer));
            Assert.Equal("missing-backup", await File.ReadAllTextAsync(backupPointer));
            var defaultState = await StorageService.OpenForTesting(profile).LoadSessionAsync();
            Assert.Equal(path, Assert.Single(defaultState!.Tabs).Path);
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
            if (reopened is { IsVisible: true }) await CloseAsync(reopened);
        }
    });

    [Fact]
    public Task ReloadingAnInitiallyUnreadableWorkspaceAllowsAutosaveAndClose() => HeadlessTests.RunAsync(async profile =>
    {
        var workspace = await new WorkspaceCatalogStore(profile).CreateAsync("Retry workspace");
        var storage = StorageService.OpenWorkspace(profile, workspace.Id, WorkspaceCatalogStore.PrimaryWindowId);
        var snapshot = await storage.SaveSessionAsync(new([new(10, null, TextEncodingKind.Utf8)]),
            new Dictionary<long, string> { [10] = "old note" });
        var window = await OpenWorkspaceAsync();
        MainWindow? reopened = null;
        try
        {
            using (new FileStream(storage.GetDraftPath(snapshot.Tabs[0])!, FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAsync<InvalidDataException>(() => CallAsync(window, "SwitchWorkspaceAsync", workspace.Id));
            Assert.Equal(WorkspaceCatalogStore.DefaultWorkspaceId, Field(window, "_workspaceId"));

            await CallAsync(window, "SwitchWorkspaceAsync", workspace.Id);
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            Assert.Equal("old note", editor.Text);
            editor.Text = "autosaved note";
            await CallAsync(window, "SaveSessionAsync");
            var saved = Assert.IsType<SessionState>(await storage.LoadSessionAsync());
            Assert.Equal("autosaved note", await storage.LoadDraftAsync(Assert.Single(saved.Tabs)));

            editor.Text = "note saved on close";
            await CloseAsync(window);
            reopened = await OpenWorkspaceAsync();
            Assert.Equal("note saved on close", Read<TextEditor>(ActiveTab(reopened), "Editor").Text);
        }
        finally
        {
            if (window.IsVisible) await CloseAsync(window);
            if (reopened is { IsVisible: true }) await CloseAsync(reopened);
        }
    });

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
            var titleBar = window.FindControl<Border>("TitleBar")!;
            var workspace = window.FindControl<ContentControl>("WorkspaceHost")!;
            window.UpdateLayout();
            var workspaceTop = workspace.Bounds.Y;
            Assert.False(banner.IsVisible);
            var release = new UpdateRelease("2.1.0", "v2.1.0",
                new Uri("https://github.com/Illson/RustText/releases/tag/v2.1.0"),
                new Uri("https://github.com/Illson/RustText/releases/download/v2.1.0/RustText.NET-win-x64.zip"),
                new Uri("https://github.com/Illson/RustText/releases/download/v2.1.0/RustText.NET-win-x64.zip.sha256"),
                123, null, "## Исправлено\n\n- Текст из GitHub.");
            Invoke(window, "PresentUpdate", release);
            window.UpdateLayout();
            Assert.True(banner.IsVisible);
            Assert.Contains(banner, titleBar.GetVisualDescendants());
            Assert.Equal(workspaceTop, workspace.Bounds.Y);
            var caption = window.FindControl<Button>("CaptionMinimize")!;
            var noticeLeft = banner.TranslatePoint(default, titleBar)!.Value.X;
            Assert.True(noticeLeft + banner.Bounds.Width <= caption.TranslatePoint(default, titleBar)!.Value.X);
            Assert.Equal("Обновление 2.1.0", window.FindControl<TextBlock>("UpdateNoticeText")!.Text);
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
            Assert.StartsWith("first row", PageEditor(log).Text);
            Assert.False(log.IsReadOnly);
        }
        finally { await CloseAsync(window); }
    });


    [Fact]
    public Task SearchAllTabsIncludesAndOpensMatchingLogRows() => HeadlessTests.RunAsync(async profile =>
    {
        var logPath = Path.Combine(profile, "messages.log");
        var textPath = Path.Combine(profile, "notes.txt");
        await File.WriteAllTextAsync(logPath, "unrelated\nlog marker\n");
        await File.WriteAllTextAsync(textPath, "text marker");
        var window = await OpenWorkspaceAsync(logPath, textPath);
        try
        {
            var logTab = Tabs(window).Single(tab => Read<string>(tab, "Path") == logPath);
            var log = Read<LogView>(logTab, "Log");
            await log.InitialPageLoad;
            Invoke(window, "OpenSearchPanel", false);
            ((TextBox)Field(window, "_searchInput")).Text = "marker";
            ((ToggleButton)Field(window, "_allTabs")).IsChecked = true;
            await CallAsync(window, "FindAsync");
            var results = (ListBox)Field(window, "_searchResults");
            Assert.Equal("Найдено 2", ((TextBlock)Field(window, "_searchStatus")).Text);
            results.SelectedItem = Assert.Single(results.Items.OfType<ListBoxItem>(), item =>
                item.Content!.ToString()!.Contains("log marker", StringComparison.Ordinal));
            await HeadlessTests.WaitForAsync(() => ReferenceEquals(ActiveTab(window), logTab)
                && LogText(log).SelectedText == "marker");
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task SearchAllTabsClosesLevelFilterBeforeHighlightingLogHit() => HeadlessTests.RunAsync(async profile =>
    {
        var logPath = Path.Combine(profile, "filtered-search.log");
        var notesPath = Path.Combine(profile, "notes.txt");
        await File.WriteAllTextAsync(logPath, "Info marker\nError failure\n");
        await File.WriteAllTextAsync(notesPath, "search from this tab");
        var window = await OpenWorkspaceAsync(logPath, notesPath);
        try
        {
            var logTab = Tabs(window).Single(tab => Read<string>(tab, "Path") == logPath);
            var log = Read<LogView>(logTab, "Log");
            await log.InitialPageLoad;
            await log.ShowLevelFilterAsync();
            var logType = typeof(LogView);
            var panel = (LogLevelFilterPanel)logType.GetField("_levelFilter", Private)!.GetValue(log)!;
            panel.SetLevels(LogLevel.Error);
            var filtered = (TextBox)logType.GetField("_filteredLogText", Private)!.GetValue(log)!;
            await HeadlessTests.WaitForAsync(() => filtered.Text == "Error failure");
            Assert.Equal(notesPath, Read<string>(ActiveTab(window), "Path"));

            Invoke(window, "OpenSearchPanel", false);
            ((TextBox)Field(window, "_searchInput")).Text = "marker";
            ((ToggleButton)Field(window, "_allTabs")).IsChecked = true;
            await CallAsync(window, "FindAsync");
            var results = (ListBox)Field(window, "_searchResults");
            results.SelectedItem = Assert.Single(results.Items.OfType<ListBoxItem>(), item =>
                item.Content!.ToString()!.Contains("Info marker", StringComparison.Ordinal));

            var filterViewport = (Control)logType.GetField("_levelFilterViewport", Private)!.GetValue(log)!;
            await HeadlessTests.WaitForAsync(() => ReferenceEquals(ActiveTab(window), logTab)
                && LogText(log).SelectedText == "marker");
            Assert.False(filterViewport.IsVisible);
            Assert.True(LogText(log).IsEffectivelyVisible);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task SearchAllTabsReloadsChangedLogPageAtSameOffsetBeforeHighlightingHit() => HeadlessTests.RunAsync(async profile =>
    {
        var logPath = Path.Combine(profile, "same-offset.log");
        var notesPath = Path.Combine(profile, "notes.txt");
        await File.WriteAllTextAsync(logPath, "before\n");
        await File.WriteAllTextAsync(notesPath, "notes without a match");
        var window = await OpenWorkspaceAsync(logPath, notesPath);
        try
        {
            var logTab = Tabs(window).Single(tab => Read<string>(tab, "Path") == logPath);
            var log = Read<LogView>(logTab, "Log");
            await log.InitialPageLoad;
            Assert.Equal("before", LogText(log).Text);
            log.SetAutoRefresh(false);
            Assert.Equal(notesPath, Read<string>(ActiveTab(window), "Path"));

            var timestamp = File.GetLastWriteTimeUtc(logPath);
            await File.WriteAllTextAsync(logPath, "marker\n");
            File.SetLastWriteTimeUtc(logPath, timestamp);

            Invoke(window, "OpenSearchPanel", false);
            ((TextBox)Field(window, "_searchInput")).Text = "marker";
            ((ToggleButton)Field(window, "_allTabs")).IsChecked = true;
            await CallAsync(window, "FindAsync");
            var results = (ListBox)Field(window, "_searchResults");
            results.SelectedItem = Assert.Single(results.Items.OfType<ListBoxItem>(), item =>
                item.Content!.ToString()!.Contains("marker", StringComparison.Ordinal));

            await HeadlessTests.WaitForAsync(() => ReferenceEquals(ActiveTab(window), logTab)
                && LogText(log).SelectionEnd > LogText(log).SelectionStart);
            Assert.Equal("marker", LogText(log).SelectedText);
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData("1234 56", @"\d+", "$&$&", "12341234 56", 9, "56")]
    [InlineData("12 34", @"(\d)\d", "$1", "1 34", 2, "34")]
    [InlineData("abc", @"\w", "", "bc", 0, "b")]
    [InlineData("abc abc", "(?=a)", ">", ">abc abc", 5, "")]
    [InlineData("aa", "(?=a)", "", "aa", 1, "")]
    public Task SingleRegexReplaceAdvancesPastExpandedReplacement(string text, string pattern,
        string replacement, string expected, int nextOffset, string nextMatch) => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "replace.txt");
        await File.WriteAllTextAsync(path, text);
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            Invoke(window, "OpenSearchPanel", true);
            ((TextBox)Field(window, "_searchInput")).Text = pattern;
            ((TextBox)Field(window, "_replacement")).Text = replacement;
            ((ToggleButton)Field(window, "_regexOption")).IsChecked = true;
            await CallAsync(window, "FindAsync");
            await CallAsync(window, "NavigateSearchAsync", false);
            await CallAsync(window, "ReplaceAsync", false);
            Assert.Equal(expected, editor.Text);
            Assert.Equal(nextMatch, editor.SelectedText);
            Assert.Equal(nextOffset, editor.SelectionStart);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ZeroLengthReplaceAtEndWrapsToFirstOccurrence() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "anchors.txt");
        await File.WriteAllTextAsync(path, "a\nb");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            Invoke(window, "OpenSearchPanel", true);
            ((TextBox)Field(window, "_searchInput")).Text = "$";
            ((TextBox)Field(window, "_replacement")).Text = "!";
            ((ToggleButton)Field(window, "_regexOption")).IsChecked = true;
            editor.CaretOffset = editor.Document.TextLength;
            await CallAsync(window, "NavigateSearchAsync", false);
            await CallAsync(window, "ReplaceAsync", false);
            Assert.Equal("a\nb!", editor.Text);
            Assert.Equal(1, editor.CaretOffset);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ForwardSearchWrapsAfterZeroLengthMatchAtEndOfFile() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "anchors.txt");
        await File.WriteAllTextAsync(path, "a\nb");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            Invoke(window, "OpenSearchPanel", false);
            ((TextBox)Field(window, "_searchInput")).Text = "$";
            ((ToggleButton)Field(window, "_regexOption")).IsChecked = true;
            await CallAsync(window, "FindAsync");
            foreach (var offset in new[] { 1, 3, 1, 3 })
            {
                await CallAsync(window, "NavigateSearchAsync", false);
                Assert.Equal(offset, editor.CaretOffset);
                Assert.Equal(0, editor.SelectionLength);
            }
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
        TextEditor? pageEditor = null;
        string? original = null;
        try
        {
            if (editable) await CallAsync(window, "OpenSinglePathAsync", path, false, null, true);
            else await CallAsync(window, "OpenPathAsync", path, true, null);
            var log = Read<LogView>(ActiveTab(window), "Log");
            await log.InitialPageLoad;
            await (Task)typeof(LogView).GetMethod("NavigateAsync", Private)!.Invoke(log, [80L * row.Length, true])!;
            var pageText = LogText(log);
            var status = window.FindControl<TextBlock>("FormatText")!;
            if (editable)
            {
                const string previousLines = "первая\rвторая\nтретья\r\n";
                pageEditor = PageEditor(log);
                original = pageEditor.Text;
                pageEditor.Text = previousLines + "четвёртая";
                pageEditor.CaretOffset = previousLines.Length + 4;
                pageEditor.Select(previousLines.Length, 4);
                Assert.StartsWith("Строка 84 · Выделено 4 симв.", status.Text);
            }
            else
            {
                pageText.CaretIndex = row.Length + 3;
                pageText.SelectionStart = row.Length;
                pageText.SelectionEnd = row.Length + 3;
                Assert.StartsWith("Строка 82 · Выделено 3 симв.", status.Text);
            }

            if (editable) pageEditor!.TextArea.ClearSelection();
            else pageText.SelectionStart = pageText.SelectionEnd;
            Assert.Contains("Выделено 0 симв.", status.Text);
        }
        finally
        {
            if (pageEditor is not null) pageEditor.Text = original;
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
            var page = PageEditor(editor);
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

    [Theory]
    [InlineData("Сохранить")]
    [InlineData("Без сохранения")]
    [InlineData("Отмена")]
    public Task ClosingEditedLogPageRespectsChoice(string choice) => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "closing.log");
        await File.WriteAllTextAsync(path, "first row");
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", path, true, null);
            var tab = ActiveTab(window);
            var log = Read<LogView>(tab, "Log");
            await log.InitialPageLoad;
            await (Task)typeof(LogView).GetMethod("TogglePageEditAsync", Private)!.Invoke(log, null)!;
            var page = PageEditor(log);
            page.Text = "";
            await HeadlessTests.WaitForAsync(() => log.DisplayLineCount == 0);
            page.TextArea.PerformTextInput("replacement");
            Assert.Equal(1L, log.DisplayLineCount);
            var closing = CallAsync(window, "CloseTabAsync", tab);
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog => dialog.Title == "Изменения страницы"));
            Click(DialogButton(Assert.Single(window.OwnedWindows), choice));
            await closing;
            Assert.Equal(choice == "Сохранить" ? "replacement" : "first row", await File.ReadAllTextAsync(path));
            if (choice == "Отмена")
            {
                Assert.Contains(tab, Tabs(window));
                Assert.True(log.HasPendingEdit);
                Assert.Equal("replacement", page.Text);
                log.DiscardPageEdits();
            }
            else Assert.DoesNotContain(tab, Tabs(window));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task SwitchingDirtyNamedEditorPreservesTheBufferAndSource() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "unchanged.txt");
        await File.WriteAllTextAsync(path, "on disk");
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", path, false, null);
            var tab = ActiveTab(window);
            Read<TextEditor>(tab, "Editor").Text = "local changes";
            await CallAsync(window, "ToggleLogModeAsync", tab);
            var log = Read<LogView>(tab, "Log");
            await log.InitialPageLoad;
            Assert.True(log.IsReadOnly);
            Assert.Equal("on disk", await File.ReadAllTextAsync(path));
            await CallAsync(window, "ToggleLogModeAsync", tab);
            Assert.Equal("local changes", Read<TextEditor>(tab, "Editor").Text);
            Assert.True(Read<bool>(tab, "Dirty"));
            Assert.Equal(path, Property(tab, "Path"));
            Assert.Equal("on disk", await File.ReadAllTextAsync(path));
        }
        finally { await CloseAsync(window); }
    });

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
            Assert.Equal("pending edit", PageEditor(log).Text);
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

    private static TextEditor PageEditor(LogView log) => (TextEditor)typeof(LogView).GetField("_pageEditor", Private)!.GetValue(log)!;

    private static TextBox LogText(LogView log) => (TextBox)typeof(LogView).GetField("_logText", Private)!.GetValue(log)!;

    private static async Task EditLogPageAsync(LogView log, string content)
    {
        await (Task)typeof(LogView).GetMethod("TogglePageEditAsync", Private)!.Invoke(log, null)!;
        Assert.False(log.IsReadOnly);
        PageEditor(log).Text = content;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OpeningTextTabsFocusesOnlyTheSelectedEditor(bool startup) => HeadlessTests.RunAsync(async profile =>
    {
        var first = Path.Combine(profile, "first.txt");
        var last = Path.Combine(profile, "last.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(last, "last");
        var window = await OpenWorkspaceAsync(startup ? [first, last] : []);
        try
        {
            if (!startup) await window.ReceivePathsAsync([first, last], activate: false);

            var selected = ActiveTab(window);
            Assert.Equal(last, Property(selected, "Path"));
            var editor = Read<TextEditor>(selected, "Editor");
            var other = Read<TextEditor>(Tabs(window).Single(tab => Equals(Property(tab, "Path"), first)), "Editor");
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.True(editor.TextArea.IsFocused);
            Assert.Equal(0, editor.CaretOffset);
            window.KeyTextInput("!");
            Assert.Equal("!last", editor.Text);
            Assert.Equal("first", other.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ClickingOpenTabReturnsKeyboardFocusToItsSavedCaret() => HeadlessTests.RunAsync(async profile =>
    {
        var first = Path.Combine(profile, "first.txt");
        var second = Path.Combine(profile, "second.txt");
        await File.WriteAllTextAsync(first, "left right");
        await File.WriteAllTextAsync(second, "other");
        var window = await OpenWorkspaceAsync(first, second);
        try
        {
            var tab = Tabs(window).Single(tab => Equals(Property(tab, "Path"), first));
            var editor = Read<TextEditor>(tab, "Editor");
            editor.CaretOffset = 5;
            var header = TabSurface(Read<TabItem>(tab, "Item"));
            var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);

            Assert.Same(tab, ActiveTab(window));
            Assert.True(editor.TextArea.IsFocused);
            Assert.Equal(5, editor.CaretOffset);
            window.KeyTextInput("middle ");
            Assert.Equal("left middle right", editor.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CreatingNewTabLetsYouTypeWithoutClickingTheEditor(bool fromHome) => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "before-new-tab.txt");
        await File.WriteAllTextAsync(path, "previous tab");
        var window = await OpenWorkspaceAsync(fromHome ? [] : [path]);
        try
        {
            var previous = fromHome ? null : Read<TextEditor>(ActiveTab(window), "Editor");
            previous?.TextArea.Focus();
            Press(window, Key.N, RawInputModifiers.Control, PhysicalKey.N, "n");

            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            Assert.NotSame(previous, editor);
            Assert.True(editor.TextArea.IsFocused);
            window.KeyTextInput("new text");
            Assert.Equal("new text", editor.Text);
            if (previous is not null) Assert.Equal("previous tab", previous.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task RestoringTextTabFocusesAtTheSavedCaret() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "restore-position.txt");
        await File.WriteAllTextAsync(path, "start end");
        var window = await OpenWorkspaceAsync(path);
        try { Read<TextEditor>(ActiveTab(window), "Editor").CaretOffset = 6; }
        finally { await CloseAsync(window); }

        window = await OpenWorkspaceAsync();
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.True(editor.TextArea.IsFocused);
            Assert.Equal(6, editor.CaretOffset);
            window.KeyTextInput("middle ");
            Assert.Equal("start middle end", editor.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task OpeningTextTabDoesNotStealFocusFromSearchOpenedDuringLoading() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "pending.txt");
        await File.WriteAllTextAsync(path, "file");
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "OpenSearchPanel", false);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Invoke(window, "NewTab", "");
            Read<TextEditor>(ActiveTab(window), "Editor").TextArea.Focus();

            var opening = CallAsync(window, "OpenPathsAsync", new[] { path }, false, null);
            Assert.True(Read<bool>(ActiveTab(window), "Loading"));
            var input = (TextBox)Field(window, "_searchInput");
            Assert.True(input.Focus());

            await opening;
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.True(input.IsFocused);
            window.KeyTextInput("search");
            Assert.Equal("search", input.Text);
            Assert.Equal("file", Read<TextEditor>(ActiveTab(window), "Editor").Text);
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData("\n", "\n")]
    [InlineData("\r\n", "\r\n")]
    [InlineData("\r\n", "\r")]
    public Task CopyingAndPastingWholeFileKeepsTabClean(string firstEnding, string lastEnding) =>
        HeadlessTests.RunAsync(async profile =>
        {
            var firstLine = "Начало\t" + new string('x', 8192);
            var path = Path.Combine(profile, "round-trip.txt");
            await File.WriteAllTextAsync(path, $"{firstLine}{firstEnding}конец{lastEnding}");
            var window = await OpenWorkspaceAsync(path);
            try
            {
                var tab = ActiveTab(window);
                var editor = Read<TextEditor>(tab, "Editor");
                var version = editor.Document.Version;
                editor.TextArea.Focus();
                editor.SelectAll();
                Assert.Equal(editor.Document.TextLength, editor.SelectionLength);
                ApplicationCommands.Copy.Execute(null, editor.TextArea);
                Assert.Equal($"{firstLine}{Environment.NewLine}конец{Environment.NewLine}", await window.Clipboard!.TryGetTextAsync());

                Press(window, Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
                await HeadlessTests.WaitForAsync(() => editor.SelectionLength == 0);

                Assert.Equal($"{firstLine}\nконец\n", editor.Text);
                Assert.False(Read<bool>(tab, "Dirty"));
                Assert.False(editor.Document.UndoStack.CanUndo);
                Assert.Same(version, editor.Document.Version);
                Assert.Equal(editor.Document.TextLength, editor.CaretOffset);
                Assert.True(editor.TextArea.IsKeyboardFocusWithin);
            }
            finally { await CloseAsync(window); }
        });

    [Fact]
    public Task IdenticalPastePreservesEarlierChangesAndTheirUndoEntry() => HeadlessTests.RunAsync(async profile =>
    {
        var prefix = new string('x', 8192);
        var path = Path.Combine(profile, "replace.txt");
        await File.WriteAllTextAsync(path, prefix + "a");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            editor.TextArea.Focus();
            var clipboard = window.Clipboard!;
            await clipboard.SetTextAsync(prefix + "b");
            editor.SelectAll();
            Press(window, Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await HeadlessTests.WaitForAsync(() => editor.Text == prefix + "b");
            Assert.True(Read<bool>(tab, "Dirty"));

            await clipboard.SetTextAsync("b");
            editor.Select(prefix.Length, 1);
            editor.CaretOffset = prefix.Length;
            Press(window, Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await HeadlessTests.WaitForAsync(() => editor.SelectionLength == 0);
            Assert.Equal(prefix + "b", editor.Text);
            Assert.Equal(editor.Document.TextLength, editor.CaretOffset);
            Assert.True(Read<bool>(tab, "Dirty"));

            editor.Undo();
            Assert.Equal(prefix + "a", editor.Text);
            Assert.False(Read<bool>(tab, "Dirty"));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task RetypingDeletedCharacterRestoresCleanTabAndPreservesUndo() => HeadlessTests.RunAsync(async profile =>
    {
        const string original = "до Ж после";
        const int offset = 3;
        var path = Path.Combine(profile, "retype.txt");
        await File.WriteAllTextAsync(path, original);
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            editor.TextArea.Focus();
            editor.Document.Remove(offset, 1);
            Assert.True(Read<bool>(tab, "Dirty"));
            editor.CaretOffset = offset;
            window.KeyTextInput("Ж");
            Assert.Equal(original, editor.Text);
            Assert.False(Read<bool>(tab, "Dirty"));

            editor.Undo();
            Assert.Equal("до  после", editor.Text);
            Assert.True(Read<bool>(tab, "Dirty"));
            editor.Undo();
            Assert.Equal(original, editor.Text);
            Assert.False(Read<bool>(tab, "Dirty"));
            editor.Redo();
            Assert.True(Read<bool>(tab, "Dirty"));
            editor.Redo();
            Assert.False(Read<bool>(tab, "Dirty"));

            await CallAsync(window, "CloseTabAsync", tab);
            Assert.Empty(window.OwnedWindows);
            Assert.Empty(Tabs(window));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task RetypingUsesTheMostRecentlySavedText() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "saved-retype.txt");
        await File.WriteAllTextAsync(path, "old");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            editor.Document.Replace(0, 3, "new");
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal("new", await File.ReadAllTextAsync(path));
            Assert.False(Read<bool>(tab, "Dirty"));

            editor.Document.Replace(0, 3, "old");
            Assert.True(Read<bool>(tab, "Dirty"));
            editor.Document.Replace(0, 3, "new");
            Assert.False(Read<bool>(tab, "Dirty"));
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
    public Task CtrlXWithoutSelectionCutsTheCurrentLineAndUndoRestoresIt()
        => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "cut-line.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\nlast");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            editor.TextArea.Focus();
            editor.CaretOffset = 7;
            Press(window, Key.X, RawInputModifiers.Control, PhysicalKey.X, "x");
            await HeadlessTests.WaitForAsync(() => editor.Text == "alpha\nlast");

            var clipboard = await window.Clipboard!.TryGetDataAsync();
            Assert.NotNull(clipboard);
            Assert.Equal("beta" + Environment.NewLine, await clipboard.TryGetTextAsync());
            editor.Undo();
            Assert.Equal("alpha\nbeta\nlast", editor.Text);

            editor.CaretOffset = editor.Text.Length;
            Press(window, Key.X, RawInputModifiers.Control, PhysicalKey.X, "x");
            await HeadlessTests.WaitForAsync(() => editor.Text == "alpha\nbeta");
            clipboard = await window.Clipboard.TryGetDataAsync();
            Assert.NotNull(clipboard);
            Assert.Equal("last", await clipboard.TryGetTextAsync());
            editor.Undo();
            Assert.Equal("alpha\nbeta\nlast", editor.Text);

            editor.Text = "alpha\nbeta\n";
            editor.CaretOffset = editor.Text.Length;
            Press(window, Key.X, RawInputModifiers.Control, PhysicalKey.X, "x");
            Assert.Equal("alpha\nbeta\n", editor.Text);
            clipboard = await window.Clipboard.TryGetDataAsync();
            Assert.NotNull(clipboard);
            Assert.Equal("last", await clipboard.TryGetTextAsync());
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task CtrlXWithMultipleEmptyCaretsDoesNotCutOnlyThePrimaryLine()
        => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "multi-caret-cut.txt");
        await File.WriteAllTextAsync(path, "first\nsecond\nthird");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            editor.TextArea.Focus();
            editor.CaretOffset = 1;
            Press(window, Key.Down, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.ArrowDown);
            Assert.Equal(2, MultiCaretEditing.Count(editor.TextArea));
            Press(window, Key.X, RawInputModifiers.Control, PhysicalKey.X, "x");
            Assert.Equal("first\nsecond\nthird", editor.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task CtrlWheelChangesEditorFontSizeAndPlainWheelDoesNot()
        => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "zoom.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 200).Select(index => $"line {index}")));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var original = editor.FontSize;
            var point = editor.TranslatePoint(new Point(12, 12), window)!.Value;

            window.MouseWheel(point, new Vector(0, 1));
            Assert.Equal(original, editor.FontSize);
            window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(original + 1, editor.FontSize);
            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control);
            Assert.Equal(original, editor.FontSize);

            var openLog = (Task?)Invoke(window, "OpenPathAsync", path, true, null);
            Assert.NotNull(openLog);
            await openLog;
            await HeadlessTests.WaitForAsync(() => Property(ActiveTab(window), "Log") is LogView);
            var log = Assert.IsType<LogView>(Property(ActiveTab(window), "Log"));
            var logText = Assert.Single(log.GetVisualDescendants().OfType<TextBox>(), textBox => textBox.IsReadOnly);
            var logPoint = logText.TranslatePoint(new Point(12, 12), window)!.Value;
            var logOriginal = logText.FontSize;
            window.MouseWheel(logPoint, new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(logOriginal + 1, logText.FontSize);

            Invoke(window, "ResizeText", 48d);
            window.MouseWheel(logPoint, new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(48d, logText.FontSize);
            Invoke(window, "ResizeText", -48d);
            window.MouseWheel(logPoint, new Vector(0, -1), RawInputModifiers.Control);
            Assert.Equal(8d, logText.FontSize);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task FirstPointerDragSelectsText()
        => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "pointer-selection.txt");
        await File.WriteAllTextAsync(path, "first selection\nsecond line");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var view = editor.TextArea.TextView;
            view.EnsureVisualLines();
            var start = view.TranslatePoint(
                view.GetVisualPosition(new TextViewPosition(1, 1), VisualYPosition.LineMiddle) - view.ScrollOffset,
                window)!.Value;
            var end = view.TranslatePoint(
                view.GetVisualPosition(new TextViewPosition(1, 6), VisualYPosition.LineMiddle) - view.ScrollOffset,
                window)!.Value;

            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);

            Assert.Equal("first", editor.SelectedText);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task CtrlClickTogglesCaretsAndTypingEditsThemInOneUndoStep() => HeadlessTests.RunAsync(async profile =>
    {
        const string original = "aaa\nbbb\nccc";
        var path = Path.Combine(profile, "ctrl-click.txt");
        await File.WriteAllTextAsync(path, original);
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var view = editor.TextArea.TextView;
            view.EnsureVisualLines();

            Click(1, 2, RawInputModifiers.None);
            Click(3, 3, RawInputModifiers.Control);
            Click(2, 2, RawInputModifiers.Control);
            Assert.Equal(new[] { 1, 5, 10 }, editor.TextArea.Selection.Segments.Select(segment => segment.StartOffset));
            Click(2, 2, RawInputModifiers.Control);
            Assert.Equal(new[] { 1, 10 }, editor.TextArea.Selection.Segments.Select(segment => segment.StartOffset));

            window.KeyTextInput("!");
            Assert.Equal("a!aa\nbbb\ncc!c", editor.Text);
            editor.Undo();
            Assert.Equal(original, editor.Text);
            editor.Redo();
            Assert.Equal("a!aa\nbbb\ncc!c", editor.Text);

            Click(2, 3, RawInputModifiers.None);
            Assert.Equal(1, MultiCaretEditing.Count(editor.TextArea));

            void Click(int line, int column, RawInputModifiers modifiers)
            {
                var location = new TextViewPosition(line, column);
                var point = view.TranslatePoint(view.GetVisualPosition(location, VisualYPosition.LineMiddle) - view.ScrollOffset,
                    window)!.Value;
                window.MouseDown(point, MouseButton.Left, modifiers);
                window.MouseUp(point, MouseButton.Left, modifiers);
            }
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task CtrlClickAddsACaretInsideALinkAndCtrlShiftClickRequestsNavigation() => HeadlessTests.RunAsync(async profile =>
    {
        const string link = "https://example.com/";
        var path = Path.Combine(profile, "ctrl-click-link.txt");
        await File.WriteAllTextAsync(path, "first");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var area = editor.TextArea;
            var view = area.TextView;
            // Underline rendering shares Avalonia decorations across headless dispatchers.
            view.LinkTextUnderline = false;
            editor.Text = $"first\n{link}";
            view.EnsureVisualLines();
            area.Focus();
            editor.CaretOffset = 1;
            var point = view.TranslatePoint(view.GetVisualPosition(new TextViewPosition(2, 10),
                VisualYPosition.LineMiddle) - view.ScrollOffset, window)!.Value;
            Uri? opened = null;
            area.AddHandler(VisualLineLinkText.OpenUriEvent, (_, e) =>
            {
                opened = e.Uri;
                e.Handled = true;
            });

            window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
            Assert.Null(opened);
            Assert.Equal(new[] { 1, 15 }, area.Selection.Segments.Select(segment => segment.StartOffset));

            const RawInputModifiers controlShift = RawInputModifiers.Control | RawInputModifiers.Shift;
            window.MouseDown(point, MouseButton.Left, controlShift);
            window.MouseUp(point, MouseButton.Left, controlShift);
            Assert.Equal(new Uri(link), opened);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task CtrlClickAboveCaretLimitKeepsExistingSelections() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "ctrl-click-limit.txt");
        await File.WriteAllTextAsync(path, "a b\n" + string.Join('\n', Enumerable.Repeat("a", 9_999)));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var area = editor.TextArea;
            var view = area.TextView;
            view.EnsureVisualLines();
            area.Focus();
            area.Caret.Offset = 0;
            Press(window, Key.L, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.L, "L");

            Assert.Equal(MultiCaretEditing.MaxCarets, MultiCaretEditing.Count(area));
            var originalRanges = area.Selection.Segments
                .Select(segment => (segment.StartOffset, segment.EndOffset)).ToArray();
            var originalPrimary = MultiCaretEditing.Primary(area);
            var point = view.TranslatePoint(view.GetVisualPosition(new TextViewPosition(1, 3),
                VisualYPosition.LineMiddle) - view.ScrollOffset, window)!.Value;

            window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);

            Assert.Equal(MultiCaretEditing.MaxCarets, MultiCaretEditing.Count(area));
            Assert.Equal(originalRanges, area.Selection.Segments
                .Select(segment => (segment.StartOffset, segment.EndOffset)));
            Assert.Equal(originalPrimary, MultiCaretEditing.Primary(area));
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
            Assert.StartsWith("xxx", PageEditor(log).Text);
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
            var reload = CallAsync(window, "ApplyEditorReloadAsync", tab, editor, pending.Task, true);

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
            var titleBar = window.FindControl<Border>("TitleBar")!;
            var selectedBackground = SolidColor(selectedSurface.Background);
            var inactiveBackground = SolidColor(inactiveSurface.Background);
            Assert.Equal(default(Thickness), titleBar.BorderThickness);
            Assert.Equal(default(Thickness), selectedSurface.BorderThickness);
            var titleBarBottom = titleBar.TranslatePoint(new Point(0, titleBar.Bounds.Height), window)!.Value.Y;
            var tabTop = selectedSurface.TranslatePoint(default, window)!.Value.Y;
            Assert.Equal(2, tabTop - titleBarBottom);
            Assert.NotEqual(selectedBackground, inactiveBackground);
            var hoverPoint = inactiveSurface.TranslatePoint(
                new Point(inactiveSurface.Bounds.Width / 2, inactiveSurface.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("Inactive tab surface is not attached.");

            window.MouseMove(hoverPoint);
            await Task.Delay(30);

            Assert.NotEqual(inactiveBackground, SolidColor(inactiveSurface.Background));
            Assert.Equal(selectedBackground, SolidColor(selectedSurface.Background));
            Assert.Same(selected, tabs.SelectedItem);
        }
        finally
        {
            await CloseAsync(window);
        }
    });
    [Fact]
    public Task UnsavedTabShowsGreenMarkAlignedWithHeaderAndCloseIcon() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", "");
            var tab = ActiveTab(window);
            var label = Read<TextBlock>(tab, "Label");
            var count = Read<TextBlock>(tab, "LineCountLabel");
            var mark = Read<Avalonia.Controls.Shapes.Ellipse>(tab, "DirtyMark");
            var header = Assert.IsType<Grid>(Read<TabItem>(tab, "Item").Header);
            var close = Assert.Single(header.Children.OfType<Button>());
            var icon = Assert.IsType<Avalonia.Controls.Shapes.Path>(close.Content);

            Assert.Equal(0, mark.Opacity);
            var editor = Read<TextEditor>(tab, "Editor");
            editor.Document.Insert(0, "first line\nsecond line");
            window.UpdateLayout();

            Assert.True(count.IsVisible);
            Assert.Equal("2 стр.", count.Text);
            Assert.Equal(1, mark.Opacity);
            var green = SolidColor(mark.Fill);
            Assert.True(green.G > green.R && green.G > green.B);
            Assert.True(icon.Bounds.Height > 0);
            var iconCenter = CenterY(icon);
            Assert.All(new Control[] { label, count, mark }, control =>
                Assert.InRange(Math.Abs(CenterY(control) - iconCenter), 0, 1));

            editor.Document.UndoStack.Undo();
            Assert.Equal(0, mark.Opacity);

            double CenterY(Control control) => control.TranslatePoint(new Point(0, control.Bounds.Height / 2), window)!.Value.Y;
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
            var reload = CallAsync(window, "ApplyEditorReloadAsync", tab, editor, pending.Task, true);
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

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(0, 1)]
    public Task CtrlWClosesActiveTabAndSelectsNeighbor(int closedIndex, int nextIndex) =>
        HeadlessTests.RunAsync(async _ =>
        {
            var window = await OpenWorkspaceAsync();
            try
            {
                for (var index = 0; index < 4; index++)
                    Invoke(window, "NewTab", "");

                var tabs = Read<WorkspaceTabs>(Field(window, "_active"), "Tabs");
                var original = tabs.Items.OfType<TabItem>().ToArray();
                var closed = original[closedIndex];
                tabs.SelectedItem = closed;
                Read<TextEditor>(ActiveTab(window), "Editor").TextArea.Focus();

                Press(window, Key.W, RawInputModifiers.Control, PhysicalKey.W, "w");

                await HeadlessTests.WaitForAsync(() => !tabs.Items.Contains(closed));
                Assert.Equal(3, tabs.Items.Count);
                Assert.Same(original[nextIndex], tabs.SelectedItem);
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
            closedMenu.IsSubMenuOpen = true;
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
    public Task ClosingLargeUntitledTabReleasesEditorMemoryAndKeepsItsSavedDraftRecoverable() => HeadlessTests.RunAsync(async _ =>
    {
        var text = "закрытый черновик 💡\n" + string.Concat(Enumerable.Repeat("строка со значимым содержимым\n", 20_000));
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", text);
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            Assert.Equal(text, editor.Text);

            await CallAsync(window, "CloseTabAsync", tab);

            Assert.Null(Property(tab, "Editor"));
            Assert.Equal(0, editor.Document.TextLength);
            var closed = Assert.Single(((System.Collections.IEnumerable)Field(window, "_closedTabs")).Cast<SessionTab>());
            Assert.NotNull(closed.DraftRef);
            Assert.Equal(text, await ((StorageService)Field(window, "_storage")!).LoadDraftAsync(closed));

            await CallAsync(window, "RestoreClosedAsync", closed);
            Assert.Equal(text, Read<TextEditor>(ActiveTab(window), "Editor").Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ClosedDraftDocumentAndUndoBufferCanBeGarbageCollected() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", "keep this one open");
            var (tab, document) = CreateCollectibleDraft(window);
            await CallAsync(window, "CloseTabAsync", tab);
            Assert.Single((List<SessionTab>)Field(window, "_closedTabs"));
            await HeadlessTests.WaitForAsync(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                return !document.IsAlive;
            });
        }
        finally { await CloseAsync(window); }
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (object Tab, WeakReference Document) CreateCollectibleDraft(MainWindow window)
    {
        Invoke(window, "NewTab", string.Concat(Enumerable.Repeat("a bounded log message\n", 6000)));
        var tab = ActiveTab(window);
        var document = Read<TextEditor>(tab, "Editor").Document;
        document.Insert(5, string.Concat(Enumerable.Repeat("more text\n", 5000)));
        return (tab, new WeakReference(document));
    }

    [Fact]
    public Task ClosedDraftsPastTheConfiguredAgeArchiveWhileOpenDraftsStayLive() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", "closed text to archive");
            var closedTab = ActiveTab(window);
            await CallAsync(window, "CloseTabAsync", closedTab);
            var closed = Assert.Single(((System.Collections.IEnumerable)Field(window, "_closedTabs")).Cast<SessionTab>());
            var old = closed with { ClosedAtMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds() };
            ((List<SessionTab>)Field(window, "_closedTabs")!)[0] = old;

            Invoke(window, "NewTab", "leave this tab open");
            Func<UserSettings, UserSettings> update = settings => settings with
            {
                ArchiveClosedDraftAfterMinutes = 1,
                ArchivedDraftRetentionDays = 0
            };
            await CallAsync(window, "SaveWorkspaceSettingsAsync", update);
            await CallAsync(window, "HandleIdleUnloadTimerTickAsync");

            Assert.Empty((List<SessionTab>)Field(window, "_closedTabs")!);
            Assert.Equal("leave this tab open", Read<TextEditor>(ActiveTab(window), "Editor").Text);
            var archive = new DraftArchiveService(((StorageService)Field(window, "_storage")!).RootDirectory);
            Assert.Equal(old.Id, Assert.Single(await archive.GetEntriesAsync()).Tab.Id);
            Assert.False(File.Exists(((StorageService)Field(window, "_storage")!).GetDraftPath(old)));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task MissingFileInNamedWorkspaceCanBeRetriedWithoutDisturbingOtherTabs() => HeadlessTests.RunAsync(async profile =>
    {
        var missing = Path.Combine(profile, "missing.txt");
        var intact = Path.Combine(profile, "intact.txt");
        await File.WriteAllTextAsync(intact, "unchanged");
        await File.WriteAllTextAsync(missing, "original");
        var window = await OpenWorkspaceAsync(intact, missing);
        try
        {
            Invoke(window, "Select", Tabs(window).Single(tab => Equals(Property(tab, "Path"), missing)));
            await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Investigation");
            var id = (string)Field(window, "_workspaceId");
            await CloseAsync(window);
            File.Delete(missing);
            window = await OpenWorkspaceAsync();
            Assert.Equal(id, Field(window, "_workspaceId"));
            var pending = Tabs(window).Single(tab => Equals(Property(tab, "Path"), missing));
            Assert.Contains(missing, Read<string>(pending, "DiskStatus"));
            var placeholder = Assert.IsType<StackPanel>(Read<TabItem>(pending, "Item").Content);
            Assert.Contains(missing, placeholder.Children.OfType<TextBlock>().Single().Text);
            var valid = Tabs(window).Single(tab => Equals(Property(tab, "Path"), intact));
            await CallAsync(window, "ActivateDeferredAsync", valid);
            Assert.Equal("unchanged", Read<TextEditor>(valid, "Editor").Text);
            await File.WriteAllTextAsync(missing, "restored file");
            Click(placeholder.Children.OfType<Button>().Single());
            await HeadlessTests.WaitForAsync(() => Property(pending, "Editor") is TextEditor editor && editor.Text == "restored file");
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task DeleteConfirmationRechecksWhetherAnotherWindowOpenedTheWorkspace() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        MainWindow? second = null;
        try
        {
            Invoke(window, "NewTab", "keep this workspace");
            await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Shared");
            var catalog = new WorkspaceCatalogStore(profile);
            var entry = (await catalog.LoadAsync()).Workspaces.Single(item => item.Name == "Shared");
            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            var deleting = CallAsync(window, "DeleteWorkspaceAsync", entry, window);
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any());
            var confirmation = Assert.Single(window.OwnedWindows);
            await CallAsync(window, "OpenWorkspaceWindowAsync", entry.Id);
            second = Read<List<MainWindow>>(Field(window, "_workspace"), "Windows").Single(other => other != window);
            await HeadlessTests.WaitForAsync(() => (bool)Field(second, "_restoreFinished"));
            Click(confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Удалить")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => deleting);

            Assert.Contains((await catalog.LoadAsync()).Workspaces, item => item.Id == entry.Id);
            Assert.Equal("keep this workspace", Read<TextEditor>(ActiveTab(second), "Editor").Text);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            if (second is { IsVisible: true }) await CloseAsync(second);
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task SettingsOpensTheSelectedNamedWorkspaceOnRestart() => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", "project notes");
            await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Logs");
            var expected = (string)Field(window, "_workspaceId");
            await CallAsync(window, "SwitchWorkspaceAsync", WorkspaceCatalogStore.DefaultWorkspaceId);
            var show = CallAsync(window, "ShowSettingsAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any());
            var dialog = Assert.Single(window.OwnedWindows);
            await SelectSettingsSectionAsync(dialog, "SettingsSectionWorkspaces");
            var mode = DialogControl<ComboBox>(dialog, "SettingsWorkspaceStartupMode");
            mode.SelectedIndex = 1;
            var spaces = DialogControl<ComboBox>(dialog, "SettingsStartupWorkspace");
            Assert.True(spaces.IsEnabled);
            spaces.SelectedItem = spaces.Items.OfType<WorkspaceEntry>().Single(entry => entry.Name == "Logs");
            await HeadlessTests.WaitForAsync(() => spaces.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Logs"));
            Click(DialogControl<Button>(dialog, "SettingsSave"));
            await show;
            var persisted = await StorageService.OpenForTesting(profile).LoadSettingsAsync();
            Assert.Equal(WorkspaceStartupMode.Specific, persisted.WorkspaceStartupMode);
            Assert.Equal(expected, persisted.StartupWorkspaceId);
            await CloseAsync(window);
            window = await OpenWorkspaceAsync();
            Assert.Equal(expected, Field(window, "_workspaceId"));
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosedDraftArchiveFollowsItsWorkspaceAcrossSwitchAndRestore(bool preventCheckpoint) => HeadlessTests.RunAsync(async profile =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", "a live draft");
            Invoke(window, "NewTab", "copy of a closed draft");
            var tab = ActiveTab(window);
            Read<TextEditor>(tab, "Editor").Select(2, 4);
            await CallAsync(window, "CloseTabAsync", tab);
            var closed = (List<SessionTab>)Field(window, "_closedTabs");
            closed[0] = Assert.Single(closed) with
            {
                ClosedAtMs = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds(),
                CustomTitle = "Review notes"
            };
            await CallAsync(window, "CreateWorkspaceFromCurrentAsync", "Project");
            var defaultStore = StorageService.OpenForTesting(profile);
            var namedStore = StorageService.OpenWorkspace(profile, (string)Field(window, "_workspaceId"), WorkspaceCatalogStore.PrimaryWindowId);
            Func<UserSettings, UserSettings> update = settings => settings with { ArchiveClosedDraftAfterMinutes = 1 };
            await CallAsync(window, "SaveWorkspaceSettingsAsync", update);
            await CallAsync(window, "HandleIdleUnloadTimerTickAsync");

            Assert.Single(await defaultStore.DraftArchive.GetEntriesAsync());
            Assert.Single(await namedStore.DraftArchive.GetEntriesAsync());
            Assert.Empty((await defaultStore.LoadSessionAsync())!.ClosedTabs!);
            Assert.Empty((await namedStore.LoadSessionAsync())!.ClosedTabs!);
            var show = CallAsync(window, "ShowDraftArchiveAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
            var dialog = Assert.Single(window.OwnedWindows);
            await HeadlessTests.WaitForAsync(() => dialog.GetVisualDescendants().OfType<TextBlock>().Any(b => b.Text == "Review notes"));
            var unavailable = Read<HashSet<string>>(Field(window, "_workspace"), "UnavailableSessions");
            var key = (string)Property(window, "WorkspaceCacheKey", Private)!;
            if (preventCheckpoint) unavailable.Add(key);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Восстановить")));
            await HeadlessTests.WaitForAsync(() => preventCheckpoint
                ? window.FindControl<TextBlock>("StatusText")?.Text?.Contains("Архивная копия сохранена") == true
                : dialog.GetVisualDescendants().OfType<TextBlock>().Any(b => b.Text == "Архив пуст."));
            unavailable.Remove(key);
            dialog.Close();
            await show;

            var restored = Read<TextEditor>(ActiveTab(window), "Editor");
            Assert.Equal("copy of a closed draft", restored.Text);
            Assert.Equal(2, restored.SelectionStart);
            Assert.Equal(4, restored.SelectionLength);
            Assert.Equal(preventCheckpoint ? 1 : 0, (await namedStore.DraftArchive.GetEntriesAsync()).Count);
            Assert.Single(await defaultStore.DraftArchive.GetEntriesAsync());
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task ClosingLogReleasesItsPageAndSearchReferencesAndCanRestoreIt() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "close-log.log");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("log row with payload\n", 300)));
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenSinglePathAsync", path, true, null, false);
            var tab = ActiveTab(window);
            var log = Read<LogView>(tab, "Log");
            await log.InitialPageLoad;
            Assert.Contains("log row", LogText(log).Text);
            typeof(MainWindow).GetField("_searchTab", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, tab);

            await CallAsync(window, "CloseTabAsync", tab);

            Assert.Null(Property(tab, "Log"));
            Assert.Null(Property(Property(tab, "Item")!, "Content"));
            Assert.Null(typeof(LogView).GetField("_page", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log));
            Assert.Equal(string.Empty, LogText(log).Text);
            Assert.Null(Field(window, "_searchTab"));
            var closed = Assert.Single(((System.Collections.IEnumerable)Field(window, "_closedTabs")).Cast<SessionTab>());

            await CallAsync(window, "RestoreClosedAsync", closed);
            var restored = Read<LogView>(ActiveTab(window), "Log");
            await restored.InitialPageLoad;
            Assert.Contains("log row", LogText(restored).Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ClosingEditorRemovesItsSearchResultsAndSourceReferences() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "search-close.txt");
        await File.WriteAllTextAsync(path, "needle in the document");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            ((TextBox)Field(window, "_searchInput")).Text = "needle";
            await CallAsync(window, "FindAsync");
            var results = (ListBox)Field(window, "_searchResults");
            Assert.Single(results.Items);
            var tab = ActiveTab(window);

            await CallAsync(window, "CloseTabAsync", tab);

            Assert.Empty(results.Items);
            Assert.Null(Field(window, "_searchTab"));
            Assert.Null(Field(window, "_searchFocus"));
            Assert.Null(Property(Property(tab, "Item")!, "Content"));
        }
        finally { await CloseAsync(window); }
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
    public Task EditorWrapKeepsLongRowsInTheViewportBesideMinimap() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "long-line.txt");
        var line = new string('x', 512) + "-end";
        await File.WriteAllTextAsync(path, line);
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var viewport = editor.GetVisualParent()!;
            var minimap = Assert.Single(viewport.GetVisualDescendants().OfType<DocumentMinimap>());
            var minimapDock = Assert.IsType<Grid>(minimap.GetVisualParent());
            var verticalScrollBar = Assert.Single(minimapDock.GetVisualChildren().OfType<ViewportScrollBar>());
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            var view = editor.TextArea.TextView;
            await HeadlessTests.WaitForAsync(() => scroll.Viewport.Width > 0
                && view.VisualLinesValid && view.VisualLines.Count > 0);
            Assert.True(editor.WordWrap);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            Assert.InRange(scroll.Offset.X, 0, 1);

            bool LineWraps() => view.GetVisualPosition(new TextViewPosition(1, line.Length + 1), VisualYPosition.LineMiddle).Y
                > view.GetVisualPosition(new TextViewPosition(1, 1), VisualYPosition.LineMiddle).Y;
            Assert.True(LineWraps());
            AssertViewportControlsDoNotOverlap(editor, minimap, verticalScrollBar, viewport, window);

            Invoke(window, "ToggleWrap");
            await HeadlessTests.WaitForAsync(() => scroll.Extent.Width > scroll.Viewport.Width && !LineWraps());
            var horizontalMaximum = scroll.Extent.Width - scroll.Viewport.Width;
            scroll.Offset = new Vector(horizontalMaximum, scroll.Offset.Y);
            Assert.Equal(horizontalMaximum, scroll.Offset.X, precision: 1);
            AssertViewportControlsDoNotOverlap(editor, minimap, verticalScrollBar, viewport, window);

            Invoke(window, "ToggleWrap");
            await HeadlessTests.WaitForAsync(() => scroll.Extent.Width <= scroll.Viewport.Width + 1 && LineWraps());
            Assert.InRange(scroll.Offset.X, 0, 1);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task TextMinimapShowsTheViewportOutlineWhenAllTextFits() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "short.txt");
        await File.WriteAllTextAsync(path, "a single line");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var minimap = Assert.Single(window.GetVisualDescendants().OfType<DocumentMinimap>());
            var scroll = Assert.Single(editor.GetVisualDescendants().OfType<ScrollViewer>());
            await HeadlessTests.WaitForAsync(() => minimap.Bounds.Height > 100 && scroll.Viewport.Height > 0
                && scroll.ScrollBarMaximum.Y == 0);

            var size = new PixelSize((int)minimap.Bounds.Width, (int)minimap.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            using var pixels = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var frame = pixels.Lock();
            bitmap.Render(minimap);
            bitmap.CopyPixels(frame);
            var edge = frame.Address + 5 * frame.RowBytes;
            var outline = Color.FromRgb(Marshal.ReadByte(edge, 2), Marshal.ReadByte(edge, 1), Marshal.ReadByte(edge));
            Assert.Equal(FocusTheme.Accent.Color, outline);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task TextMinimapRendersCompactJsonWithIndentationAndNavigatesWithinPreview() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "minimap.json");
        var lines = new[] { "{", "  \"Defaults\": {", "    \"OrderId\": 123456", "  },", "  \"Overrides\": [" }
            .Concat(Enumerable.Repeat("    { \"Channel\": \"Example\", \"RegionViews\": \"Mix\" },", 63))
            .Concat(["    { \"Channel\": \"Last\", \"RegionViews\": \"Mix\" }", "  ]", "}"]);
        await File.WriteAllTextAsync(path, string.Join('\n', lines));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var minimap = Assert.Single(window.GetVisualDescendants().OfType<DocumentMinimap>());
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.Equal(71, editor.Document.LineCount);
            await HeadlessTests.WaitForAsync(() => minimap.Bounds.Height > 200 && scroll.Extent.Height > scroll.Viewport.Height);
            var size = new PixelSize((int)minimap.Bounds.Width, (int)minimap.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            using var pixels = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var frame = pixels.Lock();

            Capture();
            var topMarker = ReadPixel(70, 5);
            var belowPreview = ReadPixel(70, 160);
            Assert.NotEqual(belowPreview, topMarker);
            Assert.Equal(topMarker, ReadPixel(3, 5)); // Indent on the second line.
            Assert.NotEqual(topMarker, ReadPixel(5, 5));
            Assert.Equal(topMarker, ReadPixel(17, 7)); // Space after the colon.
            Assert.NotEqual(topMarker, ReadPixel(18, 7));
            Assert.NotEqual(ReadPixel(4, 143), ReadPixel(3, 143)); // Closing brace on line 71.
            Assert.Equal(FocusTheme.Editor.Color, ReadPixel(22, 160));

            var maximum = scroll.Extent.Height - scroll.Viewport.Height;
            var blank = minimap.TranslatePoint(new Point(20, 160), window)!.Value;
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            Assert.InRange(scroll.Offset.Y, 0, 1);

            var bottom = minimap.TranslatePoint(new Point(20, 145), window)!.Value;
            window.MouseDown(bottom, MouseButton.Left);
            window.MouseUp(bottom, MouseButton.Left);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y >= maximum - 1);
            Capture();
            Assert.Equal(belowPreview, ReadPixel(70, 5));
            Assert.Equal(topMarker, ReadPixel(70, 143));

            editor.Document.Replace(editor.Document.TextLength - 1, 1, " ");
            Capture();
            Assert.Equal(ReadPixel(4, 143), ReadPixel(3, 143));

            void Capture()
            {
                bitmap.Render(minimap);
                bitmap.CopyPixels(frame);
            }

            Color ReadPixel(int x, int y)
            {
                var address = frame.Address + y * frame.RowBytes + x * 4;
                return Color.FromRgb(Marshal.ReadByte(address, 2), Marshal.ReadByte(address, 1), Marshal.ReadByte(address, 0));
            }
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        }
    });

    [Fact]
    public Task TextMinimapKeepsTheViewportOutlineOnTheWrappedPreview() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "wrapped-preview.txt");
        var lines = Enumerable.Range(1, 101).Select(i => i % 4 == 0 ? new string('x', 230) : $"row {i}");
        await File.WriteAllTextAsync(path, string.Join('\n', lines));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var minimap = Assert.Single(window.GetVisualDescendants().OfType<DocumentMinimap>());
            var scroll = Assert.Single(editor.GetVisualDescendants().OfType<ScrollViewer>());
            await HeadlessTests.WaitForAsync(() => minimap.Bounds.Height > 450 && scroll.ScrollBarMaximum.Y > 0);
            scroll.Offset = new Vector(0, scroll.ScrollBarMaximum.Y);
            window.UpdateLayout();

            var view = editor.TextArea.TextView;
            var previewEnd = 3 + (int)(2 * Math.Round(view.DocumentHeight / view.DefaultLineHeight));
            Assert.InRange(previewEnd, 210, (int)minimap.Bounds.Height - 10);
            var size = new PixelSize((int)minimap.Bounds.Width, (int)minimap.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            using var pixels = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var frame = pixels.Lock();
            bitmap.Render(minimap);
            bitmap.CopyPixels(frame);

            Assert.NotEqual(FocusTheme.Editor.Color, ReadPixel(0, previewEnd - 6));
            Assert.NotEqual(ReadPixel(65, previewEnd - 2), ReadPixel(3, previewEnd - 2));
            Assert.Equal(FocusTheme.Editor.Color, ReadPixel(0, previewEnd + 2));

            Color ReadPixel(int x, int y)
            {
                var address = frame.Address + y * frame.RowBytes + x * 4;
                return Color.FromRgb(Marshal.ReadByte(address, 2), Marshal.ReadByte(address, 1), Marshal.ReadByte(address, 0));
            }
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task TextMinimapUsesVisibleRowsWhenFoldingOffsetsWrapping() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "folded-wrap.json");
        await File.WriteAllTextAsync(path, "[\n [\n      1,\n      2,\n      3\n ],\ntrue,\n\"x\"\n]");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var view = editor.TextArea.TextView;
            var minimap = Assert.Single(window.GetVisualDescendants().OfType<DocumentMinimap>());
            await HeadlessTests.WaitForAsync(() => view.Bounds.Width > 200
                && view.GetService(typeof(FoldingManager)) is FoldingManager m && m.AllFoldings.Count() == 2);
            var manager = (FoldingManager)view.GetService(typeof(FoldingManager))!;
            manager.AllFoldings.Last().IsFolded = true;
            var line = editor.Document.GetLineByNumber(8);
            editor.Document.Replace(line.Offset + 1, 1, new string('x', (int)(4.3 * view.Bounds.Width / view.WideSpaceWidth)));
            window.UpdateLayout();
            Assert.Equal(editor.Document.LineCount, (int)Math.Round(view.DocumentHeight / view.DefaultLineHeight));
            Assert.Equal(7, view.GetDocumentLineByVisualTop(2.5 * view.DefaultLineHeight).LineNumber);

            var size = new PixelSize((int)minimap.Bounds.Width, (int)minimap.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            using var pixels = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var frame = pixels.Lock();
            bitmap.Render(minimap);
            bitmap.CopyPixels(frame);
            var thirdRow = frame.Address + 7 * frame.RowBytes;
            // The visible row starts with "true,", not the six spaces of the folded line.
            Assert.NotEqual(Marshal.ReadInt32(thirdRow, 50 * 4), Marshal.ReadInt32(thirdRow, 3 * 4));
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task TextMinimapNavigatesWithinWrappedSingleLine() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "wrapped-line.txt");
        var line = string.Concat(Enumerable.Repeat("word ", 2_000));
        await File.WriteAllTextAsync(path, line);
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var minimap = Assert.Single(window.GetVisualDescendants().OfType<DocumentMinimap>());
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            var view = editor.TextArea.TextView;
            await HeadlessTests.WaitForAsync(() => view.VisualLinesValid
                && scroll.Extent.Height > scroll.Viewport.Height * 2);

            var navigationHeight = minimap.PreviewHeight;
            Assert.True(navigationHeight > 100);
            Assert.True(view.GetVisualPosition(new TextViewPosition(1, line.Length + 1), VisualYPosition.LineMiddle).Y
                > view.GetVisualPosition(new TextViewPosition(1, 1), VisualYPosition.LineMiddle).Y);

            JumpTo(.25);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y / (scroll.Extent.Height - scroll.Viewport.Height)
                is >= .23 and <= .27);

            JumpTo(.75);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y / (scroll.Extent.Height - scroll.Viewport.Height)
                is >= .73 and <= .77);

            void JumpTo(double fraction)
            {
                var point = minimap.TranslatePoint(new Point(12, 3 + navigationHeight * fraction), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task TextMinimapNavigationStaysStableWhenLaterLineWraps() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "wrapped-last-line.txt");
        var longLine = string.Concat(Enumerable.Repeat("word ", 2_000));
        var lines = Enumerable.Repeat("short line", 40).Append(longLine);
        await File.WriteAllTextAsync(path, string.Join('\n', lines));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var minimap = Assert.Single(window.GetVisualDescendants().OfType<DocumentMinimap>());
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            var view = editor.TextArea.TextView;
            await HeadlessTests.WaitForAsync(() => view.VisualLinesValid
                && scroll.Extent.Height > scroll.Viewport.Height * 2);

            JumpTo(.75);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y > 0);

            scroll.Offset = new Vector(scroll.Offset.X, scroll.ScrollBarMaximum.Y);
            await HeadlessTests.WaitForAsync(() => Math.Abs(scroll.Offset.Y - scroll.ScrollBarMaximum.Y) < 1);

            scroll.Offset = default;
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y < 1);
            JumpTo(.75);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y / (scroll.Extent.Height - scroll.Viewport.Height)
                is >= .73 and <= .77);

            JumpTo(.25);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y / (scroll.Extent.Height - scroll.Viewport.Height)
                is >= .23 and <= .27);

            void JumpTo(double fraction)
            {
                window.UpdateLayout();
                var point = minimap.TranslatePoint(new Point(12, 3 + minimap.PreviewHeight * fraction), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task TextMinimapNavigatesWhenLastLineWrapsToTwoRows() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "two-row-wrap.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Repeat("short line", 40).Append("x")));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var minimap = Assert.Single(window.GetVisualDescendants().OfType<DocumentMinimap>());
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            var view = editor.TextArea.TextView;
            await HeadlessTests.WaitForAsync(() => view.Bounds.Width > 100 && scroll.Extent.Height > scroll.Viewport.Height);

            var lineLength = (int)(view.Bounds.Width / view.WideSpaceWidth * 1.2);
            var lastLine = editor.Document.GetLineByNumber(editor.Document.LineCount);
            editor.Document.Replace(lastLine.Offset, lastLine.Length, new string('W', lineLength));
            await HeadlessTests.WaitForAsync(() =>
            {
                var firstY = view.GetVisualPosition(new TextViewPosition(lastLine.LineNumber, 1), VisualYPosition.LineMiddle).Y;
                var lastY = view.GetVisualPosition(new TextViewPosition(lastLine.LineNumber, lineLength + 1), VisualYPosition.LineMiddle).Y;
                return view.VisualLinesValid && lastY > firstY && lastY - firstY < 1.8 * view.DefaultLineHeight;
            });

            var maximum = scroll.Extent.Height - scroll.Viewport.Height;
            Assert.True(maximum > 0);
            var point = minimap.TranslatePoint(new Point(12, 3 + minimap.PreviewHeight * .75), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y / (scroll.Extent.Height - scroll.Viewport.Height)
                is >= .73 and <= .77);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task ThinViewportScrollbarKeepsItsWiderDragAreaAndTrackPaging() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "scrollbar.txt");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 240).Select(row => $"Строка {row}"));
        var window = await OpenWorkspaceAsync(path);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            var bar = Assert.Single(window.GetVisualDescendants().OfType<ViewportScrollBar>());
            await HeadlessTests.WaitForAsync(() => bar.Maximum > 0
                && bar.GetVisualDescendants().OfType<Thumb>().Any(thumb => thumb.Bounds.Height > 0));
            var track = Assert.Single(bar.GetVisualDescendants().OfType<Track>());
            var thumb = Assert.IsType<Thumb>(track.Thumb);
            var rail = bar.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "Rail");
            var grip = thumb.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "Grip");

            Assert.Equal(13, bar.Bounds.Width);
            Assert.Equal(bar.Bounds.Width, thumb.Bounds.Width);
            Assert.Equal(7, grip.Bounds.Width);
            Assert.Equal(new CornerRadius(3.5), grip.CornerRadius);
            Assert.Equal(3, grip.TranslatePoint(default, thumb)!.Value.X);
            Assert.Equal(grip.Bounds.Width, rail.Bounds.Width);
            Assert.Equal(new Thickness(0, 4), rail.Margin);
            Assert.Equal(2, bar.GetVisualDescendants().OfType<RepeatButton>().Count());
            Assert.NotNull(bar.ContextFlyout);

            var point = thumb.TranslatePoint(new Point(1, thumb.Bounds.Height / 2), window)!.Value;
            var normalOpacity = thumb.Opacity;
            window.MouseMove(point);
            Assert.True(thumb.Opacity > normalOpacity);
            window.MouseDown(point, MouseButton.Left);
            var target = point + new Vector(0, (track.Bounds.Height - thumb.Bounds.Height) / 3);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton);
            window.MouseUp(target, MouseButton.Left);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y > 0);
            Assert.InRange(scroll.Offset.Y, bar.Maximum * .31, bar.Maximum * .35);
            Assert.InRange(bar.Value, scroll.Offset.Y - 1, scroll.Offset.Y + 1);

            var beforePage = scroll.Offset.Y;
            var pagePoint = track.TranslatePoint(new Point(1, track.Bounds.Height * .9), window)!.Value;
            window.MouseMove(pagePoint);
            Assert.InRange(scroll.Offset.Y, beforePage - 1, beforePage + 1);
            var expected = Math.Min(bar.Maximum, beforePage + bar.LargeChange);
            window.MouseDown(pagePoint, MouseButton.Left);
            window.MouseUp(pagePoint, MouseButton.Left);
            Assert.True(scroll.Offset.Y > beforePage);
            Assert.InRange(scroll.Offset.Y, expected - 1, expected + 1);

            var beforeWheel = scroll.Offset.Y;
            window.MouseWheel(pagePoint, new Vector(0, -1));
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y > beforeWheel);
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
            var viewport = editor.GetVisualParent()!;
            var minimap = Assert.Single(viewport.GetVisualDescendants().OfType<DocumentMinimap>());
            var minimapDock = Assert.IsType<Grid>(minimap.GetVisualParent());
            var verticalScrollBar = Assert.Single(minimapDock.GetVisualChildren().OfType<ViewportScrollBar>());
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            await HeadlessTests.WaitForAsync(() => scroll.Extent.Height > scroll.Viewport.Height && minimap.Bounds.Height > 10);
            Assert.Equal(ScrollBarVisibility.Hidden, editor.VerticalScrollBarVisibility);
            Assert.True(verticalScrollBar.IsVisible);
            AssertViewportControlsDoNotOverlap(editor, minimap, verticalScrollBar, viewport, window);
            editor.Select(3, 5);
            editor.CaretOffset = 8;
            var top = minimap.TranslatePoint(new Point(20, 1), window)!.Value;
            var middle = minimap.TranslatePoint(new Point(20, minimap.Bounds.Height / 2), window)!.Value;
            var bottom = minimap.TranslatePoint(new Point(20, minimap.Bounds.Height + 2), window)!.Value;
            var maximum = scroll.Extent.Height - scroll.Viewport.Height;

            verticalScrollBar.Value = maximum / 2;
            await HeadlessTests.WaitForAsync(() => Math.Abs(scroll.Offset.Y - maximum / 2) < 1);
            Assert.InRange(verticalScrollBar.Value, maximum * .49, maximum * .51);
            verticalScrollBar.Value = 0;
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y < 1);

            var editorPoint = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(editorPoint, new Vector(0, -1));
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y > 0);
            Assert.InRange(verticalScrollBar.Value, scroll.Offset.Y - 1, scroll.Offset.Y + 1);
            var previousWheelOffset = scroll.Offset.Y;
            var barPoint = verticalScrollBar.TranslatePoint(
                new Point(verticalScrollBar.Bounds.Width / 2, verticalScrollBar.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(barPoint, new Vector(0, -1));
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y > previousWheelOffset);

            window.MouseDown(middle, MouseButton.Left);
            await HeadlessTests.WaitForAsync(() => scroll.Offset.Y is > 0);
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

            var tab = ActiveTab(window);
            Invoke(window, "ShowEditorLevelFilter", tab);
            var filter = Assert.IsType<EditorLogFilter>(Property(tab, "EditorLogFilter"));
            filter.PanelControl.SetLevels(LogLevel.Error);
            Assert.False(minimapDock.IsVisible);
            Assert.False(verticalScrollBar.IsEffectivelyVisible);
            filter.PanelControl.SetLevels(LogLevel.All);
            Assert.True(minimapDock.IsVisible);
            Assert.True(verticalScrollBar.IsEffectivelyVisible);
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
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", path, false, null);
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
    public Task PlainTypingSkipsJsonWorkAndEditingThePrefixStillEnablesValidation() => HeadlessTests.RunAsync(async _ =>
    {
        var window = await OpenWorkspaceAsync();
        try
        {
            Invoke(window, "NewTab", "  заметка");
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            editor.Document.Insert(editor.Document.TextLength, "!");
            Assert.Null(Property(tab, "JsonCancellation"));

            editor.Document.Replace(0, editor.Document.TextLength, new string(' ', 90) + "{broken");
            await HeadlessTests.WaitForAsync(() => Property(tab, "JsonError") is not null);
            editor.Document.Insert(0, "текст");
            Assert.Null(Property(tab, "JsonError"));
            Assert.False(Read<Border>(tab, "JsonBanner").IsVisible);
            Assert.Null(Property(tab, "JsonCancellation"));
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

    [Fact]
    public Task SearchFlagsShareCheckedStyleAndInitiallyHighlightAllHits() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "search.txt");
        await File.WriteAllTextAsync(path, "term term");
        var window = await OpenWorkspaceAsync(path);
        try
        {
            Invoke(window, "OpenSearchPanel", false);
            await HeadlessTests.WaitForAsync(() => window.GetVisualDescendants().OfType<ToggleButton>()
                .Any(button => button.Classes.Contains("search-option")));
            var flags = window.GetVisualDescendants().OfType<ToggleButton>()
                .Where(button => button.Classes.Contains("search-option")).ToArray();
            Assert.Equal(new[] { ".*", "Aa", "«»", "Все вкладки", "Только текущее", "Замена" },
                flags.Select(button => (string)button.Content!));
            var current = Assert.Single(flags, flag => Equals(flag.Content, "Только текущее"));
            Assert.False(current.IsChecked);
            ((TextBox)Field(window, "_searchInput")).Text = "term";
            await CallAsync(window, "FindAsync");
            var highlights = Read<SearchHighlights>(ActiveTab(window), "Highlights");
            Assert.Equal(2, highlights.Pattern!.Matches("term term").Count);
            Assert.False(highlights.OnlyCurrent);
            current.IsChecked = true;
            Assert.True(highlights.OnlyCurrent);
            current.IsChecked = false;
            Assert.False(highlights.OnlyCurrent);
        }
        finally { await CloseAsync(window); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task TabAliasAndOptionalDraftCountSurviveCloseAndRestore(bool file) => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "original.txt");
        const string content = "one\ntwo";
        await File.WriteAllTextAsync(path, content);
        var window = file ? await OpenWorkspaceAsync(path) : await OpenWorkspaceAsync();
        try
        {
            if (!file)
            {
                Invoke(window, "ShowGroups");
                Invoke(window, "NewTab", content);
            }
            var tab = ActiveTab(window);
            var count = Read<TextBlock>(tab, "LineCountLabel");
            Assert.Equal(!file, count.IsVisible);
            if (!file)
            {
                Assert.Equal("2 стр.", count.Text);
                Read<TextEditor>(tab, "Editor").Document.Insert(0, "zero\n");
                Assert.Equal("3 стр.", count.Text);
            }
            var menu = Read<TabItem>(tab, "Item").ContextMenu!;
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Переименовать вкладку…"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var dialog = Assert.Single(window.OwnedWindows);
            await HeadlessTests.WaitForAsync(() => dialog.GetVisualDescendants().OfType<TextBox>().Any());
            dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Моя вкладка";
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "OK")));
            await HeadlessTests.WaitForAsync(() => Read<TextBlock>(tab, "Label").Text == "Моя вкладка");
            var settingsTask = CallAsync(window, "ShowSettingsAsync");
            var settings = Assert.Single(window.OwnedWindows);
            await SelectSettingsSectionAsync(settings, "SettingsSectionDraftArchive");
            DialogControl<CheckBox>(settings, "SettingsShowDraftLines").IsChecked = false;
            Click(DialogControl<Button>(settings, "SettingsSave"));
            await settingsTask;
            Assert.False(count.IsVisible);
            await CallAsync(window, "CloseTabAsync", tab);
            await CloseAsync(window);
            window = await OpenWorkspaceAsync();
            var saved = Assert.Single((List<SessionTab>)Field(window, "_closedTabs"));
            Assert.Equal("Моя вкладка", saved.CustomTitle);
            Assert.Equal(file ? path : null, saved.Path);
            await CallAsync(window, "RestoreClosedAsync", saved);
            var reopened = ActiveTab(window);
            Assert.Equal("Моя вкладка", Read<TextBlock>(reopened, "Label").Text);
            Assert.False(Read<TextBlock>(reopened, "LineCountLabel").IsVisible);
            Assert.Equal(file ? content : "zero\n" + content, Read<TextEditor>(reopened, "Editor").Text);
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        finally { if (window.IsVisible) await CloseAsync(window); }
    });

    [Fact]
    public Task LevelFilterInEditorUsesUnsavedContent() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "editing.log");
        await File.WriteAllTextAsync(path, "INFO on disk\nWARN disk warning\n");
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenPathAsync", path, false, null);
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            Assert.True(Assert.Single(editor.TextArea.TextView.LineTransformers.OfType<LogLevelColorizer>()).Enabled);
            editor.Document.Insert(editor.Document.TextLength, "ERR unsaved");
            editor.Select(0, 3);
            var document = editor.Document;
            var command = Read<TabItem>(tab, "Item").ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "Уровни…"));
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var panel = Assert.Single(window.GetVisualDescendants().OfType<LogLevelFilterPanel>());
            Assert.Empty(window.OwnedWindows);
            Assert.True(editor.IsVisible);
            Assert.False(editor.IsReadOnly);
            Assert.Single(window.GetVisualDescendants().OfType<TextEditor>());
            await HeadlessTests.WaitForAsync(() => panel.GetVisualDescendants().OfType<ToggleButton>().Count() == 7);
            foreach (var level in new[] { "Fatal", "Warn", "Info", "Debug", "Trace", "Без уровня" })
            {
                var toggle = panel.GetVisualDescendants().OfType<ToggleButton>().Single(button =>
                    AutomationProperties.GetName(button) == $"Показывать строки · {level}");
                toggle.IsChecked = false;
                Assert.False(toggle.IsChecked);
            }
            var projection = Assert.Single(window.GetVisualDescendants().OfType<TextEditor>(), view =>
                view.IsReadOnly && !ReferenceEquals(view, editor));
            await HeadlessTests.WaitForAsync(() => projection.Text.Contains("ERR unsaved", StringComparison.Ordinal)
                && !projection.Text.Contains("INFO on disk", StringComparison.Ordinal));
            Assert.Equal("ERR unsaved", projection.Text);
            var lineNumberMargin = Assert.Single(projection.TextArea.LeftMargins.OfType<GroupedLineNumberMargin>());
            Assert.Equal(new[] { 3 }, ((IReadOnlyList<int>)typeof(GroupedLineNumberMargin)
                .GetField("_sourceLines", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lineNumberMargin)!).ToArray());
            Assert.False(editor.IsVisible);
            Assert.True(editor.IsReadOnly);
            Assert.Same(document, editor.Document);
            Assert.Equal("INFO on disk\nWARN disk warning\nERR unsaved", editor.Text);
            projection.Focus();
            Press(window, Key.Z, RawInputModifiers.Control, PhysicalKey.Z);
            Assert.Equal("INFO on disk\nWARN disk warning\nERR unsaved", editor.Text);

            await CallAsync(window, "SaveAsync", false);
            Assert.Equal("INFO on disk\nWARN disk warning\nERR unsaved", await File.ReadAllTextAsync(path));
            foreach (var level in new[] { "Fatal", "Warn", "Info", "Debug", "Trace", "Без уровня" })
            {
                var toggle = panel.GetVisualDescendants().OfType<ToggleButton>().Single(button =>
                    AutomationProperties.GetName(button) == $"Показывать строки · {level}");
                toggle.IsChecked = true;
            }
            Assert.True(editor.IsVisible);
            Assert.False(editor.IsReadOnly);
            Assert.Single(window.GetVisualDescendants().OfType<TextEditor>());
            Click(panel.GetVisualDescendants().OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == "Сбросить и закрыть фильтр уровней"));
            Assert.Same(editor, Read<TextEditor>(tab, "Editor"));
            Assert.Same(document, editor.Document);
            Assert.True(editor.IsVisible);
            Assert.False(editor.IsReadOnly);
            Assert.Equal(3, editor.CaretOffset);
            Assert.Equal(0, editor.SelectionStart);
            Assert.Equal(3, editor.SelectionLength);
            document.UndoStack.Undo();
            Assert.Equal("INFO on disk\nWARN disk warning\n", editor.Text);
        }
        finally { await CloseAsync(window); }
    });

    private static Border TabSurface(TabItem item) => Assert.Single(item.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabSurface");
    private static void Press(MainWindow window, Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? symbol = null)
    {
        window.KeyPress(key, modifiers, physicalKey, symbol);
        window.KeyRelease(key, modifiers, physicalKey, symbol);
    }

    [Fact]
    public Task FileViewsShareEditsAndSaveStateAndPreserveOneDraftOnExternalChange() => HeadlessTests.RunAsync(async profile =>
    {
        var path = Path.Combine(profile, "shared.txt");
        await File.WriteAllTextAsync(path, "original");
        var first = await OpenWorkspaceAsync(path);
        MainWindow? second = null;
        try
        {
            var firstTab = ActiveTab(first);
            var originalEditor = Read<TextEditor>(firstTab, "Editor");
            originalEditor.Document.Insert(0, "local ");
            Invoke(first, "NewTab", "scratch");
            Invoke(first, "OpenTabWindow", ActiveTab(first), null);
            second = Assert.IsType<MainWindow>(first.FindOtherWindow());
            await CallAsync(second, "OpenPathAsync", path, false, null);
            var secondTab = ActiveTab(second);
            var secondEditor = Read<TextEditor>(secondTab, "Editor");
            Assert.Same(originalEditor.Document, secondEditor.Document);
            Assert.Equal("local original", secondEditor.Text);
            Assert.True(Read<bool>(secondTab, "Dirty"));
            secondEditor.Document.UndoStack.Undo();
            Assert.Equal("original", originalEditor.Text);
            secondEditor.Document.Insert(0, "saved ");
            await CallAsync(second, "SaveAsync", false);
            Assert.Equal("saved original", await File.ReadAllTextAsync(path));
            Assert.False(Read<bool>(firstTab, "Dirty"));
            Assert.False(Read<bool>(secondTab, "Dirty"));
            Assert.Equal(Property(firstTab, "Stamp"), Property(secondTab, "Stamp"));

            originalEditor.Document.Remove(0, 1);
            Assert.True(Read<bool>(firstTab, "Dirty"));
            Assert.True(Read<bool>(secondTab, "Dirty"));
            secondEditor.Document.Insert(0, "s");
            Assert.False(Read<bool>(firstTab, "Dirty"));
            Assert.False(Read<bool>(secondTab, "Dirty"));

            originalEditor.Document.Insert(0, "new ");
            await File.WriteAllTextAsync(path, "external");
            await HeadlessTests.WaitForAsync(() => Read<TextEditor>(firstTab, "Editor").Text == "external"
                && Read<TextEditor>(secondTab, "Editor").Text == "external");
            Assert.Same(Read<TextEditor>(firstTab, "Editor").Document, Read<TextEditor>(secondTab, "Editor").Document);
            Assert.Single(Tabs(first).Concat(Tabs(second)), tab => Property(tab, "Path") is null
                && Read<TextEditor>(tab, "Editor").Text == "new saved original");
        }
        finally
        {
            if (second is { IsVisible: true }) await CloseAsync(second);
            if (first.IsVisible) await CloseAsync(first);
        }
    });

    [Fact]
    public Task BackgroundConsoleDisconnectRefreshesFinalOutputAndKeepsItsIndicator() => HeadlessTests.RunAsync(async profile =>
    {
        var consolePath = Path.Combine(profile, "console.log");
        var otherPath = Path.Combine(profile, "other.txt");
        await File.WriteAllTextAsync(consolePath, "first capture frame\n");
        await File.WriteAllTextAsync(otherPath, "selected document");
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenSinglePathAsync", consolePath, true, null, false);
            var consoleTab = ActiveTab(window);
            var log = Read<LogView>(consoleTab, "Log");
            var live = (TextBlock)typeof(LogView).GetField("_liveLabel", Private)!.GetValue(log)!;
            await log.InitialPageLoad;

            await CallAsync(window, "OpenSinglePathAsync", otherPath, false, null, false);
            var status = window.FindControl<TextBlock>("StatusText")!;
            var selectedTabStatus = status.Text;

            await File.AppendAllTextAsync(consolePath, "final capture frame\n");
            log.ReportConsoleDisconnected("Подключённый процесс завершил работу.");

            await HeadlessTests.WaitForAsync(() => !log.AutoRefresh
                && LogText(log).Text?.Contains("final capture frame", StringComparison.Ordinal) == true);
            Assert.Equal(selectedTabStatus, status.Text);
            Assert.Equal("● Источник отключён", live.Text);

            Invoke(window, "Select", consoleTab);
            Assert.Equal("Подключённый процесс завершил работу.", status.Text);
            log.SetAutoRefresh(false);
            Assert.Equal("● Источник отключён", live.Text);
        }
        finally { await CloseAsync(window); }
    });

    [Fact]
    public Task PausedConsoleDisconnectPreservesFollowedPageAndReportsStatus() => HeadlessTests.RunAsync(async profile =>
    {
        var consolePath = Path.Combine(profile, "console.log");
        await File.WriteAllTextAsync(consolePath,
            string.Join('\n', Enumerable.Range(1, 180).Select(line => $"capture row {line:D3}")) + "\n");
        var window = await OpenWorkspaceAsync();
        try
        {
            await CallAsync(window, "OpenSinglePathAsync", consolePath, true, null, false);
            var log = Read<LogView>(ActiveTab(window), "Log");
            await log.InitialPageLoad;
            await (Task)typeof(LogView).GetMethod("GoToEndAsync", Private)!.Invoke(log, null)!;
            Assert.True(log.CaptureState().FollowEnd);
            log.SetAutoRefresh(false);

            var pausedOffset = log.CurrentOffset;
            var pausedText = LogText(log).Text;
            var appendedRows = string.Concat(Enumerable.Repeat("final capture row\n", 3200));
            await File.AppendAllTextAsync(consolePath, appendedRows);
            log.ReportConsoleDisconnected("Подключённый процесс завершил работу.");

            Assert.False(log.AutoRefresh);
            Assert.Equal(pausedOffset, log.CurrentOffset);
            Assert.Equal(pausedText, LogText(log).Text);
            Assert.True(log.CaptureState().FollowEnd);
            Assert.Equal("Подключённый процесс завершил работу.", log.StatusText);
            Assert.Equal("Подключённый процесс завершил работу.", window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally { await CloseAsync(window); }
    });

    private static Color SolidColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static void AssertViewportControlsDoNotOverlap(TextEditor editor, DocumentMinimap minimap,
        ViewportScrollBar verticalScrollBar, Visual viewport, Window window)
    {
        var editorOrigin = editor.TranslatePoint(default, window)!.Value;
        var minimapOrigin = minimap.TranslatePoint(default, window)!.Value;
        Assert.True(editorOrigin.X + editor.Bounds.Width <= minimapOrigin.X);
        if (!verticalScrollBar.IsEffectivelyVisible) return;

        var scrollBarOrigin = verticalScrollBar.TranslatePoint(default, window)!.Value;
        var viewportOrigin = viewport.TranslatePoint(default, window)!.Value;
        Assert.True(minimapOrigin.X + minimap.Bounds.Width <= scrollBarOrigin.X);
        Assert.True(scrollBarOrigin.X + verticalScrollBar.Bounds.Width <= viewportOrigin.X + viewport.Bounds.Width);
    }

    private static async Task<MainWindow> OpenWorkspaceAsync(params string[] paths)
    {
        var window = new MainWindow(paths);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task KeepDraftsDialogAsync(MainWindow window)
    {
        await HeadlessTests.WaitForAsync(() => window.OwnedWindows.OfType<Window>()
            .Any(dialog => dialog.Title == "Переключить рабочее пространство?"));
        var dialog = window.OwnedWindows.OfType<Window>()
            .Single(item => item.Title == "Переключить рабочее пространство?");
        var keep = dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Оставить черновики"));
        Click(keep);
    }

    private static async Task SwitchKeepingDraftsAsync(MainWindow window, string workspaceId)
    {
        var switching = CallAsync(window, "SwitchWorkspaceAsync", workspaceId);
        await KeepDraftsDialogAsync(window);
        await switching;
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

    private static async Task SelectSettingsSectionAsync(Window dialog, string id)
    {
        await HeadlessTests.WaitForAsync(() => dialog.GetVisualDescendants().OfType<ListBoxItem>()
            .Any(item => AutomationProperties.GetAutomationId(item) == id));
        DialogControl<ListBoxItem>(dialog, id).IsSelected = true;
    }

    private static T DialogControl<T>(Window dialog, string id) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetAutomationId(control) == id);

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);

    private static Task CallAsync(MainWindow window, string name, params object?[] args) => (Task)Invoke(window, name, args)!;
    private static object? Property(object target, string name, BindingFlags binding = BindingFlags.Instance | BindingFlags.Public) =>
        target.GetType().GetProperty(name, binding)!.GetValue(target);

    private static T Read<T>(object target, string name) => (T)Property(target, name)!;
}
