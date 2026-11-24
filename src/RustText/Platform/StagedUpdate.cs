namespace RustText.Demo.Platform;

public sealed class StagedUpdate : IAsyncDisposable
{
    internal StagedUpdate(
        string version,
        string installDirectory,
        string stagingDirectory,
        string archiveSha256,
        string manifestSha256)
    {
        Version = version;
        InstallDirectory = installDirectory;
        StagingDirectory = stagingDirectory;
        ArchiveSha256 = archiveSha256;
        ManifestSha256 = manifestSha256;
    }

    public string Version { get; }
    public string InstallDirectory { get; }
    public string StagingDirectory { get; }
    public string ArchiveSha256 { get; }
    public string BundleDirectory => Path.Combine(StagingDirectory, "bundle");

    internal bool ApplyStarted { get; set; }
    internal string ManifestSha256 { get; }

    public ValueTask DisposeAsync()
    {
        if (!ApplyStarted && UpdateService.IsOwnedStagingDirectory(InstallDirectory, StagingDirectory))
        {
            UpdateService.DeleteOwnedStagingDirectory(InstallDirectory, StagingDirectory);
        }

        return ValueTask.CompletedTask;
    }
}
