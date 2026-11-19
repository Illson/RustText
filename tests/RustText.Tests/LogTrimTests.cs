using System.Reflection;
using System.Text;
using RustText.Demo.Logs;
using Xunit;

namespace RustText.Tests;

public sealed class LogTrimTests
{
    [Fact]
    public void PhysicalTrimKeepsBomAndCreatesByteCompleteBackup()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "example.log");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        const string beginning = "old\r\n";
        const string tail = "оставить\nпоследняя строка";
        File.WriteAllText(path, beginning + tail, encoding);
        var original = File.ReadAllBytes(path);
        var stamp = ReadStamp(path);
        var prefixLength = (long)encoding.GetPreamble().Length;
        var boundary = prefixLength + encoding.GetByteCount(beginning);

        var backup = (string)Invoke("TrimFile", path, boundary, prefixLength, stamp, CancellationToken.None)!;

        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(tail)), File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.trim"));
    }

    [Fact]
    public void CancelledTrimLeavesTheLogAndDirectoryUnchanged()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "example.log");
        File.WriteAllText(path, "old\nkept\n");
        var original = File.ReadAllBytes(path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var failure = Assert.Throws<TargetInvocationException>(
            () => Invoke("TrimFile", path, 4L, 0L, ReadStamp(path), cancelled.Token));

        Assert.IsType<OperationCanceledException>(failure.InnerException);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(path, Assert.Single(Directory.EnumerateFiles(directory.Path)));
    }

    [Fact]
    public void ReplacementAfterStagingIsNeverOverwritten()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "example.log");
        var rotated = Path.Combine(directory.Path, "rotated.log");
        var backup = Path.Combine(directory.Path, "example.log.backup");
        var staged = Path.Combine(directory.Path, "trim-stage");
        File.WriteAllText(path, "old\nkept\n");
        File.Copy(path, backup);
        var original = File.ReadAllBytes(backup);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var stamp = Invoke("ReadFileStamp", path, held)!;
        File.WriteAllText(staged, "kept\n");
        File.Move(path, rotated);
        File.WriteAllText(path, "new\nlogs\n");

        var failure = Assert.Throws<TargetInvocationException>(
            () => Invoke("CommitTrim", path, staged, held, stamp, CancellationToken.None));

        Assert.Contains("Файл изменился во время обрезки", Assert.IsType<IOException>(failure.InnerException).Message);
        Assert.Equal("new\nlogs\n", File.ReadAllText(path));
        Assert.Equal(original, File.ReadAllBytes(rotated));
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    private static object ReadStamp(string path)
    {
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Invoke("ReadFileStamp", path, handle)!;
    }

    private static object? Invoke(string name, params object[] arguments) =>
        typeof(LogView).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);
}
