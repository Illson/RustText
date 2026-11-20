using System.Security.Cryptography;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class NativePayloadBootstrapTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly string[] PayloadNames = ["libHarfBuzzSharp.dll", "libSkiaSharp.dll", "av_libglesv2.dll"];

    [Fact]
    public void ParsePayloadHashesAcceptsEntriesInAnyOrder()
    {
        using var reader = new StringReader(
            $"av_libglesv2.dll {Hash}\nlibSkiaSharp.dll {Hash}\nlibHarfBuzzSharp.dll {Hash}\n");

        var result = NativePayloadBootstrap.ParsePayloadHashes(reader);

        Assert.Equal(3, result.Count);
        Assert.Equal(Convert.FromHexString(Hash), result["libHarfBuzzSharp.dll"]);
        Assert.Equal(Convert.FromHexString(Hash), result["libSkiaSharp.dll"]);
        Assert.Equal(Convert.FromHexString(Hash), result["av_libglesv2.dll"]);
    }

    [Theory]
    [InlineData("unlisted.dll")]
    [InlineData("libSkiaSharp.dll")]
    public void ParsePayloadHashesRejectsUnknownAndRepeatedNames(string invalidName)
    {
        var duplicateOrUnknown = invalidName == "unlisted.dll"
            ? $"libHarfBuzzSharp.dll {Hash}\nlibSkiaSharp.dll {Hash}\nav_libglesv2.dll {Hash}\n{invalidName} {Hash}\n"
            : $"libHarfBuzzSharp.dll {Hash}\n{invalidName} {Hash}\nav_libglesv2.dll {Hash}\n{invalidName} {Hash}\n";

        Assert.Throws<InvalidDataException>(() => NativePayloadBootstrap.ParsePayloadHashes(new StringReader(duplicateOrUnknown)));
    }

    [Fact]
    public void HashMatchesRejectsPayloadWhenSha256Differs()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "payload.dll");
        var bytes = "actual payload"u8.ToArray();
        File.WriteAllBytes(path, bytes);

        Assert.True(NativePayloadBootstrap.HashMatches(path, SHA256.HashData(bytes)));
        Assert.False(NativePayloadBootstrap.HashMatches(path, SHA256.HashData("different payload"u8)));
    }

    [Fact]
    public void IsCompleteAcceptsAValidCacheContainingAllPayloads()
    {
        using var directory = new TempDirectory();
        var hashes = WritePayloads(directory.Path);

        Assert.True(NativePayloadBootstrap.IsComplete(directory.Path, hashes));
    }

    [Fact]
    public void IsCompleteRejectsACacheWithOneCorruptedPayload()
    {
        using var directory = new TempDirectory();
        var hashes = WritePayloads(directory.Path);
        File.WriteAllBytes(Path.Combine(directory.Path, "libSkiaSharp.dll"), "damaged"u8.ToArray());

        Assert.False(NativePayloadBootstrap.IsComplete(directory.Path, hashes));
    }

    [Fact]
    public void IsCompleteRethrowsOriginalFileErrorInsteadOfAggregateException()
    {
        using var directory = new TempDirectory();
        var hashes = WritePayloads(directory.Path);
        using var lockedFile = new FileStream(Path.Combine(directory.Path, "libSkiaSharp.dll"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        var error = Record.Exception(() => NativePayloadBootstrap.IsComplete(directory.Path, hashes));

        Assert.IsAssignableFrom<IOException>(error);
        Assert.IsNotType<AggregateException>(error);
    }

    [Fact]
    public void IsCompletePreservesFirstFileErrorWhenLastPayloadIsMissing()
    {
        using var directory = new TempDirectory();
        var hashes = WritePayloads(directory.Path);
        using var lockedFile = new FileStream(Path.Combine(directory.Path, "libHarfBuzzSharp.dll"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        File.Delete(Path.Combine(directory.Path, "av_libglesv2.dll"));

        var error = Record.Exception(() => NativePayloadBootstrap.IsComplete(directory.Path, hashes));

        Assert.IsAssignableFrom<IOException>(error);
        Assert.IsNotType<AggregateException>(error);
    }

    [Fact]
    public void IsCompleteReturnsForEarlierCorruptionBeforeLaterFileError()
    {
        using var directory = new TempDirectory();
        var hashes = WritePayloads(directory.Path);
        File.WriteAllBytes(Path.Combine(directory.Path, "libHarfBuzzSharp.dll"), "damaged"u8.ToArray());
        using var lockedFile = new FileStream(Path.Combine(directory.Path, "libSkiaSharp.dll"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(NativePayloadBootstrap.IsComplete(directory.Path, hashes));
    }

    private static Dictionary<string, byte[]> WritePayloads(string directory)
    {
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < PayloadNames.Length; index++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"synthetic native payload {index}");
            File.WriteAllBytes(Path.Combine(directory, PayloadNames[index]), bytes);
            hashes.Add(PayloadNames[index], SHA256.HashData(bytes));
        }
        return hashes;
    }
}
