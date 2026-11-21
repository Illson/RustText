using System.Globalization;
using System.IO.Pipes;
using System.Text;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class ElevatedShellCommandTests
{
    [Theory]
    [InlineData("cmd", "echo Привет & echo ошибка 1>&2 & set /p RustTextProbe= & if defined RustTextProbe (exit /b 1) else (exit /b 17)", 17)]
    [InlineData("powershell", "Write-Output 'Привет'; [Console]::Error.WriteLine('ошибка'); if ($null -eq [Console]::ReadLine()) { exit 23 }; exit 1", 23)]
    public async Task WorkerReturnsBothStreamsAndExitCodeWithClosedInput(string shell, string command, int exitCode)
    {
        var result = await RunWorkerAsync(Environment.ProcessId, shell, command);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains("Привет", result.Output);
        Assert.Contains("ошибка", result.Output);
    }

    [Fact]
    public async Task WorkerRejectsPipeOwnedByUnexpectedProcess()
    {
        var result = await RunWorkerAsync(Environment.ProcessId + 1, "cmd", "echo unexpected");

        Assert.Equal(125, result.ExitCode);
        Assert.Empty(result.Output);
    }

    private static async Task<(int? ExitCode, string Output)> RunWorkerAsync(int ownerPid, string shell, string command)
    {
        var pipeId = Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new NamedPipeServerStream("rusttext-elevated-" + pipeId,
            PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(timeout.Token);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        var owner = ownerPid.ToString(CultureInfo.InvariantCulture);
        var worker = Task.Run(() => ElevatedShellCommand.TryRunWorker(
            ["--run-elevated-shell-command", owner, pipeId, shell, encoded]));

        await connected;
        using var reader = new StreamReader(server, Encoding.UTF8);
        var output = await reader.ReadToEndAsync(timeout.Token);
        return (await worker.WaitAsync(timeout.Token), output);
    }
}
