using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class UpdaterTests
{
    private const string ArchiveUrl = "https://github.com/Illson/RustText/releases/download/v1.2.3/RustText.NET-win-x64.zip";
    private const string ChecksumUrl = ArchiveUrl + ".sha256";
    private static readonly string[] LegacyFiles =
    [
        "rusttext.exe",
        "libSkiaSharp.dll",
        "libHarfBuzzSharp.dll",
        "av_libglesv2.dll"
    ];

    [Fact]
    public async Task CheckLatestIgnoresRustOnlyRelease()
    {
        const string json = """
            {"draft":false,"prerelease":false,"tag_name":"v1.2.3","html_url":"https://github.com/Illson/RustText/releases/tag/v1.2.3","assets":[{"name":"RustText-x64.zip","browser_download_url":"https://github.com/Illson/RustText/releases/download/v1.2.3/RustText-x64.zip","size":123}]}
            """;
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
        }));

        Assert.Null(await UpdateService.CheckLatestAsync("1.0.0", client));
    }

    [Fact]
    public async Task SingleExeBundleStagesAndUpdatesWithoutDeletingLegacySidecars()
    {
        using var directory = new TempDirectory();
        var original = GetCurrentPeImage();
        var updated = MutateUnusedPayloadByte(original);
        var staged = await StagePackageAsync(directory.Path, original, updated, includeLegacyFiles: false);
        await using (staged.Update)
        {
            Assert.Equal(new[] { "rusttext.exe" }, Directory.GetFiles(staged.Update.BundleDirectory)
                .Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal));

            var oldSidecars = new Dictionary<string, byte[]>();
            foreach (var name in LegacyFiles.Skip(1))
            {
                var path = Path.Combine(directory.Path, name);
                var bytes = Encoding.UTF8.GetBytes("old sidecar " + name);
                await File.WriteAllBytesAsync(path, bytes);
                oldSidecars.Add(name, bytes);
            }

            var started = false;
            UpdateService.ApplyBundleTransactionForTesting(directory.Path, staged.Update.StagingDirectory,
                (exe, _) =>
                {
                    Assert.Equal(updated, File.ReadAllBytes(exe));
                    started = true;
                },
                (_, _) => throw new Xunit.Sdk.XunitException("Rollback must not run after successful startup."));

            Assert.True(started);
            foreach (var (name, bytes) in oldSidecars)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, name)));
            }
        }
    }

    [Fact]
    public async Task LegacyFourFileBundleAppliesAllFiles()
    {
        using var directory = new TempDirectory();
        var original = GetCurrentPeImage();
        var updated = MutateUnusedPayloadByte(original);
        var staged = await StagePackageAsync(directory.Path, original, updated, includeLegacyFiles: true);
        await using (staged.Update)
        {
            Assert.Equal(LegacyFiles.OrderBy(name => name, StringComparer.Ordinal),
                Directory.GetFiles(staged.Update.BundleDirectory).Select(path => Path.GetFileName(path)!)
                    .OrderBy(name => name, StringComparer.Ordinal));

            var verified = false;
            UpdateService.ApplyBundleTransactionForTesting(directory.Path, staged.Update.StagingDirectory,
                (exe, _) =>
                {
                    Assert.Equal(updated, File.ReadAllBytes(exe));
                    foreach (var name in LegacyFiles.Skip(1))
                    {
                        Assert.Equal(updated, File.ReadAllBytes(Path.Combine(directory.Path, name)));
                    }
                    verified = true;
                },
                (_, _) => throw new Xunit.Sdk.XunitException("Rollback must not run after successful startup."));
            Assert.True(verified);
        }
    }

    [Fact]
    public async Task FailedStartupRestoresPreviousExeAndInvokesRollbackRestart()
    {
        using var directory = new TempDirectory();
        var original = GetCurrentPeImage();
        var updated = MutateUnusedPayloadByte(original);
        var staged = await StagePackageAsync(directory.Path, original, updated, includeLegacyFiles: false);
        await using (staged.Update)
        {
            var restarted = false;
            var exception = Assert.Throws<IOException>(() => UpdateService.ApplyBundleTransactionForTesting(
                directory.Path,
                staged.Update.StagingDirectory,
                (_, _) => throw new IOException("simulated startup failure"),
                (exe, _) =>
                {
                    Assert.Equal(original, File.ReadAllBytes(exe));
                    restarted = true;
                }));

            Assert.Contains("previous bundle was restored", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(restarted);
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "rusttext.exe")));
        }
    }

    private static async Task<(StagedUpdate Update, byte[] Archive)> StagePackageAsync(
        string installDirectory,
        byte[] original,
        byte[] updated,
        bool includeLegacyFiles)
    {
        await File.WriteAllBytesAsync(Path.Combine(installDirectory, "rusttext.exe"), original);
        var archive = CreateArchive(updated, includeLegacyFiles);
        var digest = Convert.ToHexString(SHA256.HashData(archive));
        var checksum = Encoding.ASCII.GetBytes(digest + "  RustText.NET-win-x64.zip\n");
        using var client = new HttpClient(new FixtureHandler(uri =>
        {
            var body = uri.AbsoluteUri == ArchiveUrl ? archive : uri.AbsoluteUri == ChecksumUrl ? checksum : null;
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }));
        var release = new UpdateRelease("1.2.3", "v1.2.3",
            new Uri("https://github.com/Illson/RustText/releases/tag/v1.2.3"),
            new Uri(ArchiveUrl), new Uri(ChecksumUrl), archive.Length, digest);
        var update = await UpdateService.StageAsync(release, Path.Combine(installDirectory, "rusttext.exe"), client);
        return (update, archive);
    }

    private static byte[] CreateArchive(byte[] executable, bool includeLegacyFiles)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var names = includeLegacyFiles ? LegacyFiles : ["rusttext.exe"];
            foreach (var name in names)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(executable);
            }
        }

        return output.ToArray();
    }

    private static byte[] GetCurrentPeImage()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Current test process path is unavailable.");
        return File.ReadAllBytes(path);
    }

    private static byte[] MutateUnusedPayloadByte(byte[] image)
    {
        var result = image.ToArray();
        result[^1] ^= 0x5A;
        return result;
    }

    private sealed class FixtureHandler(Func<Uri, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request.RequestUri ?? throw new InvalidOperationException("Missing request URI.")));
        }
    }
}
