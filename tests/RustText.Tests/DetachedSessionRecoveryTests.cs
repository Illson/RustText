using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class DetachedSessionRecoveryTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public Task StoredDraftStaysInItsSourceWhenDraggingAcrossSessionStores() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var target = await new WorkspaceCatalogStore(profile).CreateAsync("Independent target");
            var primary = await OpenWindowAsync();
            MainWindow? destination = null;
            try
            {
                Invoke(primary, "NewTab", "keep source window open");
                var draftText = new string('x', 64 * 1024 + 1);
                Invoke(primary, "NewTab", draftText);
                var draft = ActiveTab(primary);
                await CallAsync(primary, "ToggleLogModeAsync", draft);
                await HeadlessTests.WaitForAsync(() => Property(draft, "Recovery") is SessionTab);
                var stored = Assert.IsType<SessionTab>(Property(draft, "Recovery"));
                var sourceStorage = Assert.IsType<StorageService>(Property(primary, "SessionStorage"));

                await CallAsync(primary, "OpenWorkspaceWindowAsync", target.Id);
                destination = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));
                await HeadlessTests.WaitForAsync(() => (bool)Field(destination, "_restoreFinished"));
                Invoke(primary, "TransferTab", draft, destination, Field(destination, "_active"), null);

                Assert.Contains(draft, Tabs(primary));
                Assert.Empty(Tabs(destination));
                Assert.Equal(draftText, await sourceStorage.LoadDraftAsync(stored));
                Assert.Contains("Сохранить копию", primary.FindControl<TextBlock>("StatusText")!.Text);
            }
            finally
            {
                if (destination is { IsVisible: true }) await CloseAsync(destination);
                if (primary.IsVisible) await CloseAsync(primary);
            }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AdditionalTransferredAndNewDraftsInDetachedWindowAreRecoverable(bool closeDetachedFirst) =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string first = "draft used to open detached window";
            const string second = "another draft transferred into detached window";
            const string newDraft = "draft created inside detached window";
            var (primary, source, detached, workspaceId) = await SetUpDetachedWorkspaceAsync(profile, first);
            MainWindow? restarted = null;
            try
            {
                Invoke(source, "NewTab", second);
                Invoke(source, "TransferTab", ActiveTab(source), detached, Field(detached, "_active"), null);
                Invoke(detached, "NewTab", newDraft);
                Assert.Equal(3, Tabs(detached).Count);

                await CloseAsync(closeDetachedFirst ? detached : source);
                await CloseAsync(closeDetachedFirst ? source : detached);
                await CloseAsync(primary);

                restarted = await OpenWindowAsync();
                Assert.Equal(workspaceId, Field(restarted, "_workspaceId"));
                Assert.Null(Invoke(restarted, "FindOtherWindow"));
                var contents = new List<string>();
                foreach (var entry in ClosedTabs(restarted))
                    contents.Add((await RestoreClosedTextAsync(restarted, entry)).Text);
                Assert.Equal(new[] { first, second, newDraft }.Order(), contents.Order());
            }
            finally
            {
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
                if (detached.IsVisible) await CloseAsync(detached);
                if (source.IsVisible) await CloseAsync(source);
                if (primary.IsVisible) await CloseAsync(primary);
            }
        });

    [Fact]
    public Task ArchivedDetachedDraftAppearsInPrimaryArchiveAfterRestart() =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string draftText = "tracked draft archived before windows close";
            var (primary, source, detached, workspaceId) = await SetUpDetachedWorkspaceAsync(profile, draftText);
            MainWindow? restarted = null;
            try
            {
                var trackedId = (long)Property(ActiveTab(detached), "Id")!;
                await CallAsync(detached, "CloseTabAsync", ActiveTab(detached));
                Invoke(source, "NewTab", "untracked private draft");
                var untrackedId = (long)Property(ActiveTab(source), "Id")!;
                await CallAsync(source, "CloseTabAsync", ActiveTab(source));
                var privateHistory = (List<SessionTab>)Field(detached, "_closedTabs");
                var oldClosedAt = DateTimeOffset.UtcNow.AddHours(-6).ToUnixTimeMilliseconds();
                for (var i = 0; i < privateHistory.Count; i++)
                    privateHistory[i] = privateHistory[i] with { ClosedAtMs = oldClosedAt };
                await CallAsync(detached, "ArchiveClosedDraftsAsync");
                Assert.Empty(privateHistory);
                var privateStorage = Assert.IsType<StorageService>(Property(detached, "SessionStorage"));
                var entries = await privateStorage.DraftArchive.GetEntriesAsync();
                Assert.Contains(entries, entry => entry.Tab.Id == untrackedId);
                var original = Assert.Single(entries, entry => entry.Tab.Id == trackedId);

                await CloseAsync(detached);
                await CloseAsync(source);
                await CloseAsync(primary);

                restarted = await OpenWindowAsync();
                Assert.Equal(workspaceId, Field(restarted, "_workspaceId"));
                Assert.Empty(ClosedTabs(restarted));
                var storage = Assert.IsType<StorageService>(Property(restarted, "SessionStorage"));
                var recovered = Assert.Single(await storage.DraftArchive.GetEntriesAsync());
                Assert.Equal(original.ClosedAtMs, recovered.ClosedAtMs);
                Assert.Equal(original.ArchivedAtMs, recovered.ArchivedAtMs);
                Assert.Equal(original.Sha256, recovered.Sha256);
                Assert.Equal(draftText, await storage.LoadDraftAsync(await storage.DraftArchive.RestoreAsync(recovered)));
            }
            finally
            {
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
                if (detached.IsVisible) await CloseAsync(detached);
                if (source.IsVisible) await CloseAsync(source);
                if (primary.IsVisible) await CloseAsync(primary);
            }
        });

    [Fact]
    public Task UpdateRestartKeepsAssignedIndependentDraftReachableWithoutPrimaryDuplicate() =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string draftText = "draft stays in assigned independent session";
            const string otherDraftText = "other independent draft remains available";
            const string latestEdit = " edited after updater restart";
            var (primary, source, detached, workspaceId) = await SetUpDetachedWorkspaceAsync(profile, draftText);
            MainWindow? restarted = null;
            MainWindow? restoredSource = null;
            try
            {
                Invoke(source, "NewTab", otherDraftText);
                var sourceWindowId = (string)Field(source, "_windowId");
                await CallAsync(primary, "RestartForUpdateAsync", (Func<Task>)(() => Task.CompletedTask));
                Assert.All(new[] { primary, source, detached }, window => Assert.False(window.IsVisible));

                restarted = await OpenWindowAsync();
                Assert.Equal(workspaceId, Field(restarted, "_workspaceId"));
                restoredSource = Assert.IsType<MainWindow>(Invoke(restarted, "FindOtherWindow"));
                await HeadlessTests.WaitForAsync(() => (bool)Field(restoredSource, "_restoreFinished"));
                Assert.Equal(2, WorkspaceWindows(restarted).Length);
                Assert.Empty(ClosedTabs(restarted));
                foreach (var item in Tabs(restoredSource).ToArray())
                    if (Property(item, "Deferred") is not null)
                        await CallAsync(restoredSource, "ActivateDeferredAsync", item);

                var restoredDraft = Assert.Single(Tabs(restoredSource), item =>
                    Property(item, "Editor") is TextEditor editor && editor.Text == draftText);
                Assert.Contains(Tabs(restoredSource), item =>
                    Property(item, "Editor") is TextEditor editor && editor.Text == otherDraftText);
                var editor = (TextEditor)Property(restoredDraft, "Editor")!;
                editor.Document.Insert(editor.Document.TextLength, latestEdit);

                await CloseAsync(restoredSource);
                restoredSource = null;
                var assignments = await new WorkspaceCatalogStore(profile).LoadAsync();
                Assert.DoesNotContain(sourceWindowId, assignments.WindowAssignments.Keys);

                var recovered = Assert.Single(ClosedTabs(restarted));
                Assert.Equal(draftText + latestEdit, (await RestoreClosedTextAsync(restarted, recovered)).Text);
                Assert.DoesNotContain(WorkspaceWindows(restarted), window => !ReferenceEquals(window, restarted));
            }
            finally
            {
                if (restoredSource is { IsVisible: true }) await CloseAsync(restoredSource);
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
                if (detached.IsVisible) await CloseAsync(detached);
                if (source.IsVisible) await CloseAsync(source);
                if (primary.IsVisible) await CloseAsync(primary);
            }
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task UpdateRestartRecoversOrphanedDetachedDraftAndRetriesWithoutLoss(
        bool failFirst, bool restoreFirstCopy) => HeadlessTests.RunAsync(async profile =>
    {
        const string draftText = "draft survives orphaned updater restart";
        const string retryEdit = " with an edit after failure";
        var (primary, source, detached, workspaceId) = await SetUpDetachedWorkspaceAsync(profile, draftText);
        MainWindow? restarted = null;
        try
        {
            await CloseAsync(source);
            var sourceWindowId = (string)Field(source, "_windowId");
            var assignmentsAfterSourceClose = await new WorkspaceCatalogStore(profile).LoadAsync();
            Assert.DoesNotContain(sourceWindowId, assignmentsAfterSourceClose.WindowAssignments.Keys);

            if (failFirst)
            {
                Func<Task> failInstaller = () => Task.FromException(new IOException("Simulated updater start failure"));
                await Assert.ThrowsAsync<IOException>(() => CallAsync(primary, "RestartForUpdateAsync", failInstaller));
                Assert.True(primary.IsVisible && primary.IsEnabled);
                Assert.True(detached.IsVisible && detached.IsEnabled);
                Assert.Equal(draftText, ((TextEditor)Property(ActiveTab(detached), "Editor")!).Text);

                var firstCopy = Assert.Single(ClosedTabs(primary));
                if (restoreFirstCopy)
                    Assert.Equal(draftText, (await RestoreClosedTextAsync(primary, firstCopy)).Text);
                var editor = (TextEditor)Property(ActiveTab(detached), "Editor")!;
                editor.Document.Insert(editor.Document.TextLength, retryEdit);
            }

            bool helperRan = false;
            Func<Task> startInstaller = () =>
            {
                helperRan = true;
                return Task.CompletedTask;
            };
            await CallAsync(primary, "RestartForUpdateAsync", startInstaller);
            Assert.True(helperRan);
            Assert.False(primary.IsVisible);
            Assert.False(detached.IsVisible);

            restarted = await OpenWindowAsync();
            Assert.Equal(workspaceId, Field(restarted, "_workspaceId"));
            Assert.Null(Invoke(restarted, "FindOtherWindow"));
            Assert.Single(WorkspaceWindows(restarted));

            var expected = failFirst ? draftText + retryEdit : draftText;
            var recovered = Assert.Single(ClosedTabs(restarted));
            Assert.Equal(expected, (await RestoreClosedTextAsync(restarted, recovered)).Text);
            if (restoreFirstCopy)
                Assert.Contains(Tabs(restarted), tab =>
                    Property(tab, "Editor") is TextEditor editor && editor.Text == draftText);
            var assignments = await new WorkspaceCatalogStore(profile).LoadAsync();
            Assert.DoesNotContain(assignments.WindowAssignments.Keys, id => id != WorkspaceCatalogStore.PrimaryWindowId);
        }
        finally
        {
            if (restarted is { IsVisible: true }) await CloseAsync(restarted);
            if (detached.IsVisible) await CloseAsync(detached);
            if (source.IsVisible) await CloseAsync(source);
            if (primary.IsVisible) await CloseAsync(primary);
        }
    });

    [Fact]
    public Task UpdateRestartRefusesUnavailableCachedSessionWithoutClosingWindows() =>
        HeadlessTests.RunAsync(async profile =>
        {
            var catalog = new WorkspaceCatalogStore(profile);
            var cachedWorkspace = await catalog.CreateAsync("Unavailable cached workspace");
            var activeWorkspace = await catalog.CreateAsync("Active workspace");
            var window = await OpenWindowAsync();
            var unavailable = Assert.IsType<HashSet<string>>(Property(Field(window, "_workspace"), "UnavailableSessions"));
            string? unavailableKey = null;
            try
            {
                await CallAsync(window, "SwitchWorkspaceAsync", cachedWorkspace.Id);
                Invoke(window, "NewTab", "draft in cached workspace");
                await CallAsync(window, "SaveSessionAsync");
                await CallAsync(window, "SwitchWorkspaceAsync", activeWorkspace.Id);

                var views = (IDictionary)Field(window, "_workspaceViews");
                var cachedView = views[cachedWorkspace.Id]!;
                var cachedStorage = Assert.IsType<StorageService>(Property(cachedView, "Storage"));
                unavailableKey = Path.GetFullPath(cachedStorage.RootDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
                Assert.True(unavailable.Add(unavailableKey));

                var helperRan = false;
                Func<Task> helper = () =>
                {
                    helperRan = true;
                    return Task.CompletedTask;
                };
                await Assert.ThrowsAsync<IOException>(() => CallAsync(window, "RestartForUpdateAsync", helper));

                Assert.False(helperRan);
                Assert.True(window.IsVisible && window.IsEnabled);
                Assert.Equal(activeWorkspace.Id, Field(window, "_workspaceId"));
                var cachedTabs = ((IDictionary)Property(cachedView, "Tabs")!).Values.Cast<object>().ToArray();
                Assert.Single(cachedTabs);
                var checkpoint = Assert.IsType<SessionState>(await cachedStorage.LoadSessionAsync());
                Assert.Equal("draft in cached workspace", await cachedStorage.LoadDraftAsync(Assert.Single(checkpoint.Tabs)));
            }
            finally
            {
                if (unavailableKey is not null) unavailable.Remove(unavailableKey);
                if (window.IsVisible) await CloseAsync(window);
            }
        });

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public Task DetachedDraftFromIndependentWorkspaceWindowMergesIntoPrimarySession(
        bool closeDetachedFirst, bool keepPrimaryWorkspaceLive) => HeadlessTests.RunAsync(async profile =>
    {
        const string canonicalText = "canonical active tab remains";
        const string canonicalEditedText = "canonical active tab remains · unsaved primary edit";
        const string canonicalDraftText = "canonical closed draft remains";
        const string detachedDraftText = "detached secondary draft remains";

        var catalog = new WorkspaceCatalogStore(profile);
        var namedWorkspace = await catalog.CreateAsync("Исходное пространство");
        var otherWorkspace = await catalog.CreateAsync("Проверка без активного первичного окна");
        var primary = await OpenWindowAsync();
        MainWindow? source = null;
        MainWindow? detached = null;
        MainWindow? restarted = null;
        try
        {
            await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
            Invoke(primary, "NewTab", canonicalText);
            var canonicalTabId = Property(ActiveTab(primary), "Id");
            Invoke(primary, "NewTab", canonicalDraftText);
            var canonicalDraftId = Property(ActiveTab(primary), "Id");
            await CallAsync(primary, "CloseTabAsync", ActiveTab(primary));
            await CallAsync(primary, "SaveSessionAsync");

            await CallAsync(primary, "OpenWorkspaceWindowAsync", namedWorkspace.Id);
            source = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));
            await HeadlessTests.WaitForAsync(() => (bool)Field(source, "_restoreFinished"));
            Assert.NotSame(Property(primary, "SessionStorage"), Property(source, "SessionStorage"));

            Invoke(source, "NewTab", detachedDraftText);
            var detachedTabId = Property(ActiveTab(source), "Id");
            Invoke(source, "OpenTabWindow", ActiveTab(source), null);
            detached = Assert.IsType<MainWindow>(Invoke(source, "FindOtherWindow"));
            Assert.Same(Property(source, "SessionStorage"), Property(detached, "SessionStorage"));

            var canonicalTab = Assert.Single(Tabs(primary), tab => Equals(Property(tab, "Id"), canonicalTabId));
            var canonicalEditor = (TextEditor)Property(canonicalTab, "Editor")!;
            canonicalEditor.Document.Insert(canonicalEditor.Document.TextLength, " · unsaved primary edit");
            Assert.True((bool)Property(canonicalTab, "Dirty")!);

            if (!keepPrimaryWorkspaceLive)
            {
                await CloseAsync(primary);
                primary = null!;
            }
            else
            {
                await CallAsync(primary, "SwitchWorkspaceAsync", otherWorkspace.Id);
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Assert.Same(Property(source, "SessionStorage"), Property(detached, "SessionStorage"));
            }

            if (closeDetachedFirst)
            {
                await CloseAsync(detached);
                detached = null;
                await CloseAsync(source);
                source = null;
            }
            else
            {
                await CloseAsync(source);
                source = null;
                await CloseAsync(detached);
                detached = null;
            }

            if (primary is { IsVisible: true })
            {
                await CloseAsync(primary);
                primary = null!;
            }
            restarted = await OpenWindowAsync();
            Assert.Null(Invoke(restarted, "FindOtherWindow"));
            Assert.Single(WorkspaceWindows(restarted));

            if (!keepPrimaryWorkspaceLive)
                await CallAsync(restarted, "SwitchWorkspaceAsync", namedWorkspace.Id);

            var activeTabs = Tabs(restarted);
            var restoredCanonicalTab = Assert.Single(activeTabs, tab => Equals(Property(tab, "Id"), canonicalTabId));
            Assert.Equal(canonicalEditedText, ((TextEditor)Property(restoredCanonicalTab, "Editor")!).Text);

            var closed = ClosedTabs(restarted);
            var retainedCanonicalDraft = Assert.Single(closed, tab => Equals(Property(tab, "Id"), canonicalDraftId));
            Assert.Equal(canonicalDraftText, (await RestoreClosedTextAsync(restarted, retainedCanonicalDraft)).Text);
            foreach (var closedTab in ClosedTabs(restarted).ToArray())
                await RestoreClosedTextAsync(restarted, closedTab);

            var restoredTexts = Tabs(restarted)
                .Select(tab => ((TextEditor)Property(tab, "Editor")!).Text)
                .ToArray();
            Assert.Single(restoredTexts, text => text == canonicalDraftText);
            Assert.Single(restoredTexts, text => text == detachedDraftText);
            var restoredDetached = Assert.Single(Tabs(restarted), tab =>
                ((TextEditor)Property(tab, "Editor")!).Text == detachedDraftText);
            Assert.NotEqual(detachedTabId, Property(restoredDetached, "Id"));

            var restoredCatalog = await new WorkspaceCatalogStore(profile).LoadAsync();
            Assert.Equal(namedWorkspace.Id, restoredCatalog.WindowAssignments[WorkspaceCatalogStore.PrimaryWindowId]);
            Assert.DoesNotContain(restoredCatalog.WindowAssignments.Keys, id => id != WorkspaceCatalogStore.PrimaryWindowId);
            Assert.Single(WorkspaceWindows(restarted));
        }
        finally
        {
            if (detached is { IsVisible: true }) await CloseAsync(detached);
            if (source is { IsVisible: true }) await CloseAsync(source);
            if (primary is { IsVisible: true }) await CloseAsync(primary);
            if (restarted is { IsVisible: true }) await CloseAsync(restarted);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task DetachedPrimaryWorkspaceDraftSurvivesAtoBtoAAndEitherCloseOrder(bool closeDetachedFirst) =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string anchorText = "primary anchor draft";
            const string detachedText = "primary detached draft";
            var catalog = new WorkspaceCatalogStore(profile);
            var namedWorkspace = await catalog.CreateAsync("Первичное пространство");
            var otherWorkspace = await catalog.CreateAsync("Соседнее пространство");
            var primary = await OpenWindowAsync();
            MainWindow? detached = null;
            MainWindow? restarted = null;
            try
            {
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Invoke(primary, "NewTab", anchorText);
                var anchorId = Property(ActiveTab(primary), "Id");
                Invoke(primary, "NewTab", detachedText);
                var detachedId = Property(ActiveTab(primary), "Id");
                Invoke(primary, "OpenTabWindow", ActiveTab(primary), null);
                detached = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));

                var sharedStorage = Property(primary, "SessionStorage");
                Assert.Same(sharedStorage, Property(detached, "SessionStorage"));
                await CallAsync(primary, "SwitchWorkspaceAsync", otherWorkspace.Id);
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Assert.Same(sharedStorage, Property(primary, "SessionStorage"));
                Assert.Same(sharedStorage, Property(detached, "SessionStorage"));

                if (closeDetachedFirst)
                {
                    await CloseAsync(detached);
                    detached = null;
                    await CloseAsync(primary);
                    primary = null!;
                }
                else
                {
                    await CloseAsync(primary);
                    primary = null!;
                    await CloseAsync(detached);
                    detached = null;
                }

                restarted = await OpenWindowAsync();
                Assert.Null(Invoke(restarted, "FindOtherWindow"));
                Assert.Single(WorkspaceWindows(restarted));
                var closed = ClosedTabs(restarted);
                var expectedClosedId = closeDetachedFirst ? detachedId : anchorId;
                Assert.Contains(closed, tab => Equals(Property(tab, "Id"), expectedClosedId));
                foreach (var closedTab in ClosedTabs(restarted).ToArray())
                    await RestoreClosedTextAsync(restarted, closedTab);
                var texts = Tabs(restarted).Select(tab => ((TextEditor)Property(tab, "Editor")!).Text).ToArray();
                Assert.Contains(anchorText, texts);
                Assert.Contains(detachedText, texts);
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (primary is { IsVisible: true }) await CloseAsync(primary);
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
            }
        });

    [Fact]
    public Task UnreadablePrimaryCheckpointKeepsDetachedDraftOpenForCloseRetry() =>
        HeadlessTests.RunAsync(async profile =>
        {
            const string canonicalText = "canonical session stays recoverable";
            const string detachedText = "detached text survives a failed merge";
            var catalog = new WorkspaceCatalogStore(profile);
            var namedWorkspace = await catalog.CreateAsync("Рабочее пространство для повтора");
            var primary = await OpenWindowAsync();
            MainWindow? source = null;
            MainWindow? detached = null;
            MainWindow? restarted = null;
            try
            {
                await CallAsync(primary, "SwitchWorkspaceAsync", namedWorkspace.Id);
                Invoke(primary, "NewTab", canonicalText);
                await CallAsync(primary, "SaveSessionAsync");
                var primaryStorage = Property(primary, "SessionStorage")!;

                await CallAsync(primary, "OpenWorkspaceWindowAsync", namedWorkspace.Id);
                source = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));
                await HeadlessTests.WaitForAsync(() => (bool)Field(source, "_restoreFinished"));
                Invoke(source, "NewTab", detachedText);
                var detachedTabId = Property(ActiveTab(source), "Id");
                Invoke(source, "OpenTabWindow", ActiveTab(source), null);
                detached = Assert.IsType<MainWindow>(Invoke(source, "FindOtherWindow"));

                await CloseAsync(primary);
                primary = null!;
                await CloseAsync(source);
                source = null;

                var sessionDirectory = Path.Combine((string)Property(primaryStorage, "RootDirectory")!, "session");
                var latestPath = Path.Combine(sessionDirectory, "latest.json");
                var previousPath = Path.Combine(sessionDirectory, "previous.json");
                Assert.True(File.Exists(latestPath));
                Assert.True(File.Exists(previousPath));
                var latestBytes = await File.ReadAllBytesAsync(latestPath);
                var previousBytes = await File.ReadAllBytesAsync(previousPath);
                try
                {
                    await File.WriteAllTextAsync(latestPath, "unreadable-latest-checkpoint");
                    await File.WriteAllTextAsync(previousPath, "unreadable-previous-checkpoint");

                    detached.Close();
                    await HeadlessTests.WaitForAsync(() =>
                        detached.FindControl<TextBlock>("StatusText")?.Text?.StartsWith(
                            "Не удалось записать сессию.", StringComparison.Ordinal) == true);

                    Assert.True(detached.IsVisible);
                    Assert.Equal(detachedText, ((TextEditor)Property(ActiveTab(detached), "Editor")!).Text);
                    Assert.Contains("восстанов", detached.FindControl<TextBlock>("StatusText")!.Text!, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    await File.WriteAllBytesAsync(latestPath, latestBytes);
                    await File.WriteAllBytesAsync(previousPath, previousBytes);
                }

                detached.FindControl<TextBlock>("StatusText")!.Text = string.Empty;
                await CloseAsync(detached);
                detached = null;
                restarted = await OpenWindowAsync();
                Assert.Null(Invoke(restarted, "FindOtherWindow"));
                Assert.Single(WorkspaceWindows(restarted));
                Assert.Equal(namedWorkspace.Id, Field(restarted, "_workspaceId"));
                Assert.Equal(canonicalText, ((TextEditor)Property(ActiveTab(restarted), "Editor")!).Text);

                var recovered = Assert.Single(ClosedTabs(restarted), tab =>
                    !Equals(Property(tab, "Id"), detachedTabId));
                Assert.Equal(detachedText, (await RestoreClosedTextAsync(restarted, recovered)).Text);
                var assignments = await new WorkspaceCatalogStore(profile).LoadAsync();
                Assert.DoesNotContain(assignments.WindowAssignments.Keys, id => id != WorkspaceCatalogStore.PrimaryWindowId);
            }
            finally
            {
                if (detached is { IsVisible: true }) await CloseAsync(detached);
                if (source is { IsVisible: true }) await CloseAsync(source);
                if (primary is { IsVisible: true }) await CloseAsync(primary);
                if (restarted is { IsVisible: true }) await CloseAsync(restarted);
            }
        });

    private static async Task<MainWindow> OpenWindowAsync()
    {
        var window = new MainWindow([]);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task<(MainWindow Primary, MainWindow Source, MainWindow Detached, string WorkspaceId)>
        SetUpDetachedWorkspaceAsync(string profile, string draftText)
    {
        var catalog = new WorkspaceCatalogStore(profile);
        var named = await catalog.CreateAsync("Workspace with independent window");
        var primary = await OpenWindowAsync();
        await CallAsync(primary, "SwitchWorkspaceAsync", named.Id);
        Invoke(primary, "NewTab", "primary workspace anchor");
        await CallAsync(primary, "OpenWorkspaceWindowAsync", named.Id);
        var source = Assert.IsType<MainWindow>(Invoke(primary, "FindOtherWindow"));
        await HeadlessTests.WaitForAsync(() => (bool)Field(source, "_restoreFinished"));
        Invoke(source, "NewTab", draftText);
        Invoke(source, "OpenTabWindow", ActiveTab(source), null);
        var detached = Assert.IsType<MainWindow>(Invoke(source, "FindOtherWindow"));
        return (primary, source, detached, named.Id);
    }

    private static async Task<(object? Id, string Text)> RestoreClosedTextAsync(MainWindow window, object closedTab)
    {
        var id = Property(closedTab, "Id");
        await CallAsync(window, "RestoreClosedAsync", closedTab);
        var tab = Assert.Single(Tabs(window), candidate => Equals(Property(candidate, "Id"), id));
        return (id, ((TextEditor)Property(tab, "Editor")!).Text);
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.FindControl<TextBlock>("StatusText")!.Text = string.Empty;
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        Assert.False(window.IsVisible, window.FindControl<TextBlock>("StatusText")?.Text);
    }

    private static async Task CallAsync(MainWindow window, string name, params object?[] arguments)
    {
        var task = (Task)Invoke(window, name, arguments)!;
        await task;
    }

    private static object? Invoke(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, AnyInstance)!.Invoke(window, arguments);

    private static object ActiveTab(MainWindow window) => Property(window, "ActiveTab")!;

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)!.GetValue(target)!;

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, AnyInstance)!.GetValue(target);

    private static object[] WorkspaceWindows(MainWindow window)
    {
        var workspace = Field(window, "_workspace");
        var windows = workspace.GetType().GetProperty("Windows", BindingFlags.Instance | BindingFlags.Public)!.GetValue(workspace);
        return ((IEnumerable)windows!).Cast<object>().ToArray();
    }

    private static List<object> Tabs(MainWindow window) =>
        ((IDictionary)Field(window, "_tabs")).Values.Cast<object>().ToList();

    private static List<object> ClosedTabs(MainWindow window) =>
        ((IEnumerable)Field(window, "_closedTabs")).Cast<object>().ToList();
}
