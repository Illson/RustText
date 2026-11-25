using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RustText.Demo.Logs;

namespace RustText.Demo.Platform;

internal sealed class ConsoleCapture : IDisposable
{
    private const string WorkerArgument = "--read-console-buffer";
    // ConsoleScreenBuffer removes embedded NULs, so the worker can use one as a row suffix.
    private const char RightEdgeMarker = '\0';
    private const long MaximumCaptureBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FileStream _file;
    private bool _disposed;

    internal ConsoleCapture(string title)
    {
        Title = title;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rusttext-console-{Guid.NewGuid():N}.log");
        _file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
            16 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }

    public string Path { get; }
    public string Title { get; }
    public Task<Exception?> Completion { get; private set; } = Task.FromResult<Exception?>(null);
    public bool IsDisposed => _disposed;

    public static async Task<ConsoleCapture> ConnectAsync(int processId, string title, CancellationToken token)
    {
        var capture = new ConsoleCapture(title);
        try
        {
            capture.Completion = capture.PumpAsync(processId, capture._stop.Token);
            await capture._ready.Task.WaitAsync(ConnectionTimeout, token).ConfigureAwait(false);
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private async Task<Exception?> PumpAsync(int processId, CancellationToken token)
    {
        Process? worker = null;
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Путь RustText недоступен.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Utf8
            };
            if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add(WorkerArgument);
            start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
            worker = Process.Start(start) ?? throw new IOException("Не удалось запустить чтение консоли.");
            // The input pipe also tells the helper to exit if an output read is still pending.
            using var disconnect = token.Register(worker.StandardInput.BaseStream.Dispose);
            using var output = worker.StandardOutput;
            using var writer = new StreamWriter(_file, Utf8, leaveOpen: true);
            string? pendingJoin = null;
            while (await output.ReadLineAsync(token).ConfigureAwait(false) is { } header)
            {
                if (!int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    throw new IOException(header);
                var (completed, nextJoin) =
                    await CopyFrameAsync(output, writer, count, MaximumCaptureBytes, token, pendingJoin).ConfigureAwait(false);
                pendingJoin = nextJoin;
                _ready.TrySetResult();
                if (!completed) return new IOException("Достигнут предел захвата (256 МиБ). Собранный текст доступен для поиска.");
            }
            if (worker is null) throw new IOException("Чтение вывода консоли завершено.");
            await worker.WaitForExitAsync(token).ConfigureAwait(false);
            if (!_ready.Task.IsCompletedSuccessfully)
                throw new IOException("Чтение вывода консоли завершено до получения первого кадра.");
            if (worker.ExitCode != 0) throw new IOException("Чтение вывода консоли завершено с ошибкой.");
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ready.TrySetCanceled(token);
            return null;
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException
            or UnauthorizedAccessException)
        {
            _ready.TrySetException(error);
            return error;
        }
        finally
        {
            if (worker is not null)
            {
                using (worker)
                {
                    try
                    {
                        worker.StandardInput.BaseStream.Dispose();
                        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
                    {
                        try { if (!worker.HasExited) worker.Kill(); }
                        catch (Exception stopError) when (stopError is InvalidOperationException or Win32Exception) { }
                    }
                }
            }
        }
    }

    internal static async Task<(bool Completed, string? PendingJoin)> CopyFrameAsync(TextReader source,
        StreamWriter writer, int count, long maximumBytes, CancellationToken token, string? pendingJoin = null)
    {
        var length = writer.BaseStream.Length;
        var newlineBytes = writer.Encoding.GetByteCount(writer.NewLine);
        for (var index = 0; index < count; index++)
        {
            var row = await source.ReadLineAsync(token).ConfigureAwait(false)
                ?? throw new IOException("Поток вывода консоли прервался.");
            string? nextJoin = null;
            if (row.EndsWith(RightEdgeMarker))
            {
                // Keep the edge space only if the next row actually continues this one.
                nextJoin = row.Length > 1 && row[^2] == ' ' ? " " : "";
                row = row[..^(1 + nextJoin.Length)];
            }
            var separator = pendingJoin is null || row.Length == 0 ? ""
                : LogLevels.StartsRecord(row) ? writer.NewLine : pendingJoin;
            length += (long)writer.Encoding.GetByteCount(row) + writer.Encoding.GetByteCount(separator)
                + (nextJoin is null ? newlineBytes : 0);
            if (length > maximumBytes)
            {
                await writer.FlushAsync(token).ConfigureAwait(false);
                return (false, pendingJoin);
            }
            if (separator.Length > 0) await writer.WriteAsync(separator.AsMemory(), token).ConfigureAwait(false);
            if (nextJoin is null) await writer.WriteLineAsync(row.AsMemory(), token).ConfigureAwait(false);
            else await writer.WriteAsync(row.AsMemory(), token).ConfigureAwait(false);
            pendingJoin = nextJoin;
        }
        await writer.FlushAsync(token).ConfigureAwait(false);
        return (true, pendingJoin);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _ = DisposeAfterPumpAsync();
    }

    private async Task DisposeAfterPumpAsync()
    {
        try { await Completion.ConfigureAwait(false); }
        finally
        {
            _file.Dispose();
            _stop.Dispose();
        }
    }

    public static int? TryRunWorker(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerArgument) return null;
        if (!OperatingSystem.IsWindows() || args.Length != 2
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            || processId <= 0 || processId == Environment.ProcessId)
        {
            return 1;
        }
        try
        {
            using var parent = new StreamReader(Console.OpenStandardInput());
            using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8);
            // AttachConsole changes process-wide console state, so only the helper attaches.
            var disconnected = parent.ReadLineAsync();
            try
            {
                using var target = Process.GetProcessById(processId);
                using var buffer = new ConsoleScreenBuffer(processId);
                var initial = true;
                while (!disconnected.IsCompleted)
                {
                    var sourceExited = target.HasExited;
                    var rows = buffer.ReadNewRows(CancellationToken.None);
                    if (initial || rows.Length > 0)
                    {
                        output.WriteLine(rows.Length.ToString(CultureInfo.InvariantCulture));
                        foreach (var row in rows)
                        {
                            output.Write(row.Text);
                            if (row.MayWrap) output.Write(RightEdgeMarker);
                            output.WriteLine();
                        }
                        output.Flush();
                        initial = false;
                    }
                    if (sourceExited || disconnected.Wait(PollInterval)) break;
                }
                return 0;
            }
            catch (Exception error) when (error is Win32Exception or ArgumentException)
            {
                output.WriteLine(error.Message);
                output.Flush();
                return 1;
            }
        }
        catch (IOException)
        {
            return 1;
        }
    }
}
