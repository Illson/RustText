using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task ImportedOpenAndClosedDraftsSurviveRustJournalRotation()
    {
        using var directory = new TempDirectory();
        const string activeText = "Активный черновик 💡";
        const string backgroundText = "Фоновый черновик";
        const string closedText = "Закрытый черновик";
        await WriteLegacySessionAsync(directory.Path, "checkpoint-00000000000000000001",
            [activeText, backgroundText], [closedText]);
        var storage = StorageService.OpenForTesting(directory.Path);

        var imported = await storage.LoadSessionAsync();
        Assert.NotNull(imported);
        Assert.Equal(activeText, await storage.LoadDraftAsync(imported.Tabs[0]));
        Assert.Equal(backgroundText, await storage.LoadDraftAsync(imported.Tabs[1]));
        Assert.Equal(closedText, await storage.LoadDraftAsync(imported.ClosedTabs![0]));
        Assert.All(imported.Tabs.Concat(imported.ClosedTabs!), tab => Assert.True(tab.SelectionUsesUtf8));

        for (var save = 1; save <= 4; save++)
        {
            var session = await storage.LoadSessionAsync();
            Assert.NotNull(session);
            var edited = activeText + $" сохранение {save}";
            await storage.SaveSessionAsync(session, new Dictionary<long, string> { [10] = edited });

            if (save == 1)
            {
                var legacy = Path.Combine(directory.Path, "session", "checkpoint-00000000000000000001");
                Directory.Move(legacy, Path.Combine(directory.Path, "retired-rust-checkpoint"));
                storage = StorageService.OpenForTesting(directory.Path);
            }
            var restored = await storage.LoadSessionAsync();
            Assert.NotNull(restored);
            Assert.Equal(edited, await storage.LoadDraftAsync(restored.Tabs.Single(tab => tab.Id == 10)));
            Assert.Equal(backgroundText, await storage.LoadDraftAsync(restored.Tabs.Single(tab => tab.Id == 11)));
            Assert.Equal(closedText, await storage.LoadDraftAsync(restored.ClosedTabs!.Single(tab => tab.Id == 12)));
        }

        Assert.Equal(2, Directory.GetDirectories(Path.Combine(storage.RootDirectory, "session"), "checkpoint-*").Length);
    }

    [Fact]
    public async Task SharedDraftDamageRestoresBothTabsFromPreviousCheckpoint()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var state = new SessionState([new(1, null, TextEncodingKind.Utf8), new(2, null, TextEncodingKind.Utf8)]);
        var first = await storage.SaveSessionAsync(state, new Dictionary<long, string> { [1] = "unchanged", [2] = "first" });
        var second = await storage.SaveSessionAsync(first, new Dictionary<long, string> { [2] = "second" });
        await File.WriteAllTextAsync(storage.GetDraftPath(second.Tabs[0])!, "damaged");

        storage = StorageService.OpenForTesting(directory.Path);
        var recovered = Assert.IsType<SessionState>(await storage.LoadSessionAsync());
        Assert.Equal("unchanged", await storage.LoadDraftAsync(recovered.Tabs[0]));
        Assert.Equal("first", await storage.LoadDraftAsync(recovered.Tabs[1]));
        var next = await storage.SaveSessionAsync(recovered, new Dictionary<long, string> { [2] = "third" });
        Assert.Equal("unchanged", await storage.LoadDraftAsync(next.Tabs[0]));
        Assert.Equal("third", await storage.LoadDraftAsync(next.Tabs[1]));
    }

    [Fact]
    public async Task CorruptLatestDraft_FallsBackToPreviousIntactSession()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var initial = State(new SessionTab(1, null, TextEncodingKind.Utf8));
        await storage.SaveSessionAsync(initial, new Dictionary<long, string> { [1] = "known-good draft" });
        var saved = await storage.LoadSessionAsync();
        Assert.NotNull(saved);
        await storage.SaveSessionAsync(saved, new Dictionary<long, string> { [1] = "new current draft" });

        var latest = await storage.LoadSessionAsync();
        Assert.NotNull(latest);
        var latestDraft = storage.GetDraftPath(latest.Tabs.Single())!;
        var pending = new List<SessionState>();
        var recovered = await storage.LoadSessionAsync(showPendingTabs: async state =>
        {
            pending.Add(state);
            if (pending.Count == 1)
            {
                Assert.Equal(latest.Tabs.Single().DraftRef, state.Tabs.Single().DraftRef);
                await File.WriteAllTextAsync(latestDraft, "corrupted bytes");
            }
        });
        Assert.Equal(2, pending.Count);
        Assert.NotNull(recovered);
        Assert.Equal("known-good draft", await storage.LoadDraftAsync(recovered.Tabs.Single()));
    }

    [Fact]
    public async Task ValidLatestDraft_IsPreferredWhenPreviousCheckpointIsCorrupt()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        await storage.SaveSessionAsync(State(new SessionTab(1, null, TextEncodingKind.Utf8)),
            new Dictionary<long, string> { [1] = "previous draft" });
        var saved = await storage.LoadSessionAsync();
        Assert.NotNull(saved);
        await storage.SaveSessionAsync(saved, new Dictionary<long, string> { [1] = "latest draft" });

        var checkpoints = Directory.GetDirectories(Path.Combine(storage.RootDirectory, "session"), "checkpoint-*");
        var latestName = (await File.ReadAllTextAsync(Path.Combine(storage.RootDirectory, "session", "latest.json"))).Trim();
        var previousName = (await File.ReadAllTextAsync(Path.Combine(storage.RootDirectory, "session", "previous.json"))).Trim();
        Assert.NotEqual(latestName, previousName);
        var previousState = await DeserializeCheckpointAsync(Path.Combine(storage.RootDirectory, "session", previousName));
        await File.WriteAllTextAsync(storage.GetDraftPath(previousState.Tabs.Single())!, "corrupt previous");

        var restored = await storage.LoadSessionAsync();
        Assert.NotNull(restored);
        Assert.Equal("latest draft", await storage.LoadDraftAsync(restored.Tabs.Single()));
        Assert.Equal(2, checkpoints.Length);
    }

    [Fact]
    public async Task UnchangedDeferredDraftReference_IsReusedAcrossCheckpointRotations()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        const string text = "deferred draft retained across saves";
        await storage.SaveSessionAsync(State(new SessionTab(9, null, TextEncodingKind.Utf8)),
            new Dictionary<long, string> { [9] = text });
        var stale = (await storage.LoadSessionAsync())!.Tabs.Single();

        for (var i = 0; i < 4; i++)
        {
            var current = await storage.LoadSessionAsync();
            Assert.NotNull(current);
            await storage.SaveSessionAsync(current);
        }

        Assert.True(File.Exists(storage.GetDraftPath(stale)!));
        Assert.Single(Directory.GetFiles(Path.Combine(storage.RootDirectory, "drafts"), "session-9-*.txt"));
        var resolved = await storage.ResolveCurrentDraftAsync(stale);
        Assert.Equal(text, await storage.LoadDraftAsync(resolved));
    }

    [Fact]
    public async Task SaveSessionWithDraftReaders_StreamsUtf8AndReusesUnchangedReference()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        const string text = "стриминговый черновик 💡";
        var state = State(new SessionTab(31, null, TextEncodingKind.Utf8));
        var first = await storage.SaveSessionWithDraftReadersAsync(state,
            new Dictionary<long, Func<TextReader>> { [31] = () => new StringReader(text) });
        var firstTab = first.Tabs.Single();
        Assert.Equal(text, await storage.LoadDraftAsync(firstTab));

        var second = await storage.SaveSessionWithDraftReadersAsync(first,
            new Dictionary<long, Func<TextReader>> { [31] = () => new StringReader(text) });
        var secondTab = second.Tabs.Single();
        Assert.Equal(firstTab.DraftRef, secondTab.DraftRef);
        Assert.Equal(firstTab.DraftSha256, secondTab.DraftSha256);
        Assert.Single(Directory.GetFiles(Path.Combine(storage.RootDirectory, "drafts"), "session-31-*.txt"));
    }

    [Fact]
    public async Task UnsafeLatestDraftReference_FallsBackToPreviousCheckpoint()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        await storage.SaveSessionAsync(State(new SessionTab(1, null, TextEncodingKind.Utf8)),
            new Dictionary<long, string> { [1] = "previous valid" });
        var previous = await storage.LoadSessionAsync();
        Assert.NotNull(previous);
        await storage.SaveSessionAsync(previous, new Dictionary<long, string> { [1] = "latest valid" });

        var latestName = (await File.ReadAllTextAsync(Path.Combine(storage.RootDirectory, "session", "latest.json"))).Trim();
        var latestManifest = Path.Combine(storage.RootDirectory, "session", latestName, "manifest.json");
        var latest = await DeserializeCheckpointAsync(Path.Combine(storage.RootDirectory, "session", latestName));
        var unsafeTab = latest.Tabs.Single() with { DraftRef = "../../outside.txt" };
        await File.WriteAllTextAsync(latestManifest,
            JsonSerializer.Serialize(latest with { Tabs = [unsafeTab] }, StorageJsonContext.Default.SessionState));

        var restored = await storage.LoadSessionAsync();
        Assert.NotNull(restored);
        Assert.Equal("previous valid", await storage.LoadDraftAsync(restored.Tabs.Single()));
    }

    [Fact]
    public async Task SavingWithCorruptLatest_DoesNotReplaceValidPreviousPointer()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        await storage.SaveSessionAsync(State(new SessionTab(1, null, TextEncodingKind.Utf8)),
            new Dictionary<long, string> { [1] = "first valid" });
        var first = await storage.LoadSessionAsync();
        Assert.NotNull(first);
        await storage.SaveSessionAsync(first, new Dictionary<long, string> { [1] = "second valid" });

        var sessionDirectory = Path.Combine(storage.RootDirectory, "session");
        var latestName = (await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "latest.json"))).Trim();
        var previousName = (await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "previous.json"))).Trim();
        var latest = await DeserializeCheckpointAsync(Path.Combine(sessionDirectory, latestName));
        var damaged = latest.Tabs.Single() with { DraftRef = "../../missing.txt" };
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, latestName, "manifest.json"),
            JsonSerializer.Serialize(latest with { Tabs = [damaged] }, StorageJsonContext.Default.SessionState));

        var validPrevious = await storage.LoadSessionAsync();
        Assert.NotNull(validPrevious);
        await storage.SaveSessionAsync(validPrevious, new Dictionary<long, string> { [1] = "third valid" });

        Assert.Equal(previousName, (await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "previous.json"))).Trim());
        var restored = await storage.LoadSessionAsync();
        Assert.NotNull(restored);
        Assert.Equal("third valid", await storage.LoadDraftAsync(restored.Tabs.Single()));
        var previousState = await DeserializeCheckpointAsync(Path.Combine(sessionDirectory, previousName));
        var preserved = await storage.LoadDraftAsync(previousState.Tabs.Single());
        Assert.Equal("first valid", preserved);
    }

    [Fact]
    public async Task OversizedDraft_RemainsAvailableAsStreamedPreviewFile()
    {
        using var directory = new TempDirectory();
        var storage = StorageService.OpenForTesting(directory.Path);
        var content = new string('x', 8 * 1024 * 1024 + 1) + "\nпоследняя строка";
        await storage.SaveSessionAsync(State(new SessionTab(8, null, TextEncodingKind.Utf8)),
            new Dictionary<long, string> { [8] = content });

        var session = await storage.LoadSessionAsync();
        Assert.NotNull(session);
        var saved = session.Tabs.Single();
        var path = storage.GetDraftPath(saved);
        Assert.NotNull(path);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(content), storage.GetDraftLength(saved));
        Assert.True(File.Exists(path));
        await Assert.ThrowsAsync<IOException>(() => storage.LoadDraftAsync(saved));
        Assert.Equal("последняя строка", await ReadLastLineAsync(path!));
    }

    [Fact]
    public async Task LegacyRustSession_FallsBackPastCorruptNewestCheckpoint()
    {
        using var directory = new TempDirectory();
        await WriteLegacySessionAsync(directory.Path, "checkpoint-00000000000000000001", ["older recoverable text"], []);
        await WriteLegacySessionAsync(directory.Path, "checkpoint-00000000000000000002", ["newer damaged text"], []);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "session", "checkpoint-00000000000000000002", "draft-0.txt"), "damaged");

        var storage = StorageService.OpenForTesting(directory.Path);
        var recovered = await storage.LoadSessionAsync();

        Assert.NotNull(recovered);
        Assert.Equal("older recoverable text", await storage.LoadDraftAsync(recovered.Tabs.Single()));
    }

    [Fact]
    public async Task LegacyRecentImport_KeepsMoreThanOneHundredAndMapsRustPositionAndBookmark()
    {
        using var directory = new TempDirectory();
        var entries = new List<Dictionary<string, object?>>();
        var identity = new Dictionary<string, object?> { ["volume"] = 5UL, ["id"] = Enumerable.Range(0, 16).ToArray() };
        var fingerprint = new Dictionary<string, object?>
        {
            ["modified"] = new Dictionary<string, object?> { ["secs_since_epoch"] = 1700000000L, ["nanos_since_epoch"] = 250U },
            ["digest"] = Enumerable.Range(1, 32).ToArray()
        };
        var log = new Dictionary<string, object?>
        {
            ["identity"] = identity,
            ["size"] = 9000L,
            ["fingerprint"] = fingerprint,
            ["offset"] = 4096L,
            ["hidden"] = 128L,
            ["auto_refresh"] = false,
            ["follow_end"] = true
        };
        entries.Add(new Dictionary<string, object?>
        {
            ["path"] = "first.json",
            ["windows_1251"] = false,
            ["view_as_log"] = true,
            ["position"] = new Dictionary<string, object?>
            {
                ["json_validation_disabled"] = true,
                ["selection"] = new Dictionary<string, object?> { ["start"] = 3, ["end"] = 7 },
                ["reversed"] = true,
                ["scroll"] = new[] { -12d, -64d },
                ["log"] = log
            },
            ["line_count"] = 240L,
            ["last_worked_ms"] = 1700000000123L
        });
        for (var index = 1; index < 137; index++)
        {
            entries.Add(new Dictionary<string, object?> { ["path"] = $"file-{index:D3}.txt", ["windows_1251"] = false });
        }
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "recent-files.json"), JsonSerializer.Serialize(entries));
        var storage = StorageService.OpenForTesting(directory.Path);

        var imported = await storage.GetRecentFilesAsync();
        var first = imported[0];
        Assert.Equal(137, imported.Count);
        Assert.Equal("first.json", first.Path);
        Assert.Null(first.Encoding); // Rust's false flag means detect BOM/UTF-16 when opening.
        Assert.Equal(3, first.SelectionStart);
        Assert.Equal(4, first.SelectionLength);
        Assert.True(first.SelectionUsesUtf8);
        Assert.True(first.Reversed);
        Assert.Equal(12, first.ScrollX);
        Assert.Equal(64, first.ScrollY);
        Assert.True(first.JsonValidationDisabled);
        Assert.Equal(4096, first.LogOffset);
        Assert.Equal(240, first.LineCount);
        Assert.Equal(1700000000123, first.LastWorkedMs);
        Assert.Equal(5UL, first.Log!.Identity!.Volume);
        Assert.Equal(Enumerable.Range(0, 16), first.Log.Identity.Id);
        Assert.Equal(9000, first.Log.Size);
        Assert.Equal(4096, first.Log.Offset);
        Assert.Equal(128, first.Log.HiddenOffset);
        Assert.False(first.Log.AutoRefresh);
        Assert.True(first.Log.FollowEnd);
        Assert.Equal(1700000000, first.Log.Fingerprint!.Modified!.SecsSinceEpoch);
        Assert.Equal(250U, first.Log.Fingerprint.Modified.NanosSinceEpoch);
        Assert.Equal(Enumerable.Range(1, 32), first.Log.Fingerprint.Digest);

        await storage.AddRecentAsync(first);
        Assert.Equal(137, (await storage.GetRecentFilesAsync()).Count);
    }

    [Theory]
    [InlineData("AЖ🙂Z", 0, 0)]
    [InlineData("AЖ🙂Z", 1, 1)]
    [InlineData("AЖ🙂Z", 3, 2)]
    [InlineData("AЖ🙂Z", 7, 4)]
    [InlineData("AЖ🙂Z", 8, 5)]
    [InlineData("AЖ🙂Z", 100, 5)]
    public void Utf8IndexToUtf16_MapsRustByteOffsetsAcrossMultibyteRunes(string text, long byteOffset, int expected)
    {
        Assert.Equal(expected, MainWindow.Utf8IndexToUtf16(text, byteOffset));
    }

    [Fact]
    public async Task ImportedUtf16BomRecent_RestoresDecodedTextReversedUtf8SelectionScrollAndJsonSetting()
    {
        using var historyDirectory = new TempDirectory();
        var path = Path.Combine(historyDirectory.Path, "unicode.json");
        var content = "{\"value\":\"AЖ🙂Z\"}\n" + string.Join('\n', Enumerable.Range(0, 100).Select(i => $"{{\"row\":{i}}}"));
        var body = Encoding.Unicode.GetBytes(content);
        await File.WriteAllBytesAsync(path, new byte[] { 0xFF, 0xFE }.Concat(body).ToArray());
        var emojiStart = content.IndexOf("🙂", StringComparison.Ordinal);
        var byteStart = Encoding.UTF8.GetByteCount(content[..emojiStart]);
        var profile = historyDirectory.Path;
        var recentJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["windows_1251"] = false,
                ["view_as_log"] = false,
                ["position"] = new Dictionary<string, object?>
                {
                    ["json_validation_disabled"] = true,
                    ["selection"] = new Dictionary<string, object?> { ["start"] = byteStart, ["end"] = byteStart + 4 },
                    ["reversed"] = true,
                    ["scroll"] = new[] { 0d, -80d }
                }
            }
        });
        await File.WriteAllTextAsync(Path.Combine(profile, "recent-files.json"), recentJson);
        var entry = (await StorageService.OpenForTesting(profile).GetRecentFilesAsync()).Single();

        await HeadlessTests.RunAsync(async _ =>
        {
            var window = new MainWindow([path]);
            window.Show();
            await HeadlessTests.WaitForAsync(() => GetField<bool>(window, "_restoreFinished"));
            await InvokeTaskAsync(window, "OpenRecentAsync", entry);

            var tab = GetProperty<object>(window, "ActiveTab");
            var editor = GetProperty<TextEditor>(tab, "Editor");
            Assert.Equal(content, editor.Text);
            Assert.Equal(emojiStart, editor.SelectionStart);
            Assert.Equal(2, editor.SelectionLength);
            Assert.Equal(emojiStart, editor.CaretOffset);
            Assert.True(GetProperty<bool>(tab, "JsonDisabled"));
            await HeadlessTests.WaitForAsync(() => editor.VerticalOffset > 0);
            Assert.True(editor.VerticalOffset > 0);
            window.Close();
            await HeadlessTests.WaitForAsync(() => GetField<bool>(window, "_closed"));
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        });
    }

    [Fact]
    public async Task ImportedWindows1251Recent_UsesExplicitLegacyEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var historyDirectory = new TempDirectory();
        var path = Path.Combine(historyDirectory.Path, "cyrillic.txt");
        await File.WriteAllBytesAsync(path, Encoding.GetEncoding(1251).GetBytes("Привет, мир!"));
        var legacy = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?> { ["path"] = path, ["windows_1251"] = true }
        });
        await File.WriteAllTextAsync(Path.Combine(historyDirectory.Path, "recent-files.json"), legacy);
        var entry = (await StorageService.OpenForTesting(historyDirectory.Path).GetRecentFilesAsync()).Single();

        await HeadlessTests.RunAsync(async _ =>
        {
            var window = new MainWindow([path]);
            window.Show();
            await HeadlessTests.WaitForAsync(() => GetField<bool>(window, "_restoreFinished"));
            await InvokeTaskAsync(window, "OpenRecentAsync", entry);
            var editor = GetProperty<TextEditor>(GetProperty<object>(window, "ActiveTab"), "Editor");
            Assert.Equal("Привет, мир!", editor.Text);
            Assert.Equal(TextEncodingKind.Windows1251, GetProperty<TextEncodingKind>(GetProperty<object>(window, "ActiveTab"), "Encoding"));
            window.Close();
            await HeadlessTests.WaitForAsync(() => !window.IsVisible);
        });
    }

    private static SessionState State(params SessionTab[] tabs) => new(tabs.ToList());

    private static async Task WriteLegacySessionAsync(string profileBase, string checkpoint, string[] openDrafts, string[] closedDrafts)
    {
        var directory = Path.Combine(profileBase, "session", checkpoint);
        Directory.CreateDirectory(directory);
        var openInfo = await WriteDraftsAsync(directory, openDrafts, "draft-");
        var closedInfo = await WriteDraftsAsync(directory, closedDrafts, "closed-draft-");
        var state = new
        {
            layout = (object?)null,
            tabs = openDrafts.Select((_, index) => LegacyTab(10 + index)).ToArray(),
            closed = closedDrafts.Select((_, index) => LegacyTab(12 + index)).ToArray(),
            active = 0,
            window = new { width = 1100, height = 760, maximized = false },
            wrap = false,
            font_size = 14
        };
        var manifest = new { version = 1, state, drafts = openInfo, closed_drafts = closedInfo };
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static async Task<object?[]> WriteDraftsAsync(string directory, string[] drafts, string prefix)
    {
        var infos = new object?[drafts.Length];
        for (var index = 0; index < drafts.Length; index++)
        {
            var bytes = Encoding.UTF8.GetBytes(drafts[index]);
            await File.WriteAllBytesAsync(Path.Combine(directory, $"{prefix}{index}.txt"), bytes);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            infos[index] = new { bytes = bytes.Length, checksum = 0, sha256 = sha };
        }

        return infos;
    }

    private static object LegacyTab(long id) => new
    {
        json_validation_disabled = false,
        id,
        file = (object?)null,
        format = new { encoding = "Utf8", endings = "Lf" },
        selection = new { start = 0, end = 0 },
        reversed = false,
        scroll = new[] { 0d, 0d },
        draft_revision = 1,
        log = (object?)null
    };

    private static async Task<SessionState> DeserializeCheckpointAsync(string checkpointDirectory)
    {
        await using var stream = File.OpenRead(Path.Combine(checkpointDirectory, "manifest.json"));
        return (await JsonSerializer.DeserializeAsync(stream, StorageJsonContext.Default.SessionState))!;
    }

    private static async Task<string?> ReadLastLineAsync(string path)
    {
        using var reader = new StreamReader(path);
        string? line = null;
        while (await reader.ReadLineAsync() is { } current) line = current;
        return line;
    }

    private static async Task InvokeTaskAsync(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        await ((Task)method.Invoke(target, arguments)!).ConfigureAwait(true);
    }

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().FullName, name));

    private static T GetProperty<T>(object target, string name) =>
        (T)(target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target)
            ?? throw new MissingMemberException(target.GetType().FullName, name));
}
