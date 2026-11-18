using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustText.Demo.Platform;

/// <summary>
/// Handles only the versioned .NET bundle protocol. Nothing contacts the network until
/// CheckLatestAsync or StageAsync is explicitly called by the application.
/// </summary>
public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/Illson/RustText/releases/latest";
    private const string ReleasePagePrefix = "https://github.com/Illson/RustText/releases/tag/";
    private const string AssetPrefix = "https://github.com/Illson/RustText/releases/download/";
    private const string ArchiveName = "RustText.NET-win-x64.zip";
    private const string ChecksumName = ArchiveName + ".sha256";
    private const string HelperSwitch = "--rusttext-apply-dotnet-bundle";
    private const string StageDirectoryPrefix = ".rusttext-dotnet-update-";
    private const string CleanupPidVariable = "RUSTTEXT_DOTNET_UPDATE_HELPER_PID";
    private const string CleanupDirectoryVariable = "RUSTTEXT_DOTNET_UPDATE_STAGE_DIRECTORY";
    private const int MaximumReleaseJsonBytes = 256 * 1024;
    private const int MaximumArchiveBytes = 128 * 1024 * 1024;
    private const int MaximumChecksumBytes = 512;
    private const int MaximumArchiveEntries = 64;
    private const long MaximumBundleFileBytes = 256L * 1024 * 1024;
    private const long MaximumBundleBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan ParentWaitLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReadyWaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromMilliseconds(1500);

    private static readonly string[] BundleNames =
    [
        "rusttext.exe",
        "libSkiaSharp.dll",
        "libHarfBuzzSharp.dll",
        "av_libglesv2.dll"
    ];

    private static readonly Regex StableVersionPattern = new(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ChecksumPattern = new(
        @"^\s*([0-9a-fA-F]{64})\s+\*?RustText\.NET-win-x64\.zip\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Checks the latest stable .NET bundle release. This method is never called automatically.</summary>
    public static async Task<UpdateRelease?> CheckLatestAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var current = ParseVersion(currentVersion, allowTag: false);
        using var client = CreateClient(TimeSpan.FromSeconds(20));
        var jsonBytes = await GetLimitedBytesAsync(client, new Uri(ApiUrl), MaximumReleaseJsonBytes, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions { MaxDepth = 24 });
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
        {
            return null;
        }

        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("The release has no tag.");
        var version = ParseVersion(tag, allowTag: true);
        var pageUri = new Uri(root.GetProperty("html_url").GetString() ?? string.Empty);
        if (pageUri.AbsoluteUri != ReleasePagePrefix + tag)
        {
            throw new InvalidDataException("The release page points outside the RustText repository.");
        }

        if (CompareVersions(version, current) <= 0)
        {
            return null;
        }

        var expectedArchive = new Uri($"{AssetPrefix}{tag}/{ArchiveName}");
        var expectedChecksum = new Uri($"{expectedArchive.AbsoluteUri}.sha256");
        var archive = FindAsset(root.GetProperty("assets"), ArchiveName);
        var checksum = FindAsset(root.GetProperty("assets"), ChecksumName);
        if (archive.Uri != expectedArchive || archive.Size is <= 0 or > MaximumArchiveBytes)
        {
            throw new InvalidDataException("The .NET update archive has an invalid URL or size.");
        }

        if (checksum.Uri != expectedChecksum || checksum.Size is <= 0 or > MaximumChecksumBytes)
        {
            throw new InvalidDataException("The .NET update checksum has an invalid URL or size.");
        }

        var apiDigest = archive.Digest is null ? null : ParseApiDigest(archive.Digest);
        return new UpdateRelease(version, tag, pageUri, archive.Uri, checksum.Uri, archive.Size, apiDigest);
    }

    /// <summary>Downloads and stages the verified four-file Native AOT bundle beside the installed executable.</summary>
    public static async Task<StagedUpdate> StageAsync(
        UpdateRelease release,
        string currentExecutable,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(TimeSpan.FromMinutes(2));
        return await StageAsync(release, currentExecutable, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uses a caller-owned HTTP client; internal so release verification can use an offline fixture handler.</summary>
    internal static async Task<StagedUpdate> StageAsync(
        UpdateRelease release,
        string currentExecutable,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentExecutable);
        ArgumentNullException.ThrowIfNull(client);
        ValidateRelease(release);

        var executablePath = Path.GetFullPath(currentExecutable);
        if (!Path.GetFileName(executablePath).Equals("rusttext.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The .NET update protocol expects the installed executable to be named rusttext.exe.");
        }

        var installDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidDataException("The installed executable has no parent directory.");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The installed RustText executable was not found.", executablePath);
        }

        var stagingDirectory = Path.Combine(installDirectory, StageDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var archivePath = Path.Combine(stagingDirectory, "download.zip");
            var downloadedSize = await DownloadToFileAsync(client, release.ArchiveUri, archivePath,
                MaximumArchiveBytes, cancellationToken).ConfigureAwait(false);
            if (downloadedSize != release.ArchiveSize)
            {
                throw new InvalidDataException("The downloaded .NET archive size differs from the release metadata.");
            }

            var archiveHash = await HashFileAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (release.ApiSha256 is not null && !archiveHash.Equals(release.ApiSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The .NET archive hash differs from the GitHub release digest.");
            }

            var checksumBytes = await GetLimitedBytesAsync(client, release.ChecksumUri, MaximumChecksumBytes, cancellationToken)
                .ConfigureAwait(false);
            var checksumText = Encoding.UTF8.GetString(checksumBytes);
            var checksumMatch = ChecksumPattern.Match(checksumText);
            if (!checksumMatch.Success || !archiveHash.Equals(checksumMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The .NET archive SHA-256 does not match its checksum file.");
            }

            var bundleDirectory = Path.Combine(stagingDirectory, "bundle");
            Directory.CreateDirectory(bundleDirectory);
            var bundleFiles = await ExtractAndValidateBundleAsync(archivePath, bundleDirectory, cancellationToken)
                .ConfigureAwait(false);
            File.Delete(archivePath);

            var manifest = new BundleManifest(release.Version, archiveHash, bundleFiles);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BundleManifestJsonContext.Default.BundleManifest);
            await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "bundle-manifest.json"), manifestBytes, cancellationToken)
                .ConfigureAwait(false);
            return new StagedUpdate(release.Version, installDirectory, stagingDirectory, archiveHash);
        }
        catch
        {
            DeleteOwnedStagingDirectory(installDirectory, stagingDirectory);
            throw;
        }
    }

    /// <summary>
    /// Starts a copied companion instance and waits until it has validated its arguments and acquired the parent handle.
    /// The caller can then finish session saving and close the main application.
    /// </summary>
    public static async Task ApplyStagedAsync(
        StagedUpdate stagedUpdate,
        string currentExecutable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedUpdate);
        var executablePath = Path.GetFullPath(currentExecutable);
        var installDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidDataException("The installed executable has no parent directory.");
        ValidateStagedUpdate(stagedUpdate, executablePath, installDirectory);
        VerifyBundleManifest(stagedUpdate.BundleDirectory, stagedUpdate.Version, stagedUpdate.ArchiveSha256);

        var helperDirectory = Path.Combine(stagedUpdate.StagingDirectory, "helper");
        Directory.CreateDirectory(helperDirectory);
        foreach (var name in BundleNames)
        {
            var source = Path.Combine(installDirectory, name);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"The installed .NET bundle is missing {name}.", source);
            }

            File.Copy(source, Path.Combine(helperDirectory, name), overwrite: false);
        }

        var readyPath = Path.Combine(stagedUpdate.StagingDirectory, ".helper-ready");
        var errorPath = Path.Combine(stagedUpdate.StagingDirectory, ".helper-error");
        var parent = Process.GetCurrentProcess();
        var parentStartTicks = parent.StartTime.ToUniversalTime().Ticks;
        var helper = Process.Start(new ProcessStartInfo(Path.Combine(helperDirectory, "rusttext.exe"))
        {
            WorkingDirectory = helperDirectory,
            UseShellExecute = false,
            ArgumentList =
            {
                HelperSwitch,
                readyPath,
                installDirectory,
                stagedUpdate.StagingDirectory,
                parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                parentStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        }) ?? throw new IOException("Could not start the .NET update helper.");

        using (helper)
        {
            var deadline = DateTime.UtcNow + ReadyWaitLimit;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(errorPath))
                    {
                        throw new IOException(await File.ReadAllTextAsync(errorPath, cancellationToken).ConfigureAwait(false));
                    }

                    if (File.Exists(readyPath))
                    {
                        stagedUpdate.ApplyStarted = true;
                        return;
                    }

                    if (helper.HasExited)
                    {
                        throw new IOException($"The update helper exited before becoming ready with code {helper.ExitCode}.");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false);
                }

                throw new TimeoutException("The .NET update helper did not become ready in time.");
            }
            catch
            {
                if (!helper.HasExited)
                {
                    helper.Kill();
                    await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    /// <summary>Handles the companion mode before Avalonia starts. Returns null for ordinary application startup.</summary>
    public static int? TryHandleApplyArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        StartStageCleanupFromEnvironment();
        if (args.Length == 0 || !args[0].Equals(HelperSwitch, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            ApplyFromHelperArguments(args);
            return 0;
        }
        catch (Exception exception)
        {
            TryWriteHelperError(args, exception.Message);
            return 1;
        }
    }

    internal static bool IsOwnedStagingDirectory(string installDirectory, string stagingDirectory)
    {
        var install = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stage = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetDirectoryName(stage)?.Equals(install, StringComparison.OrdinalIgnoreCase) == true &&
            Path.GetFileName(stage).StartsWith(StageDirectoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static void DeleteOwnedStagingDirectory(string installDirectory, string stagingDirectory)
    {
        if (IsOwnedStagingDirectory(installDirectory, stagingDirectory) && Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RustText.NET", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static async Task<byte[]> GetLimitedBytesAsync(
        HttpClient client,
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingAllowedRedirectsAsync(client, uri, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The update response exceeds its size limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static async Task<long> DownloadToFileAsync(
        HttpClient client,
        Uri uri,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingAllowedRedirectsAsync(client, uri, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("The update archive exceeds its size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return total;
    }

    private static async Task<HttpResponseMessage> SendFollowingAllowedRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedInitialUri(initialUri))
        {
            throw new InvalidDataException("The update URL is outside the RustText GitHub release endpoints.");
        }

        var current = initialUri;
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    throw new InvalidDataException("GitHub returned a redirect without a location.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsAllowedRedirect(current))
                {
                    throw new InvalidDataException("GitHub redirected the update download outside the allowed hosts.");
                }

                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"GitHub returned HTTP {(int)status} ({status}).");
            }

            if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            {
                response.Dispose();
                throw new InvalidDataException("The update response exceeds its declared size limit.");
            }

            return response;
        }

        throw new InvalidDataException("GitHub exceeded the allowed redirect count.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<BundleFile[]> ExtractAndValidateBundleAsync(
        string archivePath,
        string bundleDirectory,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != BundleNames.Length || archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The .NET update archive must contain exactly the four required bundle files.");
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BundleFile>(BundleNames.Length);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (entry.Name.Length == 0 || name != entry.Name || name.Contains('/') || name.Contains('\\') ||
                !BundleNames.Contains(name, StringComparer.OrdinalIgnoreCase) || !found.Add(name) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                entry.Length <= 0 || entry.Length > MaximumBundleFileBytes)
            {
                throw new InvalidDataException("The .NET update archive contains an unexpected or unsafe entry.");
            }

            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaximumBundleBytes)
            {
                throw new InvalidDataException("The extracted .NET bundle exceeds its size limit.");
            }

            var destination = Path.Combine(bundleDirectory, name);
            var copied = await ExtractEntryAsync(entry, destination, cancellationToken).ConfigureAwait(false);
            if (copied != entry.Length)
            {
                throw new InvalidDataException($"The extracted file size is invalid for {name}.");
            }

            VerifyPeX64(destination);

            result.Add(new BundleFile(name, copied, await HashFileAsync(destination, cancellationToken).ConfigureAwait(false)));
        }

        if (found.Count != BundleNames.Length || !found.Contains("rusttext.exe"))
        {
            throw new InvalidDataException("The .NET update archive is missing a required bundle file.");
        }

        VerifyPeX64(Path.Combine(bundleDirectory, "rusttext.exe"));
        return result.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<long> ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[256 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > entry.Length || total > MaximumBundleFileBytes)
            {
                throw new InvalidDataException("An extracted update file exceeded its declared size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return total;
    }

    private static void VerifyPeX64(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 90)
        {
            throw new InvalidDataException("The .NET executable is too small to be a Windows PE image.");
        }

        Span<byte> dosHeader = stackalloc byte[64];
        stream.ReadExactly(dosHeader);
        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            throw new InvalidDataException("The .NET executable is not a Windows PE image.");
        }

        var peOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dosHeader[60..64]);
        if (peOffset > 1024 * 1024 || peOffset + 26 > stream.Length)
        {
            throw new InvalidDataException("The .NET executable has an invalid PE header offset.");
        }

        stream.Position = peOffset;
        Span<byte> header = stackalloc byte[26];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("PE\0\0"u8) ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]) != 0x8664 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[24..26]) != 0x20b)
        {
            throw new InvalidDataException("The .NET executable is not a Windows x64 PE32+ image.");
        }
    }

    private static (Uri Uri, long Size, string? Digest) FindAsset(JsonElement assets, string expectedName)
    {
        JsonElement match = default;
        var found = false;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) ||
                !string.Equals(name.GetString(), expectedName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                throw new InvalidDataException($"The release contains duplicate {expectedName} assets.");
            }

            match = asset;
            found = true;
        }

        if (!found)
        {
            throw new InvalidDataException($"The release does not contain {expectedName}.");
        }

        var url = new Uri(match.GetProperty("browser_download_url").GetString() ?? string.Empty);
        var size = match.GetProperty("size").GetInt64();
        var digest = match.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String
            ? digestElement.GetString()
            : null;
        return (url, size, digest);
    }

    private static void ValidateRelease(UpdateRelease release)
    {
        var version = ParseVersion(release.Version, allowTag: false);
        var expectedTag = "v" + version;
        if (release.TagName != expectedTag || release.ReleasePageUri.AbsoluteUri != ReleasePagePrefix + expectedTag ||
            release.ArchiveUri.AbsoluteUri != $"{AssetPrefix}{expectedTag}/{ArchiveName}" ||
            release.ChecksumUri.AbsoluteUri != $"{AssetPrefix}{expectedTag}/{ChecksumName}" ||
            release.ArchiveSize is <= 0 or > MaximumArchiveBytes ||
            release.ApiSha256 is not null && !IsSha256(release.ApiSha256))
        {
            throw new InvalidDataException("The update release metadata failed validation.");
        }
    }

    private static string ParseVersion(string value, bool allowTag)
    {
        var parsed = allowTag && value.StartsWith('v') ? value : "v" + value;
        if (!StableVersionPattern.IsMatch(parsed))
        {
            throw new InvalidDataException("The update version is not a stable three-part semantic version.");
        }

        return parsed[1..];
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(uint.Parse).ToArray();
        var rightParts = right.Split('.').Select(uint.Parse).ToArray();
        for (var index = 0; index < 3; index++)
        {
            var comparison = leftParts[index].CompareTo(rightParts[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static string ParseApiDigest(string digest)
    {
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..]))
        {
            throw new InvalidDataException("GitHub returned an invalid archive digest.");
        }

        return digest[7..].ToUpperInvariant();
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsAllowedInitialUri(Uri uri) =>
        uri.AbsoluteUri == ApiUrl ||
        uri.AbsoluteUri.StartsWith(AssetPrefix, StringComparison.Ordinal) &&
        (uri.AbsoluteUri.EndsWith("/" + ArchiveName, StringComparison.Ordinal) ||
         uri.AbsoluteUri.EndsWith("/" + ChecksumName, StringComparison.Ordinal));

    private static bool IsAllowedRedirect(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
        new[]
        {
            "api.github.com",
            "github.com",
            "release-assets.githubusercontent.com",
            "objects.githubusercontent.com"
        }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    private static void ValidateStagedUpdate(StagedUpdate staged, string executablePath, string installDirectory)
    {
        if (!staged.InstallDirectory.Equals(installDirectory, StringComparison.OrdinalIgnoreCase) ||
            !IsOwnedStagingDirectory(installDirectory, staged.StagingDirectory) ||
            Path.GetFileName(executablePath).Equals("rusttext.exe", StringComparison.OrdinalIgnoreCase) is false ||
            !IsSha256(staged.ArchiveSha256))
        {
            throw new InvalidDataException("The staged .NET update does not belong to this installation.");
        }
    }

    private static void VerifyBundleManifest(string bundleDirectory, string version, string archiveSha256)
    {
        var stageDirectory = Directory.GetParent(bundleDirectory)?.FullName
            ?? throw new InvalidDataException("The staged bundle has no containing directory.");
        var manifestPath = Path.Combine(stageDirectory, "bundle-manifest.json");
        var bytes = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize(bytes, BundleManifestJsonContext.Default.BundleManifest)
            ?? throw new InvalidDataException("The staged bundle manifest is invalid.");
        if (manifest.Version != version || !manifest.ArchiveSha256.Equals(archiveSha256, StringComparison.OrdinalIgnoreCase) ||
            manifest.Files.Length != BundleNames.Length)
        {
            throw new InvalidDataException("The staged bundle manifest does not match the release.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in manifest.Files)
        {
            if (!BundleNames.Contains(item.Name, StringComparer.OrdinalIgnoreCase) || !seen.Add(item.Name) ||
                item.Length <= 0 || item.Length > MaximumBundleFileBytes || !IsSha256(item.Sha256))
            {
                throw new InvalidDataException("The staged bundle manifest contains an invalid file entry.");
            }

            var path = Path.Combine(bundleDirectory, item.Name);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != item.Length ||
                !HashFileSync(path).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The staged bundle file failed validation: {item.Name}.");
            }

            total = checked(total + item.Length);
        }

        if (seen.Count != BundleNames.Length || total > MaximumBundleBytes ||
            Directory.EnumerateFiles(bundleDirectory).Count() != BundleNames.Length)
        {
            throw new InvalidDataException("The staged bundle is incomplete or contains unexpected files.");
        }

    }

    private static string HashFileSync(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ApplyFromHelperArguments(string[] args)
    {
        if (args.Length != 6)
        {
            throw new InvalidDataException("The .NET update helper arguments are invalid.");
        }

        var readyPath = Path.GetFullPath(args[1]);
        var installDirectory = Path.GetFullPath(args[2]);
        var stageDirectory = Path.GetFullPath(args[3]);
        if (!IsOwnedStagingDirectory(installDirectory, stageDirectory) ||
            !readyPath.Equals(Path.Combine(stageDirectory, ".helper-ready"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The .NET update helper paths failed validation.");
        }

        var helperExecutable = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidDataException("The helper executable path is unavailable."));
        if (!helperExecutable.Equals(Path.Combine(stageDirectory, "helper", "rusttext.exe"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The .NET updater must run from its staged helper directory.");
        }

        var parentId = int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        var parentStartTicks = long.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
        var targetExecutable = Path.Combine(installDirectory, "rusttext.exe");
        VerifyBundleManifest(Path.Combine(stageDirectory, "bundle"), ReadBundleVersion(stageDirectory), ReadBundleHash(stageDirectory));

        using var parent = Process.GetProcessById(parentId);
        if (parent.StartTime.ToUniversalTime().Ticks != parentStartTicks ||
            !Path.GetFullPath(parent.MainModule?.FileName ?? string.Empty).Equals(targetExecutable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update parent process identity does not match the installed RustText process.");
        }

        using (var ready = new FileStream(readyPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var bytes = Encoding.UTF8.GetBytes("ready");
            ready.Write(bytes);
            ready.Flush(flushToDisk: true);
        }

        if (!parent.WaitForExit((int)ParentWaitLimit.TotalMilliseconds))
        {
            throw new TimeoutException("The running RustText window did not close before the update timeout.");
        }

        var lockPath = Path.Combine(installDirectory, ".rusttext-dotnet.update.lock");
        using var updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ApplyBundleTransaction(installDirectory, stageDirectory);
    }

    private static string ReadBundleVersion(string stageDirectory)
    {
        var manifest = ReadManifest(stageDirectory);
        return manifest.Version;
    }

    private static string ReadBundleHash(string stageDirectory)
    {
        var manifest = ReadManifest(stageDirectory);
        return manifest.ArchiveSha256;
    }

    private static BundleManifest ReadManifest(string stageDirectory)
    {
        var bytes = File.ReadAllBytes(Path.Combine(stageDirectory, "bundle-manifest.json"));
        return JsonSerializer.Deserialize(bytes, BundleManifestJsonContext.Default.BundleManifest)
            ?? throw new InvalidDataException("The staged bundle manifest is invalid.");
    }

    private static void ApplyBundleTransaction(string installDirectory, string stageDirectory)
    {
        var bundleDirectory = Path.Combine(stageDirectory, "bundle");
        var backupDirectory = Path.Combine(stageDirectory, "backup");
        Directory.CreateDirectory(backupDirectory);
        var changed = new List<(string Name, bool HadOriginal)>();

        try
        {
            foreach (var name in BundleNames)
            {
                var source = Path.Combine(bundleDirectory, name);
                var destination = Path.Combine(installDirectory, name);
                var backup = Path.Combine(backupDirectory, name);
                var hadOriginal = File.Exists(destination);
                if (hadOriginal)
                {
                    File.Replace(source, destination, backup);
                }
                else
                {
                    File.Move(source, destination);
                }

                changed.Add((name, hadOriginal));
            }

            VerifyInstalledBundle(installDirectory, Path.Combine(stageDirectory, "bundle-manifest.json"));
            StartAndVerifyInstalledApplication(Path.Combine(installDirectory, "rusttext.exe"), stageDirectory);
        }
        catch (Exception updateError)
        {
            try
            {
                RollBackBundle(installDirectory, backupDirectory, changed);
                StartApplicationWithCleanup(Path.Combine(installDirectory, "rusttext.exe"), stageDirectory);
            }
            catch (Exception rollbackError)
            {
                throw new IOException(
                    $"The .NET update failed and rollback also failed. The complete backup is preserved at '{backupDirectory}'. Update error: {updateError.Message} Rollback error: {rollbackError.Message}",
                    new AggregateException(updateError, rollbackError));
            }

            throw new IOException($"The .NET update failed. The previous bundle was restored. {updateError.Message}", updateError);
        }
    }

    private static void VerifyInstalledBundle(string installDirectory, string manifestPath)
    {
        var stageDirectory = Path.GetDirectoryName(manifestPath)!;
        var manifest = ReadManifest(stageDirectory);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            if (!BundleNames.Contains(item.Name, StringComparer.OrdinalIgnoreCase) || !seen.Add(item.Name))
            {
                throw new InvalidDataException("The staged bundle manifest contains an invalid installed file entry.");
            }

            var path = Path.Combine(installDirectory, item.Name);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != item.Length ||
                !HashFileSync(path).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The installed .NET bundle file failed validation: {item.Name}.");
            }
        }

        if (seen.Count != BundleNames.Length)
        {
            throw new InvalidDataException("The installed .NET bundle is incomplete.");
        }

        VerifyPeX64(Path.Combine(installDirectory, "rusttext.exe"));
    }

    private static void StartAndVerifyInstalledApplication(string executablePath, string stageDirectory)
    {
        var process = StartApplicationWithCleanup(executablePath, stageDirectory);
        if (!process.WaitForExit((int)StartupCheckDelay.TotalMilliseconds))
        {
            process.Dispose();
            return;
        }

        var exitCode = process.ExitCode;
        process.Dispose();
        throw new IOException($"The updated RustText process exited during startup with code {exitCode}.");
    }

    private static Process StartApplicationWithCleanup(string executablePath, string stageDirectory)
    {
        return Process.Start(new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            Environment =
            {
                [CleanupPidVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [CleanupDirectoryVariable] = stageDirectory
            }
        }) ?? throw new IOException("Could not start RustText after applying the .NET bundle.");
    }

    private static void RollBackBundle(string installDirectory, string backupDirectory, List<(string Name, bool HadOriginal)> changed)
    {
        for (var index = changed.Count - 1; index >= 0; index--)
        {
            var (name, hadOriginal) = changed[index];
            var destination = Path.Combine(installDirectory, name);
            var backup = Path.Combine(backupDirectory, name);
            if (hadOriginal)
            {
                if (File.Exists(destination))
                {
                    File.Replace(backup, destination, null);
                }
                else
                {
                    File.Move(backup, destination);
                }
            }
            else if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private static void StartStageCleanupFromEnvironment()
    {
        var processText = Environment.GetEnvironmentVariable(CleanupPidVariable);
        var stageDirectory = Environment.GetEnvironmentVariable(CleanupDirectoryVariable);
        Environment.SetEnvironmentVariable(CleanupPidVariable, null);
        Environment.SetEnvironmentVariable(CleanupDirectoryVariable, null);
        if (!int.TryParse(processText, out var processId) || processId <= 0 || string.IsNullOrWhiteSpace(stageDirectory))
        {
            return;
        }

        var executable = Environment.ProcessPath;
        var installDirectory = executable is null ? null : Path.GetDirectoryName(Path.GetFullPath(executable));
        if (installDirectory is null || !IsOwnedStagingDirectory(installDirectory, stageDirectory))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var helper = Process.GetProcessById(processId);
                await helper.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    DeleteOwnedStagingDirectory(installDirectory, stageDirectory);
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        });
    }

    private static void TryWriteHelperError(string[] args, string message)
    {
        if (args.Length < 4)
        {
            return;
        }

        try
        {
            var readyPath = Path.GetFullPath(args[1]);
            var installDirectory = Path.GetFullPath(args[2]);
            var stageDirectory = Path.GetFullPath(args[3]);
            if (IsOwnedStagingDirectory(installDirectory, stageDirectory) &&
                readyPath.Equals(Path.Combine(stageDirectory, ".helper-ready"), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(Path.Combine(stageDirectory, ".helper-error"), message, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

}
