using RustText.Demo;
using System.Text;
using Xunit;

namespace RustText.Tests;

public sealed class DraftArchiveTests
{
    [Fact]
    public async Task ArchiveSettingsDefaultForOldProfilesAndPersistValidatedValues()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(storage.RootDirectory, "settings.json"), "{\"untitled_tab_limit\":12}");

        var legacy = await storage.LoadSettingsAsync();
        Assert.Equal(300, legacy.ArchiveClosedDraftAfterMinutes);
        Assert.Equal(30, legacy.ArchivedDraftRetentionDays);

        await storage.SaveSettingsAsync(legacy with
        {
            ArchiveClosedDraftAfterMinutes = 900_000,
            ArchivedDraftRetentionDays = -4
        });
        var saved = await storage.LoadSettingsAsync();
        Assert.Equal(525_600, saved.ArchiveClosedDraftAfterMinutes);
        Assert.Equal(0, saved.ArchivedDraftRetentionDays);
    }

    [Fact]
    public async Task ClosedAtTimePersistsInSessionCheckpoint()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var closedAt = DateTimeOffset.UtcNow.AddHours(-4).ToUnixTimeMilliseconds();
        var state = new SessionState([], [new SessionTab(13, null, TextEncodingKind.Utf8) { ClosedAtMs = closedAt }]);

        await storage.SaveSessionAsync(state);

        var restored = Assert.Single((await storage.LoadSessionAsync())!.ClosedTabs!);
        Assert.Equal(closedAt, restored.ClosedAtMs);
    }

    [Fact]
    public async Task ArchiveAndRestorePreserveTextMetadataAndChecksum()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var archive = new DraftArchiveService(storage.RootDirectory);
        var text = string.Concat(Enumerable.Repeat("Повторяемая строка 💡\n", 500));
        var closed = await storage.SaveClosedDraftAsync(new SessionTab(12, null, TextEncodingKind.Utf8)
        {
            UntitledName = "Рабочий черновик",
            ClosedAtMs = DateTimeOffset.UtcNow.AddHours(-6).ToUnixTimeMilliseconds(),
            SelectionStart = 5,
            SelectionLength = 3
        }, text);

        var archived = await archive.ArchiveAsync(closed, storage.GetDraftPath(closed)!);
        var zipPath = Path.Combine(storage.RootDirectory, "drafts-archive", archived.Id + ".zip");
        Assert.True(File.Exists(zipPath));
        Assert.True(new FileInfo(zipPath).Length < Encoding.UTF8.GetByteCount(text));
        Assert.Equal(closed.DraftSha256, archived.Sha256);
        Assert.Equal(closed.ClosedAtMs, archived.ClosedAtMs);
        Assert.Equal("Повторяемая строка 💡", archived.DisplayName);
        Assert.DoesNotContain("Повторяемая строка", Encoding.UTF8.GetString(File.ReadAllBytes(zipPath)));

        var restored = await archive.RestoreAsync(archived);
        Assert.Equal(text, await storage.LoadDraftAsync(restored));
        Assert.Equal(closed.SelectionStart, restored.SelectionStart);
        Assert.Equal(closed.SelectionLength, restored.SelectionLength);
        Assert.Equal(closed.DraftSha256, restored.DraftSha256);
        await archive.RemoveAsync(archived);
        Assert.Empty(await archive.GetEntriesAsync());
    }

    [Fact]
    public async Task CopyEntryToIsIdempotentAndPreservesArchiveMetadataAndSource()
    {
        using var directory = new TempDirectory();
        var sourceStorage = StorageService.OpenForTesting(Path.Combine(directory.Path, "source"));
        var destinationStorage = StorageService.OpenForTesting(Path.Combine(directory.Path, "destination"));
        var sourceArchive = sourceStorage.DraftArchive;
        var destinationArchive = destinationStorage.DraftArchive;
        var closedAt = DateTimeOffset.UtcNow.AddHours(-7).ToUnixTimeMilliseconds();
        const string text = "archived source stays available 💡\nsecond line";
        var draft = await sourceStorage.SaveClosedDraftAsync(new SessionTab(17, null, TextEncodingKind.Utf8)
        {
            CustomTitle = "Tracked detached draft",
            UntitledName = "Archive copy test",
            ClosedAtMs = closedAt,
            SelectionStart = 4,
            SelectionLength = 6
        }, text);
        var sourceEntry = await sourceArchive.ArchiveAsync(draft, sourceStorage.GetDraftPath(draft)!);

        await sourceArchive.CopyEntryToAsync(sourceEntry, destinationArchive);
        Assert.Equal(sourceEntry, Assert.Single(await destinationArchive.GetEntriesAsync()));
        var reloadedSourceEntry = Assert.Single(await sourceArchive.GetEntriesAsync());
        await sourceArchive.CopyEntryToAsync(reloadedSourceEntry, destinationArchive);

        var retried = Assert.Single(await destinationArchive.GetEntriesAsync());
        Assert.Equal(sourceEntry, retried);
        Assert.Equal(sourceEntry, Assert.Single(await sourceArchive.GetEntriesAsync()));
        Assert.Equal(text, await destinationStorage.LoadDraftAsync(await destinationArchive.RestoreAsync(retried)));
        Assert.Equal(text, await sourceStorage.LoadDraftAsync(await sourceArchive.RestoreAsync(sourceEntry)));
        Assert.Empty(Directory.GetFiles(Path.Combine(destinationStorage.RootDirectory, "drafts-archive"), "copy-*.tmp"));
    }

    [Fact]
    public async Task CorruptArchiveLeavesEntryAvailableAndExpiryHonorsUnlimitedRetention()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var archive = new DraftArchiveService(storage.RootDirectory);
        var closed = await storage.SaveClosedDraftAsync(new SessionTab(7, null, TextEncodingKind.Utf8), "saved text");
        var entry = await archive.ArchiveAsync(closed, storage.GetDraftPath(closed)!);
        var path = Path.Combine(storage.RootDirectory, "drafts-archive", entry.Id + ".zip");
        await File.WriteAllTextAsync(path, "damaged");

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => archive.RestoreAsync(entry));
        Assert.Single(await archive.GetEntriesAsync());
        Assert.Equal(0, await archive.PruneExpiredAsync(0));
        Assert.Single(await archive.GetEntriesAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"entries\":[null]}")]
    public async Task InvalidArchiveIndexKeepsPendingRecoveryFiles(string invalid)
    {
        using var folder = new TempDirectory();
        var storage = StorageService.OpenForTesting(folder.Path);
        var archive = storage.DraftArchive;
        var draft = await storage.SaveClosedDraftAsync(new SessionTab(1, null, TextEncodingKind.Utf8), "saved");
        var entry = await archive.ArchiveAsync(draft, storage.GetDraftPath(draft)!);
        var directory = Path.Combine(storage.RootDirectory, "drafts-archive");
        var index = Path.Combine(directory, "index.json");
        var valid = await File.ReadAllTextAsync(index);
        var zip = Path.Combine(directory, entry.Id + ".zip");
        File.Move(zip, zip + ".delete");
        await File.WriteAllTextAsync(index, invalid);

        await Assert.ThrowsAsync<InvalidDataException>(() => archive.GetEntriesAsync());
        Assert.True(File.Exists(zip + ".delete"));
        Assert.Equal(invalid, await File.ReadAllTextAsync(index));
        await File.WriteAllTextAsync(index, valid);
        Assert.Equal(entry.Id, Assert.Single(await archive.GetEntriesAsync()).Id);
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task PruneExpiredRemovesOnlyExpiredArchiveFiles()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var archive = new DraftArchiveService(storage.RootDirectory);
        var old = await storage.SaveClosedDraftAsync(new SessionTab(3, null, TextEncodingKind.Utf8), "old");
        var recent = await storage.SaveClosedDraftAsync(new SessionTab(4, null, TextEncodingKind.Utf8), "recent");
        var oldEntry = await archive.ArchiveAsync(old, storage.GetDraftPath(old)!);
        var recentEntry = await archive.ArchiveAsync(recent, storage.GetDraftPath(recent)!);
        var entriesPath = Path.Combine(storage.RootDirectory, "drafts-archive", "index.json");
        var entries = await archive.GetEntriesAsync();
        var rewritten = entries.Select(entry => entry.Id == oldEntry.Id
            ? entry with { ArchivedAtMs = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds() }
            : entry).ToList();
        await File.WriteAllTextAsync(entriesPath, System.Text.Json.JsonSerializer.Serialize(new DraftArchiveIndex(rewritten),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }));

        Assert.Equal(1, await archive.PruneExpiredAsync(1));
        Assert.Equal(recentEntry.Id, Assert.Single(await archive.GetEntriesAsync()).Id);
        Assert.False(File.Exists(Path.Combine(storage.RootDirectory, "drafts-archive", oldEntry.Id + ".zip")));
        Assert.True(File.Exists(Path.Combine(storage.RootDirectory, "drafts-archive", recentEntry.Id + ".zip")));
    }

    [Fact]
    public async Task ConcurrentServicesShareTheArchiveIndexAndDoNotCreateDuplicateEntries()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var first = new DraftArchiveService(storage.RootDirectory);
        var second = new DraftArchiveService(storage.RootDirectory);
        var closed = await storage.SaveClosedDraftAsync(new SessionTab(8, null, TextEncodingKind.Utf8)
        {
            ClosedAtMs = DateTimeOffset.UtcNow.AddHours(-8).ToUnixTimeMilliseconds()
        }, "concurrent draft");

        var results = await Task.WhenAll(
            first.ArchiveAsync(closed, storage.GetDraftPath(closed)!),
            second.ArchiveAsync(closed, storage.GetDraftPath(closed)!));

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Single(await first.GetEntriesAsync());
    }

    [Fact]
    public async Task ReArchiveRepairsMissingOrCorruptArchiveWithoutRemovingThePlaintextSource()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var archive = new DraftArchiveService(storage.RootDirectory);
        var closed = await storage.SaveClosedDraftAsync(new SessionTab(9, null, TextEncodingKind.Utf8)
        {
            ClosedAtMs = DateTimeOffset.UtcNow.AddHours(-8).ToUnixTimeMilliseconds()
        }, "recoverable draft");
        var original = await archive.ArchiveAsync(closed, storage.GetDraftPath(closed)!);
        var path = Path.Combine(storage.RootDirectory, "drafts-archive", original.Id + ".zip");
        await File.WriteAllTextAsync(path, "corrupt");

        var repaired = await archive.ArchiveAsync(closed, storage.GetDraftPath(closed)!);

        Assert.NotEqual(original.Id, repaired.Id);
        Assert.Equal("recoverable draft", await storage.LoadDraftAsync(closed));
        Assert.Equal("recoverable draft", await storage.LoadDraftAsync(await archive.RestoreAsync(repaired)));
        Assert.Single(await archive.GetEntriesAsync());
    }

    [Fact]
    public async Task ArchiveChecksumFailureLeavesPlaintextAndIndexUntouched()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var archive = new DraftArchiveService(storage.RootDirectory);
        var closed = await storage.SaveClosedDraftAsync(new SessionTab(10, null, TextEncodingKind.Utf8), "original text");
        var source = storage.GetDraftPath(closed)!;
        await File.WriteAllTextAsync(source, "changed outside the application");

        await Assert.ThrowsAsync<InvalidDataException>(() => archive.ArchiveAsync(closed, source));

        Assert.Equal("changed outside the application", await File.ReadAllTextAsync(source));
        Assert.Empty(await archive.GetEntriesAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(storage.RootDirectory, "drafts-archive"), "*.zip"));
    }
}
