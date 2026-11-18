namespace RustText.Demo.Platform;

public sealed class StagedUpdate : IAsyncDisposable
{
    internal StagedUpdate(
        string version,
        string installDirectory,
        string stagingDirectory,
        string archiveSha256)
    {
        Version = version;
        InstallDirectory = installDirectory;
        StagingDirectory = stagingDirectory;
        ArchiveSha256 = archiveSha256;
    }

    public string Version { get; }
    public string InstallDirectory { get; }
    public string StagingDirectory { get; }
    public string ArchiveSha256 { get; }
    public string BundleDirectory => Path.Combine(StagingDirectory, "bundle");

    internal bool ApplyStarted { get; set; }

    public ValueTask DisposeAsync()
    {
        if (!ApplyStarted && UpdateService.IsOwnedStagingDirectory(InstallDirectory, StagingDirectory))
        {
            UpdateService.DeleteOwnedStagingDirectory(InstallDirectory, StagingDirectory);
        }

        return ValueTask.CompletedTask;
    }
}
