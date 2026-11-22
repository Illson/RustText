using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RustText.Demo;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class LockedFileInspectionTests
{
    [Fact]
    public async Task IdentifiesTheFileAndItsParentFolderAsBlockedAndOnlyTerminatesAConfirmedProcess()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "lock.txt");
        await File.WriteAllTextAsync(path, "test fixture");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var holder = await StartOwnerAsync(path, timeout.Token);
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
        finally
        {
            if (!holder.HasExited)
            {
                await holder.StandardInput.WriteLineAsync();
                await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task DoesNotOfferToCloseEitherPossibleOwnerOfTheSameFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ambiguous.txt");
        await File.WriteAllTextAsync(path, "test fixture");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var first = await StartOwnerAsync(path, timeout.Token);
        using var second = await StartOwnerAsync(path, timeout.Token);
        try
        {
            var items = LockedFileInspection.Inspect(LockedFileInspection.Select(path), timeout.Token);
            var owners = FileLockProcesses.Describe(items);
            Assert.Equal(new[] { first.Id, second.Id }.Order(), owners.Select(owner => owner.Id));
            Assert.All(owners, owner => Assert.False(owner.CanClose));
        }
        finally
        {
            await first.StandardInput.WriteLineAsync();
            await second.StandardInput.WriteLineAsync();
            await first.WaitForExitAsync(timeout.Token);
            await second.WaitForExitAsync(timeout.Token);
        }
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

    internal static async Task<Process> StartOwnerAsync(string path, CancellationToken token)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $stream = [IO.File]::Open($env:RUSTTEXT_TEST_LOCK, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try {
                [Console]::WriteLine('ready')
                [void][Console]::ReadLine()
            } finally { $stream.Dispose() }
            """;
        var setup = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        setup.ArgumentList.Add("-NoProfile");
        setup.ArgumentList.Add("-NonInteractive");
        setup.ArgumentList.Add("-EncodedCommand");
        setup.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        setup.Environment["RUSTTEXT_TEST_LOCK"] = path;
        var process = Process.Start(setup)!;
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync(token));
            return process;
        }
        catch
        {
            process.StandardInput.Close();
            process.Dispose();
            throw;
        }
    }
}

[Collection(HeadlessCollection.Name)]
public sealed class RecycleDialogTests
{
    [Fact]
    public Task CancellingTheSecondConfirmationDoesNotKillTheLockOwnerOrStartDeletion() =>
        HeadlessTests.RunAsync(async _ =>
        {
            using var temp = new TempDirectory();
            var path = Path.Combine(temp.Path, "keep.txt");
            await File.WriteAllTextAsync(path, "fixture");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var owner = await LockedFileInspectionTests.StartOwnerAsync(path, timeout.Token);
            var window = new MainWindow([]);
            window.Show();
            try
            {
                await HeadlessTests.WaitForAsync(() => ReadFlag("_restoreFinished"));
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
                if (!owner.HasExited)
                {
                    await owner.StandardInput.WriteLineAsync();
                    await owner.WaitForExitAsync(timeout.Token);
                }
                await HeadlessTests.WaitForAsync(() => ReadFlag("_closed"));
            }
            Assert.Equal("fixture", await File.ReadAllTextAsync(path));

            bool ReadFlag(string name) => (bool)typeof(MainWindow).GetField(name, System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;

            async Task<Window> FindDialogAsync(string title)
            {
                await HeadlessTests.WaitForAsync(() => window.OwnedWindows.Any(dialog => dialog.Title == title));
                var dialog = Assert.Single(window.OwnedWindows, dialog => dialog.Title == title);
                dialog.UpdateLayout();
                return dialog;
            }

            static void Click(Window dialog, string label) =>
                Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, label))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
}
