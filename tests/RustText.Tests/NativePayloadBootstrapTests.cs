using System.Security.Cryptography;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class NativePayloadBootstrapTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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
}
