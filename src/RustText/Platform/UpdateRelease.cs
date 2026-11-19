namespace RustText.Demo.Platform;

public sealed record UpdateRelease(
    string Version,
    string TagName,
    Uri ReleasePageUri,
    Uri ArchiveUri,
    Uri ChecksumUri,
    long ArchiveSize,
    string? ApiSha256,
    string? Notes = null);

public sealed record ReleaseNotes(string Version, Uri PageUri, string? Body);

internal sealed record BundleFile(string Name, long Length, string Sha256);

internal sealed record BundleManifest(string Version, string ArchiveSha256, BundleFile[] Files);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(BundleManifest))]
internal partial class BundleManifestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
