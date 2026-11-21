using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RustText.Demo.Platform;

internal static partial class ElevatedShellCommand
{
    private const string WorkerArgument = "--run-elevated-shell-command";
    private const string PipePrefix = "rusttext-elevated-";
    private const int MaximumCommandBytes = 3072;
    private const int ErrorCancelled = 1223;
    private const int WorkerError = 125;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly Encoding Utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    public static async Task<int?> RunAsync(ShellKind shell, string command, Func<string, Task> appendOutput,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Повышение прав доступно в Windows.");
        if (!IsValidCommand(command))
            throw new ArgumentException("Введите команду размером до 3072 байт.");

        var pipeId = Guid.NewGuid().ToString("N");
        using var server = CreateOutputServer(PipePrefix + pipeId);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var closeOnCancel = cancellationToken.Register(server.Dispose);
        var connected = server.WaitForConnectionAsync(connection.Token);
        try
        {
            var info = CreateWorkerStartInfo(shell, command, pipeId);
            using var worker = await Task.Run(() => Process.Start(info), cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Не удалось запустить команду от имени администратора.");
            cancellationToken.ThrowIfCancellationRequested();
            connection.CancelAfter(ConnectionTimeout);
            try
            {
                await connected.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Повышенный процесс не подключился для передачи вывода.");
            }

            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientId) || clientId != worker.Id)
                throw new IOException("Подключился неизвестный процесс.");

            using var reader = new StreamReader(server, Utf8, detectEncodingFromByteOrderMarks: false);
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                await appendOutput(new string(buffer, 0, count)).ConfigureAwait(false);

            await worker.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return worker.ExitCode;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            await connection.CancelAsync().ConfigureAwait(false);
            try { await connected.ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // The connection result is handled above, if the worker was started.
            }
        }
    }

    private static ProcessStartInfo CreateWorkerStartInfo(ShellKind shell, string command, string pipeId)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Путь RustText недоступен.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.SystemDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        info.ArgumentList.Add(WorkerArgument);
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(pipeId);
        info.ArgumentList.Add(shell == ShellKind.Cmd ? "cmd" : "powershell");
        info.ArgumentList.Add(Convert.ToBase64String(Utf8.GetBytes(command)));
        return info;
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateOutputServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Не удалось определить учётную запись Windows.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 0, security);
    }

    public static int? TryRunWorker(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerArgument) return null;
        if (!OperatingSystem.IsWindows()
            || args is not [_, var owner, var pipeId, "cmd" or "powershell", var encoded]
            || !uint.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId)
            || ownerId == 0
            || !Guid.TryParseExact(pipeId, "N", out _)
            || encoded.Length > (MaximumCommandBytes + 2) / 3 * 4)
        {
            return WorkerError;
        }

        try
        {
            var command = Utf8.GetString(Convert.FromBase64String(encoded));
            if (!IsValidCommand(command)) return WorkerError;

            using var client = new NamedPipeClientStream(".", PipePrefix + pipeId, PipeDirection.Out,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            client.Connect(checked((int)ConnectionTimeout.TotalMilliseconds));
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverId) || serverId != ownerId)
                return WorkerError;

            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var shell = args[3] == "cmd" ? ShellKind.Cmd : ShellKind.PowerShell;
            try
            {
                return ExecuteAsync(shell, command, writer).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException
                or UnauthorizedAccessException)
            {
                writer.WriteLine(error.Message);
                return WorkerError;
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or TimeoutException
            or ArgumentException or FormatException or UnauthorizedAccessException)
        {
            return WorkerError;
        }
    }

    private static bool IsValidCommand(string command) =>
        !string.IsNullOrWhiteSpace(command) && !command.Contains('\0') && Utf8.GetByteCount(command) <= MaximumCommandBytes;

    private static async Task<int> ExecuteAsync(ShellKind shell, string command, StreamWriter output)
    {
        var system = Environment.SystemDirectory;
        var info = new ProcessStartInfo(ShellTab.GetExecutablePath(ShellKind.PowerShell))
        {
            WorkingDirectory = system,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        const string setup = """
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
            $ProgressPreference = 'SilentlyContinue'
            """;
        var commandScript = shell == ShellKind.Cmd ? CreateCmdScript(command) : "& {\n" + command + "\n}";
        var script = setup + "\n" + commandScript;
        string[] arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить оболочку.");
        process.StandardInput.Close();
        using var writeLock = new SemaphoreSlim(1);
        await Task.WhenAll(CopyOutputAsync(process.StandardOutput), CopyOutputAsync(process.StandardError),
            process.WaitForExitAsync()).ConfigureAwait(false);
        return process.ExitCode;

        async Task CopyOutputAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            var connected = true;
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                if (!connected) continue;
                await writeLock.WaitAsync().ConfigureAwait(false);
                try { await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false); }
                catch (IOException) { connected = false; }
                finally { writeLock.Release(); }
            }
        }
    }

    private static string CreateCmdScript(string command)
    {
        // Start CMD after setting its inherited console code page. CMD can cache the code page
        // for redirected output, so changing it inside the same /C command loses characters.
        var arguments = Convert.ToBase64String(Encoding.Unicode.GetBytes($"/D /S /C \"{command}\""));
        return $$"""
            $ErrorActionPreference = 'Stop'
            $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::Combine([Environment]::SystemDirectory, 'cmd.exe'))
            $start.Arguments = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{arguments}}'))
            $start.UseShellExecute = $false
            $start.RedirectStandardInput = $true
            $child = [Diagnostics.Process]::Start($start)
            try {
                $child.StandardInput.Close()
                $child.WaitForExit()
                exit $child.ExitCode
            } finally { $child.Dispose() }
            """;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
