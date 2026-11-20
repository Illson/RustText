using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class VersionTests
{
    [Fact]
    public async Task SaveBoundary_CapturesOriginalAndCommittedVersions()
    {
        await HeadlessTests.RunAsync(async profilePath =>
        {
            var source = Path.Combine(profilePath, "notes.txt");
            var store = VersionHistory.Open(Path.Combine(profilePath, "dotnet-demo", "versions"), 1024, 4096);
            await File.WriteAllTextAsync(source, "A");
            var originalStamp = await DocumentFiles.GetStampAsync(source);

            await store.CaptureExpectedAsync(source, TextEncodingKind.Utf8, originalStamp);
            var savedStamp = await DocumentFiles.SaveAsync(source, "B", TextEncodingKind.Utf8,
                LineEndingKind.Lf, originalStamp);
            await store.CaptureExpectedAsync(source, TextEncodingKind.Utf8, savedStamp);

            var versions = await store.ListAsync(source);
            Assert.Equal(2, versions.Count);
            Assert.Equal("B", await File.ReadAllTextAsync(source));
            var contents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var version in versions)
            {
                contents.Add(DocumentFiles.DecodeText(await store.ReadAsync(source, version), TextEncodingKind.Utf8));
            }
            Assert.Equal(new[] { "A", "B" }, contents.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public async Task ExpectedStamp_RejectsExternalChangesWithoutReplacingOrCapturingThem()
    {
        await HeadlessTests.RunAsync(async profilePath =>
        {
            var source = Path.Combine(profilePath, "external.txt");
            var store = VersionHistory.Open(Path.Combine(profilePath, "versions"), 1024, 4096);
            await File.WriteAllTextAsync(source, "opened");
            var expected = await DocumentFiles.GetStampAsync(source);
            await File.WriteAllTextAsync(source, "external");

            await Assert.ThrowsAsync<IOException>(() => store.CaptureExpectedAsync(source, TextEncodingKind.Utf8, expected));
            await Assert.ThrowsAsync<IOException>(() => DocumentFiles.SaveAsync(source, "editor", TextEncodingKind.Utf8,
                LineEndingKind.Lf, expected));

            Assert.Equal("external", await File.ReadAllTextAsync(source));
            Assert.Empty(await store.ListAsync(source));
        });
    }

    [Fact]
    public async Task LegacyRustSnapshots_AreListedAndReadWithoutChangingTheirProfile()
    {
        await HeadlessTests.RunAsync(async profilePath =>
        {
            var source = Path.Combine(profilePath, "legacy.txt");
            var legacyRoot = Path.Combine(profilePath, "RustText", "versions");
            var dotnetRoot = Path.Combine(profilePath, "dotnet-demo", "versions");
            var legacyBlobs = Path.Combine(legacyRoot, "blobs");
            Directory.CreateDirectory(legacyBlobs);
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A' };
            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var legacyId = "0000000000000001";
            var legacyBlob = Path.Combine(legacyBlobs, checksum);
            await File.WriteAllBytesAsync(legacyBlob, bytes);
            await File.WriteAllTextAsync(source, "current");
            var manifestPath = Path.Combine(legacyRoot, "manifest.json");
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                settings = new { max_file_bytes = 1024, total_bytes = 2048 },
                sequence = 1,
                versions = new[]
                {
                    new { id = legacyId, path = Path.GetFullPath(source), captured_millis = 7L,
                        bytes = (long)bytes.Length, checksum, encoding = "Utf8Bom" }
                }
            });
            await File.WriteAllBytesAsync(manifestPath, manifest);
            var store = VersionHistory.Open(dotnetRoot, 1024, 12, legacyRoot);

            var versions = await store.ListAsync(source);
            var imported = Assert.Single(versions);
            Assert.Equal(legacyId, imported.Id);
            Assert.True(imported.IsLegacy);
            Assert.Equal(TextEncodingKind.Utf8Bom, imported.Encoding);
            Assert.Equal(bytes, await store.ReadAsync(source, imported));
            Assert.Equal(manifest, await File.ReadAllBytesAsync(manifestPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(legacyBlob));

            var diskStamp = await DocumentFiles.GetStampAsync(source);
            await store.CaptureExpectedAsync(source, TextEncodingKind.Utf8, diskStamp);
            var merged = await store.ListAsync(source);
            Assert.Equal(2, merged.Count);
            Assert.Single(merged, version => version.IsLegacy);
            var dotnetVersions = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(dotnetRoot, "manifest.json")));
            using (dotnetVersions)
            {
                var total = dotnetVersions.RootElement.EnumerateArray()
                    .Sum(entry => entry.GetProperty("bytes").GetInt64());
                Assert.True(total <= 12);
                Assert.True(total + imported.Bytes <= 12);
            }
            Assert.Equal(manifest, await File.ReadAllBytesAsync(manifestPath));
        });
    }

    [Fact]
    public async Task TotalQuota_PrunesOldHistoryAcrossFilesAndRejectsOversizedLatestSet()
    {
        await HeadlessTests.RunAsync(async profilePath =>
        {
            var store = VersionHistory.Open(Path.Combine(profilePath, "versions"), 100, 8);
            var first = Path.Combine(profilePath, "first.txt");
            var second = Path.Combine(profilePath, "second.txt");
            await File.WriteAllTextAsync(first, "aaaa");
            await store.CaptureAsync(first, TextEncodingKind.Utf8);
            await File.WriteAllTextAsync(second, "bbbb");
            await store.CaptureAsync(second, TextEncodingKind.Utf8);
            await File.WriteAllTextAsync(first, "cccc");
            await store.CaptureAsync(first, TextEncodingKind.Utf8);

            Assert.Single(await store.ListAsync(first));
            Assert.Single(await store.ListAsync(second));
            var manifestPath = Path.Combine(profilePath, "versions", "manifest.json");
            var beforeFailure = await File.ReadAllBytesAsync(manifestPath);
            using (var manifest = JsonDocument.Parse(beforeFailure))
            {
                var total = manifest.RootElement.EnumerateArray()
                    .Sum(entry => entry.GetProperty("bytes").GetInt64());
                Assert.True(total <= 8);
            }
            await File.WriteAllTextAsync(second, "12345");
            await Assert.ThrowsAsync<IOException>(() => store.CaptureAsync(second, TextEncodingKind.Utf8));

            Assert.Equal(beforeFailure, await File.ReadAllBytesAsync(manifestPath));
            Assert.Equal("cccc", DocumentFiles.DecodeText(await store.ReadAsync(first, Assert.Single(await store.ListAsync(first))), TextEncodingKind.Utf8));
            Assert.Equal("bbbb", DocumentFiles.DecodeText(await store.ReadAsync(second, Assert.Single(await store.ListAsync(second))), TextEncodingKind.Utf8));
            Assert.Equal("12345", await File.ReadAllTextAsync(second));
        });
    }

    [Fact]
    public async Task LegacySnapshot_WithoutEncodingRemainsReadableAsBytesButIsNotGuessed()
    {
        await HeadlessTests.RunAsync(async profilePath =>
        {
            var source = Path.Combine(profilePath, "legacy-unknown.txt");
            var legacyRoot = Path.Combine(profilePath, "RustText", "versions");
            var blobs = Path.Combine(legacyRoot, "blobs");
            Directory.CreateDirectory(blobs);
            var bytes = new byte[] { 0xCF, 0xF0 };
            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var id = "0000000000000002";
            await File.WriteAllBytesAsync(Path.Combine(blobs, checksum), bytes);
            await File.WriteAllTextAsync(source, "now");
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                settings = new { max_file_bytes = 1024, total_bytes = 2048 },
                sequence = 2,
                versions = new[]
                {
                    new { id, path = Path.GetFullPath(source), captured_millis = 9L,
                        bytes = (long)bytes.Length, checksum }
                }
            });
            await File.WriteAllBytesAsync(Path.Combine(legacyRoot, "manifest.json"), manifest);
            var store = VersionHistory.Open(Path.Combine(profilePath, "dotnet-demo", "versions"), 1024, 1024, legacyRoot);

            var version = Assert.Single(await store.ListAsync(source));
            Assert.Null(version.Encoding);
            Assert.Equal(bytes, await store.ReadAsync(source, version));
        });
    }

    [Theory]
    [InlineData("before\ninserted\nafter", "before\nafter", true, "inserted", false)]
    [InlineData("before\nafter", "before\nremoved\nafter", false, "removed", false)]
    [InlineData("before\nnew\nafter", "before\nold\nafter", true, "new", true)]
    public void Compare_DistinguishesInsertedDeletedAndReplacementLines(string current, string snapshot, bool isAdded, string line, bool hasOppositeLine)
    {
        var diff = VersionDiff.Compare(current, snapshot);

        var expected = isAdded ? VersionDiffKind.Added : VersionDiffKind.Removed;
        Assert.Contains(diff, item => item.Kind == expected && item.Text == line);
        var opposite = isAdded ? VersionDiffKind.Removed : VersionDiffKind.Added;
        Assert.Equal(hasOppositeLine, diff.Any(item => item.Kind == opposite));
    }

    [Fact]
    public void Compare_IdenticalCurrentAndSnapshotReportsNoDifferences()
    {
        var diff = VersionDiff.Compare("same\ntext", "same\ntext");

        Assert.Contains(diff, item => item.Kind == VersionDiffKind.Info && item.Text == "Отличий нет.");
        Assert.DoesNotContain(diff, item => item.Kind is VersionDiffKind.Added or VersionDiffKind.Removed);
    }

    [Fact]
    public void Compare_LateEditRemainsVisibleAfterThousandsOfUnchangedLines()
    {
        var prefix = string.Join('\n', Enumerable.Repeat("unchanged", 5_000));
        var current = prefix + "\ninserted late";
        var snapshot = prefix + "\nold late";

        var diff = VersionDiff.Compare(current, snapshot);

        Assert.Contains(diff, item => item.Kind == VersionDiffKind.Added && item.Text == "inserted late");
        Assert.Contains(diff, item => item.Kind == VersionDiffKind.Removed && item.Text == "old late");
        Assert.Contains(diff, item => item.Kind == VersionDiffKind.Info && item.Text.Contains("строк скрыто", StringComparison.Ordinal));
    }

    [Fact]
    public Task VersionDialog_DiffUsesUnsavedTextAndRestoreRemainsUndoable() => HeadlessTests.RunAsync(async profilePath =>
    {
        var source = Path.Combine(profilePath, "restore.txt");
        await File.WriteAllTextAsync(source, "A");
        var window = await OpenWorkspaceAsync(source);
        try
        {
            var tab = ActiveTab(window);
            var editor = Read<TextEditor>(tab, "Editor");
            editor.Document.Replace(0, editor.Document.TextLength, "B");
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal("B", await File.ReadAllTextAsync(source));

            var versions = VersionHistory.OpenDefault();
            var snapshots = await versions.ListAsync(source);
            VersionSnapshot? original = null;
            foreach (var version in snapshots)
            {
                if (DocumentFiles.DecodeText(await versions.ReadAsync(source, version), version.Encoding!.Value) == "A")
                    original = version;
            }
            var originalSnapshot = original ?? throw new InvalidOperationException("Initial snapshot A was not captured.");
            editor.Document.Replace(0, editor.Document.TextLength, "unsaved C");

            var dialogTask = CallAsync(window, "ShowVersionsAsync");
            await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
            var dialog = Assert.Single(window.OwnedWindows);
            var list = Assert.Single(dialog.GetVisualDescendants().OfType<ListBox>());
            var item = list.Items.OfType<ListBoxItem>().Single(candidate =>
                candidate.Tag is VersionSnapshot snapshot && snapshot == originalSnapshot);
            list.SelectedItem = item;
            var preview = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
            await HeadlessTests.WaitForAsync(() => preview.Text == "A");

            var compare = Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>());
            compare.IsChecked = true;
            var diff = dialog.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text is null && block.Inlines is { Count: > 0 });
            var inlineText = string.Concat(diff.Inlines!.OfType<Run>().Select(run => run.Text));
            Assert.Contains("+ unsaved C", inlineText, StringComparison.Ordinal);
            Assert.Contains("− A", inlineText, StringComparison.Ordinal);
            Assert.Equal("unsaved C", editor.Text);
            Assert.Equal("B", await File.ReadAllTextAsync(source));

            compare.IsChecked = false;
            Assert.Equal("A", preview.Text);
            Assert.Equal("unsaved C", editor.Text);
            Assert.Equal("B", await File.ReadAllTextAsync(source));

            var restore = dialog.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Восстановить в редакторе"));
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessTests.WaitForAsync(() => editor.Text == "A" && !dialog.IsVisible);
            await dialogTask;
            Assert.Equal("B", await File.ReadAllTextAsync(source));

            editor.TextArea.Focus();
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Equal("unsaved C", editor.Text);
            Assert.Equal("B", await File.ReadAllTextAsync(source));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task MixedEndings_ReplaceDeleteInsertAndUndoRedoPreserveDelimiterBytes() => HeadlessTests.RunAsync(async profilePath =>
    {
        var source = Path.Combine(profilePath, "mixed-endings.txt");
        var originalText = "one\r\ntwo\rtree\nfour";
        var originalBytes = Encoding.UTF8.GetBytes(originalText);
        await File.WriteAllBytesAsync(source, originalBytes);
        var window = await OpenWorkspaceAsync(source);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            var lineToDelete = editor.Text.IndexOf("two\n", StringComparison.Ordinal);
            editor.Document.Replace(lineToDelete, "two\n".Length, string.Empty);
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(Encoding.UTF8.GetBytes("one\r\ntree\nfour"), await File.ReadAllBytesAsync(source));

            editor.TextArea.Focus();
            PressUndo(window);
            Assert.Equal("one\ntwo\ntree\nfour", editor.Text);
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));

            PressRedo(window);
            Assert.Equal("one\ntree\nfour", editor.Text);
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(Encoding.UTF8.GetBytes("one\r\ntree\nfour"), await File.ReadAllBytesAsync(source));

            var insertAt = editor.Text.IndexOf("four", StringComparison.Ordinal);
            editor.Document.Insert(insertAt, "new\n");
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(Encoding.UTF8.GetBytes("one\r\ntree\nnew\nfour"), await File.ReadAllBytesAsync(source));

            PressUndo(window);
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(Encoding.UTF8.GetBytes("one\r\ntree\nfour"), await File.ReadAllBytesAsync(source));

            PressRedo(window);
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(Encoding.UTF8.GetBytes("one\r\ntree\nnew\nfour"), await File.ReadAllBytesAsync(source));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task ReplaceAll_RemovingSeparatedLinesPreservesUnchangedMixedEndings() => HeadlessTests.RunAsync(async profilePath =>
    {
        var source = Path.Combine(profilePath, "replace-all-mixed.txt");
        var originalBytes = Encoding.UTF8.GetBytes("keepA\r\nremove1\rkeepB\nremove2\r\nkeepC\rkeepD");
        await File.WriteAllBytesAsync(source, originalBytes);
        var window = await OpenWorkspaceAsync(source);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            Invoke(window, "OpenSearchPanel", false);
            ((TextBox)Field(window, "_searchInput")).Text = @"\n(?=remove)";
            ((TextBox)Field(window, "_replacement")).Text = string.Empty;
            ((ToggleButton)Field(window, "_regexOption")).IsChecked = true;

            await CallAsync(window, "ReplaceAsync", true);

            Assert.Equal("keepAremove1\nkeepBremove2\nkeepC\nkeepD", editor.Text);
            await CallAsync(window, "SaveAsync", false);
            var expectedBytes = Encoding.UTF8.GetBytes("keepAremove1\rkeepBremove2\r\nkeepC\rkeepD");
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(source));

            ((TextBox)Field(window, "_searchInput")).Text = "(?s).*";
            ((TextBox)Field(window, "_replacement")).Text = "$&";
            await CallAsync(window, "ReplaceAsync", true);
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(source));
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    [Fact]
    public Task RestoredFormat_UndoSaveRedoSaveKeepsOriginalBytesAndDoesNotDuplicateTheirEncoding() => HeadlessTests.RunAsync(async profilePath =>
    {
        var source = Path.Combine(profilePath, "format-history.txt");
        var oldText = "old\r\nformat\rlast\n";
        var oldUtf16Bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(oldText)).ToArray();
        await File.WriteAllBytesAsync(source, oldUtf16Bytes);
        var store = VersionHistory.OpenDefault();
        await store.CaptureAsync(source, TextEncodingKind.Utf16LittleEndian);

        var currentText = "current\r\nline\rend\n";
        var currentUtf8Bytes = Encoding.UTF8.GetBytes(currentText);
        await File.WriteAllBytesAsync(source, currentUtf8Bytes);
        var currentStamp = await DocumentFiles.GetStampAsync(source);
        await store.CaptureExpectedAsync(source, TextEncodingKind.Utf8, currentStamp);

        var window = await OpenWorkspaceAsync(source);
        try
        {
            var editor = Read<TextEditor>(ActiveTab(window), "Editor");
            await RestoreVersionAsync(window, editor, source, TextEncodingKind.Utf16LittleEndian, "old\nformat\nlast\n");
            Assert.Equal(currentUtf8Bytes, await File.ReadAllBytesAsync(source));

            editor.TextArea.Focus();
            PressUndo(window);
            Assert.Equal("current\nline\nend\n", editor.Text);
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(currentUtf8Bytes, await File.ReadAllBytesAsync(source));

            PressRedo(window);
            Assert.Equal("old\nformat\nlast\n", editor.Text);
            await CallAsync(window, "SaveAsync", false);
            Assert.Equal(oldUtf16Bytes, await File.ReadAllBytesAsync(source));

            var originalHash = Convert.ToHexString(SHA256.HashData(currentUtf8Bytes));
            var matchingVersions = (await store.ListAsync(source))
                .Where(version => version.Sha256.Equals(originalHash, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var matchingVersion = Assert.Single(matchingVersions);
            Assert.Equal(TextEncodingKind.Utf8, matchingVersion.Encoding);
        }
        finally
        {
            await CloseAsync(window);
        }
    });

    private static async Task RestoreVersionAsync(MainWindow window, TextEditor editor, string path,
        TextEncodingKind encoding, string expectedText)
    {
        var versions = await VersionHistory.OpenDefault().ListAsync(path);
        var target = Assert.Single(versions, version => version.Encoding == encoding);
        var dialogTask = CallAsync(window, "ShowVersionsAsync");
        await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Count == 1);
        var dialog = Assert.Single(window.OwnedWindows);
        var list = Assert.Single(dialog.GetVisualDescendants().OfType<ListBox>());
        var item = list.Items.OfType<ListBoxItem>().Single(candidate =>
            candidate.Tag is VersionSnapshot snapshot && snapshot == target);
        list.SelectedItem = item;
        var preview = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        await HeadlessTests.WaitForAsync(() => preview.Text?.ReplaceLineEndings("\n") == expectedText);

        var restore = dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Восстановить в редакторе"));
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessTests.WaitForAsync(() => editor.Text == expectedText && !dialog.IsVisible);
        await dialogTask;
    }

    private static void PressUndo(MainWindow window)
    {
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
    }

    private static void PressRedo(MainWindow window)
    {
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        window.KeyRelease(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static async Task<MainWindow> OpenWorkspaceAsync(params string[] paths)
    {
        var window = new MainWindow(paths);
        window.Show();
        await HeadlessTests.WaitForAsync(() => (bool)Field(window, "_restoreFinished"));
        return window;
    }

    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await HeadlessTests.WaitForAsync(() => !window.IsVisible);
    }

    private static object ActiveTab(MainWindow window) => Property(window, "ActiveTab", Private)!;
    private static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static Task CallAsync(MainWindow window, string name, params object?[] args) => (Task)Invoke(window, name, args)!;
    private static object? Property(object target, string name, BindingFlags binding = BindingFlags.Instance | BindingFlags.Public) =>
        target.GetType().GetProperty(name, binding)!.GetValue(target);
    private static T Read<T>(object target, string name) => (T)Property(target, name)!;
}
