using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RustText.Demo;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class UpdaterTests
{
    [Fact]
    public async Task AutomaticUpdateChecksDefaultOnButHonorExplicitOptOutAndProfileOverride()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);

        var newProfile = await storage.LoadSettingsAsync();
        Assert.Null(newProfile.AutoCheckUpdates);
        Assert.True(MainWindow.ShouldRunAutomaticUpdateCheck(newProfile.AutoCheckUpdates, isWindows: true, profileOverride: null));
        Assert.False(MainWindow.ShouldRunAutomaticUpdateCheck(newProfile.AutoCheckUpdates, isWindows: false, profileOverride: null));
        Assert.False(MainWindow.ShouldRunAutomaticUpdateCheck(newProfile.AutoCheckUpdates, isWindows: true, profileOverride: "C:\\isolated"));

        await storage.SaveSettingsAsync(newProfile with { AutoCheckUpdates = false });
        var optedOut = await storage.LoadSettingsAsync();
        Assert.Equal(false, optedOut.AutoCheckUpdates);
        Assert.False(MainWindow.ShouldRunAutomaticUpdateCheck(optedOut.AutoCheckUpdates, isWindows: true, profileOverride: null));

        await storage.SaveSettingsAsync(optedOut with { AutoCheckUpdates = true });
        var optedIn = await storage.LoadSettingsAsync();
        Assert.Equal(true, optedIn.AutoCheckUpdates);
        Assert.True(MainWindow.ShouldRunAutomaticUpdateCheck(optedIn.AutoCheckUpdates, isWindows: true, profileOverride: null));
    }

    [Fact]
    public void AutomaticUpdateCheckCadenceIsOneHour() =>
        Assert.Equal(TimeSpan.FromHours(1), MainWindow.AutomaticUpdateCheckInterval);

    [Fact]
    public void DismissedBackgroundReleaseStaysHiddenButNewerReleaseIsPresented()
    {
        var dismissed = Release("2.1.0");
        Assert.False(MainWindow.ShouldPresentBackgroundUpdate(dismissed, "2.1.0"));
        Assert.True(MainWindow.ShouldPresentBackgroundUpdate(dismissed, null));

        var newer = Release("2.2.0");
        Assert.True(MainWindow.ShouldPresentBackgroundUpdate(newer, "2.1.0"));
        Assert.False(MainWindow.ShouldPresentBackgroundUpdate(null, "2.1.0"));
    }

    private static UpdateRelease Release(string version) => new(
        version,
        $"v{version}",
        new Uri($"https://github.com/Illson/RustText/releases/tag/v{version}"),
        new Uri($"https://github.com/Illson/RustText/releases/download/v{version}/RustText.NET-win-x64.zip"),
        new Uri($"https://github.com/Illson/RustText/releases/download/v{version}/RustText.NET-win-x64.zip.sha256"),
        1,
        null,
        null);

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
    public async Task CheckLatestReturnsGitHubReleaseDescription()
    {
        const string json = """
            {"draft":false,"prerelease":false,"tag_name":"v1.2.3","html_url":"https://github.com/Illson/RustText/releases/tag/v1.2.3","body":"## Исправлено\n\n- Улучшено обновление.","assets":[{"name":"RustText.NET-win-x64.zip","browser_download_url":"https://github.com/Illson/RustText/releases/download/v1.2.3/RustText.NET-win-x64.zip","size":123},{"name":"RustText.NET-win-x64.zip.sha256","browser_download_url":"https://github.com/Illson/RustText/releases/download/v1.2.3/RustText.NET-win-x64.zip.sha256","size":64}]}
            """;
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
        }));

        var release = await UpdateService.CheckLatestAsync("1.0.0", client);

        Assert.NotNull(release);
        Assert.Equal("## Исправлено\n\n- Улучшено обновление.", release.Notes);
    }

    [Fact]
    public async Task GetReleaseHistoryIncludesStableRustAndDotNetReleasesAcrossPages()
    {
        var requestedPages = new List<int>();
        using var client = new HttpClient(new FixtureHandler(uri =>
        {
            var pageParameter = uri.Query.TrimStart('?').Split('&')
                .Single(part => part.StartsWith("page=", StringComparison.Ordinal));
            var page = int.Parse(pageParameter["page=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            requestedPages.Add(page);
            var json = page == 1
                ? """
                    [
                      {"draft":false,"prerelease":false,"tag_name":"v2.0.0","html_url":"https://github.com/Illson/RustText/releases/tag/v2.0.0","body":"Первый выпуск .NET."},
                      {"draft":false,"prerelease":false,"tag_name":"v1.0.0","html_url":"https://github.com/Illson/RustText/releases/tag/v1.0.0","body":"Первый выпуск Rust."},
                      {"draft":true,"prerelease":false,"tag_name":"v9.0.0","html_url":"https://github.com/Illson/RustText/releases/tag/v9.0.0","body":"Черновик."},
                      {"draft":false,"prerelease":true,"tag_name":"v8.0.0","html_url":"https://github.com/Illson/RustText/releases/tag/v8.0.0","body":"Предварительная версия."},
                      {"draft":false,"prerelease":false,"tag_name":"v3.0.0-rc.1","html_url":"https://github.com/Illson/RustText/releases/tag/v3.0.0-rc.1","body":"Не стабильная версия."},
                      {"draft":false,"prerelease":false,"tag_name":"nightly","html_url":"https://github.com/Illson/RustText/releases/tag/nightly","body":"Сборка разработки."}
                    ]
                    """
                : "[]";
            if (page == 1)
            {
                var ignoredReleases = string.Join(",", Enumerable.Repeat(
                    """{"draft":true,"prerelease":false,"tag_name":"v9.0.0","html_url":"https://github.com/Illson/RustText/releases/tag/v9.0.0","body":"Черновик."}""",
                    100 - 6));
                json = json.Insert(json.LastIndexOf(']'), "," + ignoredReleases);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
            };
        }));

        var history = await UpdateService.GetReleaseHistoryAsync(client);

        Assert.Equal(new[] { 1, 2 }, requestedPages);
        Assert.Collection(history,
            release =>
            {
                Assert.Equal("2.0.0", release.Version);
                Assert.Equal("Первый выпуск .NET.", release.Body);
                Assert.Equal("https://github.com/Illson/RustText/releases/tag/v2.0.0", release.PageUri.AbsoluteUri);
            },
            release =>
            {
                Assert.Equal("1.0.0", release.Version);
                Assert.Equal("Первый выпуск Rust.", release.Body);
            });
    }

    [Fact]
    public async Task GetReleaseHistoryRejectsReleasePageOutsideRustTextRepository()
    {
        const string json = """
            [{"draft":false,"prerelease":false,"tag_name":"v1.0.0","html_url":"https://example.com/releases/v1.0.0","body":"Notes"}]
            """;
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.GetReleaseHistoryAsync(client));
    }

    [Fact]
    public void LegacyRustCleanupRemovesOnlyKnownFilesAndLeavesUnrelatedContents()
    {
        using var install = new TempDirectory();
        var stage = Path.Combine(install.Path, ".rusttext-update-test");
        Directory.CreateDirectory(stage);
        foreach (var name in new[] { "replacement.exe", ".rusttext-previous.exe", ".helper-ready", "rusttext-updater.exe" })
            File.WriteAllText(Path.Combine(stage, name), name);
        var unrelated = Path.Combine(stage, "user-data.bin");
        File.WriteAllText(unrelated, "keep");

        UpdateService.CleanupLegacyRustStagingDirectory(install.Path, stage);

        Assert.True(Directory.Exists(stage));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Equal("user-data.bin", Path.GetFileName(Assert.Single(Directory.GetFiles(stage))));
    }

    [Fact]
    public void LegacyRustCleanupRemovesOwnedStageWhenItContainsOnlyKnownFiles()
    {
        using var install = new TempDirectory();
        var stage = Path.Combine(install.Path, ".rusttext-update-test");
        Directory.CreateDirectory(stage);
        foreach (var name in new[] { "replacement.exe", ".rusttext-previous.exe", ".helper-ready", "rusttext-updater.exe" })
            File.WriteAllText(Path.Combine(stage, name), name);

        UpdateService.CleanupLegacyRustStagingDirectory(install.Path, stage);

        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public void LegacyRustCleanupAcceptsExtendedWin32Paths()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var install = new TempDirectory();
        var normalStage = Path.Combine(install.Path, ".rusttext-update-test");
        Directory.CreateDirectory(normalStage);
        File.WriteAllText(Path.Combine(normalStage, "replacement.exe"), "temporary");
        var extendedInstall = @"\\?\" + Path.GetFullPath(install.Path);
        var extendedStage = @"\\?\" + Path.GetFullPath(normalStage);

        Assert.True(UpdateService.IsLegacyRustStagingDirectory(extendedInstall, extendedStage));
        UpdateService.CleanupLegacyRustStagingDirectory(extendedInstall, extendedStage);

        Assert.False(Directory.Exists(normalStage));
    }

    [Fact]
    public void LegacyRustCleanupRejectsOutsideAndUnexpectedDirectories()
    {
        using var install = new TempDirectory();
        using var outside = new TempDirectory();
        var outsideStage = Path.Combine(outside.Path, ".rusttext-update-outside");
        var unexpectedStage = Path.Combine(install.Path, "unrelated-update");
        Directory.CreateDirectory(outsideStage);
        Directory.CreateDirectory(unexpectedStage);
        var outsideFile = Path.Combine(outsideStage, "replacement.exe");
        var unexpectedFile = Path.Combine(unexpectedStage, "replacement.exe");
        File.WriteAllText(outsideFile, "keep");
        File.WriteAllText(unexpectedFile, "keep");

        UpdateService.CleanupLegacyRustStagingDirectory(install.Path, outsideStage);
        UpdateService.CleanupLegacyRustStagingDirectory(install.Path, unexpectedStage);
        Assert.False(UpdateService.IsLegacyRustStagingDirectory(install.Path, Path.GetFileName(unexpectedStage)));

        Assert.Equal("keep", File.ReadAllText(outsideFile));
        Assert.Equal("keep", File.ReadAllText(unexpectedFile));
    }

    [Fact]
    public void LegacyRustCleanupDoesNotFollowFileLinks()
    {
        using var install = new TempDirectory();
        using var destination = new TempDirectory();
        var stage = Path.Combine(install.Path, ".rusttext-update-test");
        Directory.CreateDirectory(stage);
        var destinationFile = Path.Combine(destination.Path, "preserve.bin");
        File.WriteAllText(destinationFile, "preserve");
        var link = Path.Combine(stage, "replacement.exe");
        try
        {
            File.CreateSymbolicLink(link, destinationFile);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        UpdateService.CleanupLegacyRustStagingDirectory(install.Path, stage);

        Assert.True(File.Exists(link));
        Assert.Equal("preserve", File.ReadAllText(destinationFile));
        Assert.True(Directory.Exists(stage));
    }

    [Fact]
    public void LegacyRustCleanupRejectsReparsePointStagingDirectory()
    {
        using var install = new TempDirectory();
        using var destination = new TempDirectory();
        var destinationFile = Path.Combine(destination.Path, "replacement.exe");
        File.WriteAllText(destinationFile, "preserve");
        var link = Path.Combine(install.Path, ".rusttext-update-link");
        try
        {
            Directory.CreateSymbolicLink(link, destination.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        UpdateService.CleanupLegacyRustStagingDirectory(install.Path, link);

        Assert.True(Directory.Exists(link));
        Assert.Equal("preserve", File.ReadAllText(destinationFile));
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
