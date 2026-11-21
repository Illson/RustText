using System.Text.Json.Serialization;
using RustText.Demo.Logs;

namespace RustText.Demo;

internal sealed record WorkspaceLayout(long GroupId, long[]? Tabs = null, long? ActiveTab = null, string? Split = null, double Ratio = 0.5, WorkspaceLayout? First = null, WorkspaceLayout? Second = null);
internal sealed record SessionTab(long Id, string? Path, TextEncodingKind Encoding, int SelectionStart = 0, int SelectionLength = 0, bool Reversed = false, double ScrollX = 0, double ScrollY = 0, string? DraftRef = null, string? DraftSha256 = null, long? LogOffset = null, long? LineCount = null, long? LastWorkedMs = null, LineEndingKind LineEnding = LineEndingKind.Lf, byte[]? LineEndingMap = null, FileStamp? Stamp = null, LogTabState? Log = null)
{
    public TextEncodingKind? DiskEncoding { get; init; }
    public bool? AutoDetectEncoding { get; init; }
    public bool JsonValidationDisabled { get; init; }
    public LegacyFileStamp? LegacyStamp { get; init; }
    public bool SelectionUsesUtf8 { get; init; }
    public string? CustomTitle { get; init; }
    public string? UntitledName { get; init; }
    public bool? DraftLogMode { get; init; }
    public long? DraftOffset { get; init; }
    public bool? AutoRefresh { get; init; }
    public long? ClosedAtMs { get; init; }
}
internal sealed record LegacyFileStamp(long Length, DateTime LastWriteUtc, DateTime? CreatedUtc);
internal sealed record DraftArchiveEntry(string Id, SessionTab Tab, long ClosedAtMs, long Bytes, string Sha256, string DisplayName, long ArchivedAtMs);
internal sealed record DraftArchiveIndex(List<DraftArchiveEntry> Entries);
internal sealed record SessionState(List<SessionTab> Tabs, List<SessionTab>? ClosedTabs = null, int Active = 0, double WindowWidth = 1100, double WindowHeight = 760, bool Maximized = false, bool Wrap = false, double FontSize = FocusTheme.EditorFontSize, WorkspaceLayout? Layout = null, long? ActiveGroupId = null, Dictionary<long, long?>? DetachedDrafts = null);
public sealed record UserSettings(int UntitledTabLimit = 10, int RecentFileLimit = 100, int VersionFileLimitBytes = 5 * 1024 * 1024, long VersionTotalLimitBytes = 100L * 1024 * 1024)
{
    public string Theme { get; init; } = "focus";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? GlassEnabled { get; init; }
    public int GlassIntensity { get; init; }
    public bool ShowDraftLineCount { get; init; } = true;
    public bool AutoCheckUpdates { get; init; } = true;
    public bool AutoCheckUpdatesChoiceMade { get; init; }
    public bool? OpenLargeFilesAsLog { get; init; }
    public int ArchiveClosedDraftAfterMinutes { get; init; } = 300;
    public int ArchivedDraftRetentionDays { get; init; } = 30;
    public bool? WordWrap { get; init; }
    public double? FontSize { get; init; }
    public WorkspaceStartupMode WorkspaceStartupMode { get; init; } = WorkspaceStartupMode.LastUsed;
    public string? StartupWorkspaceId { get; init; }
    public int WorkspaceHistoryLimit { get; init; } = 10;
}
internal sealed record RecentFileEntry(
    string Path,
    TextEncodingKind? Encoding,
    bool ViewAsLog = false,
    int SelectionStart = 0,
    int SelectionLength = 0,
    long? LogOffset = null,
    long? LineCount = null,
    long? LastWorkedMs = null,
    bool Reversed = false,
    double ScrollX = 0,
    double ScrollY = 0,
    bool JsonValidationDisabled = false,
    LogTabState? Log = null,
    bool SelectionUsesUtf8 = false);
public sealed record VersionSnapshot(string Id, string Path, long CapturedMillis, long Bytes, string Sha256, TextEncodingKind? Encoding)
{
    [JsonIgnore]
    internal bool IsLegacy { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(DraftArchiveIndex))]
[JsonSerializable(typeof(LogTabState))]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(List<RecentFileEntry>))]
[JsonSerializable(typeof(List<VersionSnapshot>))]
internal partial class StorageJsonContext : JsonSerializerContext { }
