using System.Security.Cryptography;
using System.Text;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

public sealed class ConcurrentSaveTests
{
    [Fact]
    public async Task ConcurrentSavesWithSameStamp_OnlyOneCommitsAndReturnsItsPayloadStamp()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "shared.txt");
        await File.WriteAllTextAsync(path, new string('o', 6 * 1024 * 1024));
        var expected = await DocumentFiles.GetStampAsync(path);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, 4).Select(i => SaveAsync(new string((char)('A' + i), 128 * 1024))).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(writes);

        var saved = Assert.Single(results, result => result.Stamp is not null);
        Assert.All(results.Where(result => result.Stamp is null), result => Assert.IsAssignableFrom<IOException>(result.Error));
        Assert.Equal(Encoding.UTF8.GetByteCount(saved.Text), saved.Stamp!.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(saved.Text))), saved.Stamp.Sha256);
        Assert.Equal(saved.Stamp, await DocumentFiles.GetStampAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(directory.Path));

        async Task<(string Text, FileStamp? Stamp, Exception? Error)> SaveAsync(string text)
        {
            try
            {
                await start.Task;
                var stamp = await DocumentFiles.SaveAsync(path, text, TextEncodingKind.Utf8,
                    LineEndingKind.Lf, expected);
                return (text, stamp, null);
            }
            catch (IOException error)
            {
                return (text, null, error);
            }
        }
    }

    [Fact]
    public async Task ConcurrentSaveAsCalls_DoesNotReplaceTheFirstCreatedFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "new.txt");
        var firstText = new string('x', 1024 * 1024);
        var secondText = new string('y', 1024 * 1024);

        var results = await Task.WhenAll(
            TrySaveAsAsync(firstText),
            TrySaveAsAsync(secondText));

        var saved = Assert.Single(results, result => result.Stamp is not null);
        var rejected = Assert.Single(results, result => result.Error is not null);
        Assert.IsType<IOException>(rejected.Error);
        Assert.Equal(saved.Text, await File.ReadAllTextAsync(path));
        Assert.Equal(saved.Stamp, await DocumentFiles.GetStampAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".new.txt.*.pending"));

        async Task<(string Text, FileStamp? Stamp, Exception? Error)> TrySaveAsAsync(string text)
        {
            try
            {
                var stamp = await DocumentFiles.SaveAsync(path, text, TextEncodingKind.Utf8,
                    LineEndingKind.Lf, expectedStamp: null);
                return (text, stamp, null);
            }
            catch (IOException error)
            {
                return (text, null, error);
            }
        }
    }

    [Fact]
    public async Task CancelledSave_LeavesOriginalFileAndNoPendingFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "cancelled.txt");
        await File.WriteAllTextAsync(path, "keep this");
        var original = await File.ReadAllBytesAsync(path);
        var expected = await DocumentFiles.GetStampAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentFiles.SaveAsync(path,
            "replacement", TextEncodingKind.Utf8, LineEndingKind.Lf,
            expected, cancellationToken: cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".cancelled.txt.*.pending"));
    }

    [Fact]
    public async Task ReplacementThatFindsANewerFileKeepsTheDisplacedBytes()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "note.txt");
        var staged = Path.Combine(directory.Path, "staged.tmp");
        await File.WriteAllTextAsync(path, "original");
        await File.WriteAllTextAsync(staged, "editor edit");
        using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        File.Move(path, Path.Combine(directory.Path, "old.txt"));
        await File.WriteAllTextAsync(path, "external edit");

        var error = Assert.Throws<IOException>(() => DocumentFiles.PublishReplacement(staged, path,
            backup => File.ReadAllText(backup) == "original"));

        var savedExternal = Assert.Single(Directory.GetFiles(directory.Path, ".rusttext-replaced-*.bak"));
        Assert.Contains(savedExternal, error.Message);
        Assert.Equal("external edit", await File.ReadAllTextAsync(savedExternal));
        Assert.Equal("editor edit", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FailedSaveWithStaleStampLeavesExternalContentAndAllowsRetry()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "retry.txt");
        await File.WriteAllTextAsync(path, "original");
        var stale = await DocumentFiles.GetStampAsync(path);
        await File.WriteAllTextAsync(path, "external update");

        await Assert.ThrowsAsync<IOException>(() => DocumentFiles.SaveAsync(path, "editor text",
            TextEncodingKind.Utf8, LineEndingKind.Lf, stale));

        Assert.Equal("external update", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".retry.txt.*.pending"));

        var current = await DocumentFiles.GetStampAsync(path);
        var saved = await DocumentFiles.SaveAsync(path, "editor text", TextEncodingKind.Utf8,
            LineEndingKind.Lf, current);

        Assert.Equal("editor text", await File.ReadAllTextAsync(path));
        Assert.Equal(saved.Sha256, (await DocumentFiles.GetStampAsync(path)).Sha256);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".retry.txt.*.pending"));
    }
}
