using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RustText.Demo;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class LockedFileInspectionTests
{
    [Fact]
    public async Task IdentifiesTheFileAndItsParentFolderAsBlockedAndOnlyTerminatesAConfirmedProcess()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "lock.txt");
        await File.WriteAllTextAsync(path, "test fixture");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var fixture = await TestLockOwner.StartAsync(path, timeout.Token);
        var holder = fixture.Process;
        try
        {
            var file = LockedFileInspection.Select(path);
            var directory = LockedFileInspection.Select(temp.Path);
            var blockedFile = Assert.Single(LockedFileInspection.Inspect(file, timeout.Token));
            Assert.Equal(path, blockedFile.Path);
            Assert.Equal(holder.Id, Assert.Single(blockedFile.ProcessIds));

            var blockedChild = Assert.Single(LockedFileInspection.Inspect(directory, timeout.Token));
            Assert.Equal(path, blockedChild.Path);
            var owner = Assert.Single(FileLockProcesses.Describe([blockedChild]));
            Assert.True(owner.CanClose);
            Assert.Equal(holder.Id, owner.Id);

            FileLockProcesses.ForceClose([owner with { Started = owner.Started - 1 }], timeout.Token);
            Assert.False(holder.HasExited);
            FileLockProcesses.ForceClose([owner with { CanClose = false }], timeout.Token);
            Assert.False(holder.HasExited);
            Assert.True(File.Exists(path));

            FileLockProcesses.ForceClose([owner], timeout.Token);
            await holder.WaitForExitAsync(timeout.Token);
            while (LockedFileInspection.Inspect(file, timeout.Token).Count != 0)
                await Task.Delay(25, timeout.Token);
            Assert.Empty(LockedFileInspection.Inspect(file, timeout.Token));
            Assert.Empty(LockedFileInspection.Inspect(directory, timeout.Token));
            Assert.Equal("test fixture", await File.ReadAllTextAsync(path));
        }
        finally { await fixture.ReleaseAsync(); }
    }

    [Fact]
    public async Task DoesNotOfferToCloseEitherPossibleOwnerOfTheSameFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ambiguous.txt");
        await File.WriteAllTextAsync(path, "test fixture");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var first = await TestLockOwner.StartAsync(path, timeout.Token);
        await using var second = await TestLockOwner.StartAsync(path, timeout.Token);
        var items = LockedFileInspection.Inspect(LockedFileInspection.Select(path), timeout.Token);
        var owners = FileLockProcesses.Describe(items);
        Assert.Equal(new[] { first.Process.Id, second.Process.Id }.Order(), owners.Select(owner => owner.Id));
        Assert.All(owners, owner => Assert.False(owner.CanClose));
    }

    [Fact]
    public async Task DoesNotOfferToCloseAShareDeleteReaderWhenRustTextHasTheBlockingHandle()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "reader.txt");
        await File.WriteAllTextAsync(path, "test fixture");
        using var testLock = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var sharingReader = await TestLockOwner.StartAsync(path, timeout.Token, shareDelete: true);
        var target = LockedFileInspection.Select(path);
        var blocked = Assert.Single(LockedFileInspection.Inspect(target, timeout.Token));
        Assert.Contains(Environment.ProcessId, blocked.ProcessIds);
        Assert.Contains(sharingReader.Process.Id, blocked.ProcessIds);
        var owners = FileLockProcesses.Describe([blocked]);
        Assert.Contains(owners, owner => owner.Id == sharingReader.Process.Id && !owner.CanClose);
        Assert.DoesNotContain(owners, owner => owner.CanClose);

        testLock.Dispose();
        Assert.Empty(LockedFileInspection.Inspect(target, timeout.Token));
        Assert.False(sharingReader.Process.HasExited);
    }

    [Fact]
    public void RejectsReplacementAtTheSamePathAndRootOrStreamTargets()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "entry.txt");
        File.WriteAllText(path, "old");
        var original = LockedFileInspection.Select(path);
        File.Move(path, Path.Combine(temp.Path, "kept.txt"));
        File.WriteAllText(path, "new");

        Assert.Throws<IOException>(original.EnsureUnchanged);
        Assert.Throws<IOException>(() => LockedFileInspection.Select(Path.GetPathRoot(path)!));
        Assert.Throws<IOException>(() => LockedFileInspection.Select(path + ":stream"));
        Assert.True(LockedFileInspection.ContainsPath(temp.Path, path));
        Assert.False(LockedFileInspection.ContainsPath(temp.Path, temp.Path + "-other\\entry.txt"));
        Assert.Equal("new", File.ReadAllText(path));
    }

}

internal sealed class TestLockOwner(Process process, TempDirectory communication) : IAsyncDisposable
{
    public Process Process { get; } = process;

    public static async Task<TestLockOwner> StartAsync(string path, CancellationToken token, bool shareDelete = false)
    {
        var communication = new TempDirectory();
        TestLockOwner? owner = null;
        try
        {
            var ready = Path.Combine(communication.Path, "ready");
            var setup = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "RustText.Tests.runtimeconfig.json"),
                typeof(LockFixture.Program).Assembly.Location, path, ready, Path.Combine(communication.Path, "stop"), shareDelete.ToString()
            })
                setup.ArgumentList.Add(argument);

            owner = new TestLockOwner(Process.Start(setup)!, communication);
            while (!File.Exists(ready))
            {
                Assert.False(owner.Process.HasExited, "The test lock owner exited before opening the file.");
                await Task.Delay(25, token);
            }
            return owner;
        }
        catch
        {
            if (owner is null) communication.Dispose();
            else await owner.DisposeAsync();
            throw;
        }
    }

    public async Task ReleaseAsync()
    {
        if (Process.HasExited) return;
        await File.WriteAllTextAsync(Path.Combine(communication.Path, "stop"), "");
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        try { await ReleaseAsync(); }
        finally
        {
            Process.Dispose();
            communication.Dispose();
        }
    }
}

[Collection(HeadlessCollection.Name)]
public sealed class RecycleDialogTests
{
    [Fact]
    public Task RefusesWorkspaceDataBeforeOfferingDeletion() => HeadlessTests.RunAsync(async profile =>
    {
        var draftDirectory = Directory.CreateDirectory(Path.Combine(profile, "workspaces", "fixture", "windows", "primary",
            "dotnet-demo", "drafts")).FullName;
        var draft = Path.Combine(draftDirectory, "untitled.txt");
        await File.WriteAllTextAsync(draft, "private draft");
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
            await Assert.ThrowsAsync<IOException>(() => window.RecyclePathAsync(draftDirectory));
            await Assert.ThrowsAsync<IOException>(() => window.RecyclePathAsync(draft));
            Assert.Empty(window.OwnedWindows);
            Assert.Equal("private draft", await File.ReadAllTextAsync(draft));
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
        }
    });

    [Fact]
    public Task CancellingTheSecondConfirmationDoesNotKillTheLockOwnerOrStartDeletion() =>
        HeadlessTests.RunAsync(async _ =>
        {
            using var temp = new TempDirectory();
            var path = Path.Combine(temp.Path, "keep.txt");
            await File.WriteAllTextAsync(path, "fixture");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await using var fixture = await TestLockOwner.StartAsync(path, timeout.Token);
            var owner = fixture.Process;
            var window = new MainWindow([]);
            window.Show();
            try
            {
                await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
                var remove = window.RecyclePathAsync(path);
                Click(await FindDialogAsync("Удалить с диска?"), "Удалить");
                var blocked = await FindDialogAsync("Объект занят");
                Assert.Contains(blocked.GetVisualDescendants().OfType<TextBlock>(), text =>
                    text.Text?.Contains($"PID {owner.Id}", StringComparison.Ordinal) == true);
                Click(blocked, "Освободить и удалить");
                var force = await FindDialogAsync("Принудительное завершение");
                Assert.False(owner.HasExited);
                Click(force, "Отмена");
                await remove;
                Assert.False(owner.HasExited);
                Assert.True(File.Exists(path));
            }
            finally
            {
                foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
                window.Close();
                await fixture.ReleaseAsync();
                await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
            }
            Assert.Equal("fixture", await File.ReadAllTextAsync(path));

            Task<Window> FindDialogAsync(string title) => WaitForDialogAsync(window, title);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnreleasableChildPreventsClosingOtherOwners(bool blockAfterForcePrompt) => HeadlessTests.RunAsync(async _ =>
    {
        using var temp = new TempDirectory();
        var first = Path.Combine(temp.Path, "first.txt");
        var second = Path.Combine(temp.Path, "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await using var fixture = await TestLockOwner.StartAsync(second, timeout.Token);
        using var initial = blockAfterForcePrompt ? null : HoldFirst();
        var window = new MainWindow([]);
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_restoreFinished"));
            var remove = window.RecyclePathAsync(temp.Path);
            Click(await WaitForDialogAsync(window, "Удалить с диска?"), "Удалить");
            if (blockAfterForcePrompt)
            {
                Click(await WaitForDialogAsync(window, "Объект занят"), "Освободить и удалить");
                var force = await WaitForDialogAsync(window, "Принудительное завершение");
                using var changed = HoldFirst();
                Click(force, "Завершить и удалить");
                await DismissBlockedAsync();
            }
            else await DismissBlockedAsync();
            await remove;
            Assert.False(fixture.Process.HasExited);
            Assert.Equal("first", await File.ReadAllTextAsync(first));
            Assert.Equal("second", await File.ReadAllTextAsync(second));
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await fixture.ReleaseAsync();
            await HeadlessTests.WaitForAsync(() => ReadFlag(window, "_closed"));
        }
        FileStream HoldFirst() => new(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        async Task DismissBlockedAsync()
        {
            var blocked = await WaitForDialogAsync(window, "Объект остаётся занят");
            Assert.Single(blocked.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Закрыть"));
            Assert.DoesNotContain(blocked.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Завершить и удалить"));
            Click(blocked, "Закрыть");
        }
    });

    private static async Task<Window> WaitForDialogAsync(MainWindow window, string title)
    {
        await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog => dialog.Title == title));
        var dialog = Assert.Single(window.OwnedWindows, dialog => dialog.Title == title);
        dialog.UpdateLayout();
        return dialog;
    }

    private static void Click(Window dialog, string label) =>
        Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, label))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool ReadFlag(MainWindow window, string name) => (bool)typeof(MainWindow)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
}
