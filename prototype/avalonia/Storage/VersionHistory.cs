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
    private readonly long _maxFileBytes;
    private readonly long _totalBytes;

    private VersionHistory(string directory, long maxFileBytes, long totalBytes)
    {
        _directory = directory; _blobs = Path.Combine(directory, "blobs"); _manifest = Path.Combine(directory, "manifest.json");
        _maxFileBytes = Math.Clamp(maxFileBytes, 0, DocumentFiles.MaximumDocumentBytes); _totalBytes = Math.Clamp(totalBytes, 0, 1024L * 1024 * 1024);
        Directory.CreateDirectory(_blobs);
    }

    public static VersionHistory OpenDefault(long maxFileBytes = DefaultMaxFileBytes, long totalBytes = DefaultTotalBytes)
    {
        return new(Path.Combine(StorageService.GetProfileBaseDirectory(), "dotnet-demo", "versions"), maxFileBytes, totalBytes);
    }

    public async Task<VersionSnapshot?> CaptureAsync(string path, TextEncodingKind encoding, CancellationToken cancellationToken = default)
    {
        using var gate = AcquireLock();
        var fullPath = Path.GetFullPath(path);
        var before = new FileInfo(fullPath);
        if (before.Length > _maxFileBytes) return null;
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength > _maxFileBytes) return null;
        var stamp = await DocumentFiles.GetStampAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (stamp.Length != bytes.LongLength || !string.Equals(stamp.Sha256, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal))
            throw new IOException("Файл изменился во время создания версии.");
        var digest = stamp.Sha256;
        var entries = (await ReadManifestAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var same = entries.FirstOrDefault(e => string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (same is not null && same.Sha256 == digest && same.Encoding == encoding) return same;
        var snapshot = new VersionSnapshot(DateTime.UtcNow.Ticks.ToString("x16") + Guid.NewGuid().ToString("N")[..8], fullPath,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), bytes.LongLength, digest, encoding);
        await StorageService.AtomicWriteAsync(Path.Combine(_blobs, digest), bytes, cancellationToken).ConfigureAwait(false);
        entries.Insert(0, snapshot);
        var total = entries.Sum(e => e.Bytes);
        var newestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newestIds = entries.Where(e => newestPaths.Add(e.Path)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = entries.Count - 1; i >= 0 && total > _totalBytes; i--)
        {
            var item = entries[i];
            if (!newestIds.Contains(item.Id)) { total -= item.Bytes; entries.RemoveAt(i); }
        }
        // Preserve at least the latest version per path, even if those files exceed the global budget.
        await WriteManifestAsync(entries, cancellationToken).ConfigureAwait(false);
        await DeleteUnusedBlobsAsync(entries, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<IReadOnlyList<VersionSnapshot>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        using var gate = AcquireLock();
        var normalized = Path.GetFullPath(path);
        return (await ReadManifestAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public async Task<byte[]> ReadAsync(string path, string id, CancellationToken cancellationToken = default)
    {
        using var gate = AcquireLock();
        var normalized = Path.GetFullPath(path);
        var item = (await ReadManifestAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(v => v.Id == id && string.Equals(v.Path, normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Снимок не найден.");
        var blob = Path.Combine(_blobs, item.Sha256);
        var bytes = await File.ReadAllBytesAsync(blob, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != item.Bytes || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), item.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Снимок повреждён.");
        return bytes;
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

    private async Task DeleteUnusedBlobsAsync(List<VersionSnapshot> items, CancellationToken token)
    {
        var retained = items.Select(e => e.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_blobs))
        {
            token.ThrowIfCancellationRequested();
            if (!retained.Contains(Path.GetFileName(file))) File.Delete(file);
        }
    }
}
