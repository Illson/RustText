using RustText.Demo;
using Xunit;

namespace RustText.Tests;

public sealed class WorkspaceCatalogTests
{
    [Fact]
    public async Task CatalogCreatesRenamesAssignsAndDeletesWorkspacesPersistently()
    {
        var profile = CreateProfile();
        try
        {
            var store = new WorkspaceCatalogStore(profile);
            var initial = await store.LoadAsync();
            Assert.Equal("По умолчанию", Assert.Single(initial.Workspaces).Name);

            var created = await store.CreateAsync("  Разбор логов  ");
            await store.RenameAsync(created.Id, "Сборка");
            await store.SetWindowWorkspaceAsync("window:123", created.Id);

            var reloaded = await store.LoadAsync();
            Assert.Equal("Сборка", reloaded.Workspaces.Single(item => item.Id == created.Id).Name);
            Assert.Equal(created.Id, reloaded.WindowAssignments["window:123"]);
            Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "index.json")));

            var workspaceDirectory = store.GetWorkspaceDirectory(created.Id);
            Directory.CreateDirectory(workspaceDirectory);
            await File.WriteAllTextAsync(Path.Combine(workspaceDirectory, "session-data.txt"), "saved");
            var afterDelete = await store.DeleteAsync(created.Id);
            Assert.Single(afterDelete.Workspaces);
            Assert.DoesNotContain("window:123", afterDelete.WindowAssignments.Keys);
            Assert.False(Directory.Exists(workspaceDirectory));
            Assert.Equal("Сборка", Assert.Single(afterDelete.DeletedWorkspaces!).Workspace.Name);
            var restored = await store.RestoreDeletedAsync(created.Id);
            Assert.Equal("saved", await File.ReadAllTextAsync(Path.Combine(workspaceDirectory, "session-data.txt")));
            Assert.Contains(restored.Workspaces, item => item.Id == created.Id);
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    [Fact]
    public async Task RetentionLimitRemovesExpiredWorkspaceData()
    {
        var profile = CreateProfile();
        try
        {
            var store = new WorkspaceCatalogStore(profile);
            await store.LoadAsync();
            var first = await store.CreateAsync("First");
            var firstDirectory = store.GetWorkspaceDirectory(first.Id);
            Directory.CreateDirectory(firstDirectory);
            await File.WriteAllTextAsync(Path.Combine(firstDirectory, "data.txt"), "old");
            var deleted = await store.DeleteAsync(first.Id, retentionLimit: 0);

            Assert.Empty(deleted.DeletedWorkspaces ?? []);
            Assert.False(Directory.Exists(firstDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(store.DirectoryPath, "deleted")));
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    [Fact]
    public async Task CatalogRejectsDuplicateNamesAndPreservesDataWhenIndexIsCorrupt()
    {
        var profile = CreateProfile();
        try
        {
            var store = new WorkspaceCatalogStore(profile);
            await store.LoadAsync();
            await store.CreateAsync("Editor");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(" editor "));

            await File.WriteAllTextAsync(Path.Combine(store.DirectoryPath, "index.json"), "{");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateAsync("Another"));
            Assert.Equal("{", await File.ReadAllTextAsync(Path.Combine(store.DirectoryPath, "index.json")));
            Assert.Throws<InvalidDataException>(() => store.GetWorkspaceDirectory("../../outside"));
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    [Fact]
    public async Task FailedCopyKeepsExistingDeletedWorkspaceAndMissingArchiveStaysRecoverable()
    {
        using var folder = new TempDirectory();
        var store = new WorkspaceCatalogStore(folder.Path);
        var saved = await store.CreateAsync("Existing workspace");
        var source = store.GetWorkspaceDirectory(saved.Id);
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "saved content");
        var history = await store.DeleteAsync(saved.Id);
        var original = Assert.Single(history.DeletedWorkspaces!);

        var incomplete = await store.CreateAsync("Incomplete copy");
        var discarded = await store.DiscardIncompleteCopyAsync(incomplete.Id);
        Assert.Equal(original, Assert.Single(discarded.DeletedWorkspaces!));
        var archive = Path.Combine(store.DirectoryPath, "deleted", original.DirectoryName);
        var moved = Path.Combine(folder.Path, "temporarily-unavailable");
        Directory.Move(archive, moved);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreDeletedAsync(saved.Id));
        Assert.Equal(original, Assert.Single((await store.LoadAsync()).DeletedWorkspaces!));
        Directory.Move(moved, archive);
        await store.RestoreDeletedAsync(saved.Id);
        Assert.Equal("saved content", await File.ReadAllTextAsync(Path.Combine(source, "data.txt")));
    }

    [Fact]
    public async Task InterruptedDirectoryMovesFollowTheLastCommittedCatalog()
    {
        using var folder = new TempDirectory();
        var store = new WorkspaceCatalogStore(folder.Path);
        var workspace = await store.CreateAsync("Project");
        var live = store.GetWorkspaceDirectory(workspace.Id);
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "note.txt"), "keep me");
        var trash = Directory.CreateDirectory(Path.Combine(store.DirectoryPath, "deleted")).FullName;
        var name = workspace.Id + "-1000-" + Guid.NewGuid().ToString("N");
        var marker = Path.Combine(store.DirectoryPath, "pending-directory-move");

        await File.WriteAllTextAsync(marker, name);
        Directory.Move(live, Path.Combine(trash, name));
        var restarted = new WorkspaceCatalogStore(folder.Path);
        Assert.Contains((await restarted.LoadAsync()).Workspaces, entry => entry.Id == workspace.Id);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(live, "note.txt")));
        Assert.False(File.Exists(marker));

        var deleted = Assert.Single((await restarted.DeleteAsync(workspace.Id)).DeletedWorkspaces!);
        var archived = Path.Combine(trash, deleted.DirectoryName);
        await File.WriteAllTextAsync(marker, deleted.DirectoryName);
        Directory.Move(archived, live);
        Assert.Single((await store.LoadAsync()).DeletedWorkspaces!);
        Assert.False(Directory.Exists(live));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(archived, "note.txt")));
        await store.RestoreDeletedAsync(workspace.Id);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(live, "note.txt")));
    }

    [Fact]
    public async Task ConcurrentCatalogWritersKeepBothWorkspaceEntries()
    {
        using var folder = new TempDirectory();
        var first = new WorkspaceCatalogStore(folder.Path);
        var second = new WorkspaceCatalogStore(folder.Path);
        await Task.WhenAll(first.CreateAsync("First"), second.CreateAsync("Second"));
        var saved = await first.LoadAsync();
        Assert.Contains(saved.Workspaces, entry => entry.Name == "First");
        Assert.Contains(saved.Workspaces, entry => entry.Name == "Second");
    }

    private static string CreateProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RustText-workspaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteProfile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), fullPath, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }
}
