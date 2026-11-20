using System.Security.Cryptography;
using System.Text.Json;

namespace RustText.Demo;

public sealed class VersionHistory
{
    public const long DefaultMaxFileBytes = 5L * 1024 * 1024;
    public const long DefaultTotalBytes = 100L * 1024 * 1024;
    private readonly string _directory;
    private readonly string _blobs;
    private readonly string _manifest;
    private readonly string? _legacyDirectory;
    private readonly long _maxFileBytes;
    private readonly long _totalBytes;

    private VersionHistory(string directory, long maxFileBytes, long totalBytes, string? legacyDirectory = null)
    {
        _directory = directory; _blobs = Path.Combine(directory, "blobs"); _manifest = Path.Combine(directory, "manifest.json");
        _legacyDirectory = legacyDirectory is null ? null : Path.GetFullPath(legacyDirectory);
        _maxFileBytes = Math.Clamp(maxFileBytes, 0, DocumentFiles.MaximumDocumentBytes); _totalBytes = Math.Clamp(totalBytes, 0, 1024L * 1024 * 1024);
        Directory.CreateDirectory(_blobs);
    }

    public static VersionHistory OpenDefault(long maxFileBytes = DefaultMaxFileBytes, long totalBytes = DefaultTotalBytes)
    {
        var profileBase = StorageService.GetProfileBaseDirectory();
        return new(Path.Combine(profileBase, "dotnet-demo", "versions"), maxFileBytes, totalBytes,
            Path.Combine(profileBase, "versions"));
    }

    internal static VersionHistory Open(string directory, long maxFileBytes, long totalBytes, string? legacyDirectory = null) =>
        new(directory, maxFileBytes, totalBytes, legacyDirectory);

    public Task<VersionSnapshot?> CaptureAsync(string path, TextEncodingKind encoding, CancellationToken cancellationToken = default) =>
        CaptureCoreAsync(path, encoding, expectedStamp: null, cancellationToken);

    internal Task<VersionSnapshot?> CaptureExpectedAsync(string path, TextEncodingKind? encoding, FileStamp expectedStamp, CancellationToken cancellationToken = default) =>
        CaptureCoreAsync(path, encoding, expectedStamp, cancellationToken);

    private async Task<VersionSnapshot?> CaptureCoreAsync(string path, TextEncodingKind? encoding, FileStamp? expectedStamp, CancellationToken cancellationToken)
    {
        using var gate = AcquireLock();
        var fullPath = Path.GetFullPath(path);
        await using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var initialLength = source.Length;
        if (initialLength > _maxFileBytes) return null;
        var bytes = new byte[checked((int)initialLength)];
        await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var stamp = await FileStamp.CaptureAsync(source, cancellationToken).ConfigureAwait(false);
        if (stamp.Length != bytes.LongLength || !string.Equals(stamp.Sha256, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal))
            throw new IOException("Файл изменился во время создания версии.");
        if (expectedStamp is not null && stamp != expectedStamp)
            throw new IOException("Файл изменился с момента последнего сохранения. Версия не записана.");
        var digest = stamp.Sha256;
        var entries = (await ReadManifestAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var same = entries.FirstOrDefault(e => string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (same is not null && same.Sha256 == digest && (encoding is null || same.Encoding == encoding)) return same;
        if (encoding is null)
        {
            // Save As can target a file the editor has never opened.
            try { encoding = DocumentFiles.DetectEncoding(bytes); }
            catch (InvalidDataException)
            {
                /* Keep unknown or binary content marked as such. */
            }
        }
        var snapshot = new VersionSnapshot(DateTime.UtcNow.Ticks.ToString("x16") + Guid.NewGuid().ToString("N")[..8], fullPath,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), bytes.LongLength, digest, encoding);
        entries.Insert(0, snapshot);
        PruneToBudget(entries, _totalBytes);
        var blob = Path.Combine(_blobs, digest);
        var blobCreated = false;
        if (!File.Exists(blob))
        {
            await StorageService.AtomicWriteAsync(blob, bytes, cancellationToken).ConfigureAwait(false);
            blobCreated = true;
        }
        else
        {
            await VerifyBlobAsync(blob, digest, bytes.LongLength, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await WriteManifestAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (blobCreated && !entries.Any(item => !ReferenceEquals(item, snapshot) && item.Sha256 == digest))
            {
                try { File.Delete(blob); } catch (IOException) { }
            }
            throw;
        }
        DeleteUnusedBlobs(entries, cancellationToken);
        return snapshot;
    }

    public async Task<IReadOnlyList<VersionSnapshot>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        using var gate = AcquireLock();
        var normalized = Path.GetFullPath(path);
        var current = (await ReadManifestAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        var legacy = await ReadLegacyListAsync(normalized, cancellationToken).ConfigureAwait(false);
        return current.Concat(legacy).OrderByDescending(item => item.CapturedMillis).ToArray();
    }

    public async Task<byte[]> ReadAsync(string path, string id, CancellationToken cancellationToken = default)
    {
        var versions = await ListAsync(path, cancellationToken).ConfigureAwait(false);
        var selected = versions.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("Снимок не найден.");
        return await ReadAsync(path, selected, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadAsync(string path, VersionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = Path.GetFullPath(path);
        if (!string.Equals(normalized, snapshot.Path, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Снимок не найден.");
        if (snapshot.IsLegacy)
        {
            if (_legacyDirectory is null || !IsDigest(snapshot.Sha256))
                throw new InvalidDataException("Некорректная ссылка на старую версию.");
            var available = await ReadLegacyListAsync(normalized, cancellationToken).ConfigureAwait(false);
            var legacyItem = available.FirstOrDefault(item => item.Id == snapshot.Id
                && item.Sha256 == snapshot.Sha256 && item.Bytes == snapshot.Bytes && item.CapturedMillis == snapshot.CapturedMillis)
                ?? throw new FileNotFoundException("Старая версия не найдена.");
            var legacyBlob = Path.Combine(_legacyDirectory, "blobs", legacyItem.Sha256);
            return await ReadVerifiedBlobAsync(legacyBlob, legacyItem.Sha256, legacyItem.Bytes, cancellationToken).ConfigureAwait(false);
        }

        using var gate = AcquireLock();
        var item = (await ReadManifestAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(v => v.Id == snapshot.Id
            && string.Equals(v.Path, normalized, StringComparison.OrdinalIgnoreCase)
            && v.Sha256 == snapshot.Sha256 && v.Bytes == snapshot.Bytes)
            ?? throw new FileNotFoundException("Снимок не найден.");
        var blob = Path.Combine(_blobs, item.Sha256);
        return await ReadVerifiedBlobAsync(blob, item.Sha256, item.Bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VersionSnapshot>> ReadLegacyListAsync(string normalizedPath, CancellationToken token)
    {
        if (_legacyDirectory is null) return [];
        var manifest = Path.Combine(_legacyDirectory, "manifest.json");
        if (!File.Exists(manifest)) return [];

        await using var stream = File.OpenRead(manifest);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Манифест старых версий имеет неверный формат.");

        var result = new List<VersionSnapshot>();
        foreach (var item in versions.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetString(item, "path", out var pathText)
                || !TryGetString(item, "id", out var id)
                || !TryGetString(item, "checksum", out var checksum)
                || !IsLegacyId(id)
                || !IsLegacyDigest(checksum)
                || !item.TryGetProperty("bytes", out var bytesValue) || !bytesValue.TryGetInt64(out var bytes) || bytes < 0
                || !item.TryGetProperty("captured_millis", out var capturedValue) || !capturedValue.TryGetInt64(out var capturedMillis)
                || capturedMillis is < 0 or > 253_402_300_799_999)
            {
                continue;
            }

            if (!Path.IsPathFullyQualified(pathText)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(pathText); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!string.Equals(fullPath, normalizedPath, StringComparison.OrdinalIgnoreCase)) continue;

            var encoding = item.TryGetProperty("encoding", out var encodingValue)
                && encodingValue.ValueKind == JsonValueKind.String
                ? ParseLegacyEncoding(encodingValue.GetString())
                : null;
            result.Add(new VersionSnapshot(id, fullPath, capturedMillis, bytes, checksum, encoding)
            {
                IsLegacy = true
            });
        }
        return result;
    }

    private async Task<List<VersionSnapshot>> ReadManifestAsync(CancellationToken token)
    {
        if (!File.Exists(_manifest)) return [];
        await using var stream = File.OpenRead(_manifest);
        try { return await JsonSerializer.DeserializeAsync(stream, StorageJsonContext.Default.ListVersionSnapshot, token).ConfigureAwait(false) ?? []; }
        catch (JsonException ex) { throw new InvalidDataException("Манифест версий повреждён.", ex); }
    }

    private Task WriteManifestAsync(List<VersionSnapshot> items, CancellationToken token) => StorageService.WriteJsonAsync(_manifest, items, StorageJsonContext.Default.ListVersionSnapshot, token);

    private FileStream AcquireLock()
    {
        var stream = new FileStream(Path.Combine(_directory, "versions.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try { stream.Lock(0, 1); return stream; }
        catch { stream.Dispose(); throw; }
    }

    private void DeleteUnusedBlobs(List<VersionSnapshot> items, CancellationToken token)
    {
        var retained = items.Select(e => e.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_blobs))
        {
            token.ThrowIfCancellationRequested();
            if (!retained.Contains(Path.GetFileName(file))) File.Delete(file);
        }
    }

    private static void PruneToBudget(List<VersionSnapshot> entries, long totalBytes)
    {
        long total = 0;
        foreach (var entry in entries) total = checked(total + entry.Bytes);

        var newestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newestIds = entries.Where(entry => newestPaths.Add(entry.Path))
            .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = entries.Count - 1; index >= 0 && total > totalBytes; index--)
        {
            var item = entries[index];
            if (newestIds.Contains(item.Id)) continue;
            total -= item.Bytes;
            entries.RemoveAt(index);
        }
        if (total > totalBytes)
            throw new IOException("Общий лимит не вмещает последние версии всех файлов. Увеличьте квоту.");
    }

    private static async Task VerifyBlobAsync(string path, string digest, long expectedBytes, CancellationToken token)
    {
        var bytes = await ReadVerifiedBlobAsync(path, digest, expectedBytes, token).ConfigureAwait(false);
        _ = bytes;
    }

    private static async Task<byte[]> ReadVerifiedBlobAsync(string path, string digest, long expectedBytes, CancellationToken token)
    {
        if (!IsDigest(digest) || expectedBytes < 0 || expectedBytes > 128L * 1024 * 1024)
            throw new InvalidDataException("Размер или контрольная сумма снимка неверны.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedBytes)
            throw new InvalidDataException("Размер снимка не совпадает с манифестом.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        if (bytes.LongLength != expectedBytes || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Снимок повреждён.");
        return bytes;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } result)
        {
            value = result;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool IsLegacyId(string value) => value.Length == 16
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsLegacyDigest(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDigest(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static TextEncodingKind? ParseLegacyEncoding(string? value) => value switch
    {
        "Utf8" => TextEncodingKind.Utf8,
        "Utf8Bom" => TextEncodingKind.Utf8Bom,
        "Utf16Le" => TextEncodingKind.Utf16LittleEndian,
        "Utf16Be" => TextEncodingKind.Utf16BigEndian,
        "Windows1251" => TextEncodingKind.Windows1251,
        _ => null
    };
}
