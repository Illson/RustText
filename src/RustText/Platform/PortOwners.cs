using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;

namespace RustText.Demo.Platform;

internal static class PortOwners
{
    internal sealed record Binding(string Protocol, IPEndPoint Local, string State, int Pid);
    internal sealed record Match(Binding Binding, LocalProcesses.Owner Owner);

    [SupportedOSPlatform("windows")]
    public static async Task<Match[]> FindAsync(int port, CancellationToken token)
    {
        var bindings = await ReadAsync(port, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (bindings.Length == 0) return [];
        var owners = LocalProcesses.Describe(bindings.Select(binding => binding.Pid)).ToDictionary(owner => owner.Id);
        return bindings.Select(binding => new Match(binding, owners[binding.Pid])).ToArray();
    }

    [SupportedOSPlatform("windows")]
    public static async Task StopAsync(Match match, CancellationToken token)
    {
        var selected = match.Binding;
        var current = await ReadAsync(selected.Local.Port, token).ConfigureAwait(false);
        if (selected.Pid != match.Owner.Id || !current.Any(row =>
                row.Pid == selected.Pid && row.Protocol == selected.Protocol && row.Local.Equals(selected.Local)))
            throw new InvalidOperationException("Процесс больше не использует этот порт. Повторите поиск.");
        if (!LocalProcesses.TryForceClose(match.Owner, token))
            throw new InvalidOperationException("Процесс изменился, уже завершился или недоступен для завершения.");
    }

    private static async Task<Binding[]> ReadAsync(int port, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        token.ThrowIfCancellationRequested();
        // shortcut: netstat gives a snapshot; live tracking would need the Windows socket tables directly.
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netstat.exe"), "-qno")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Не удалось запустить netstat.");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Не удалось получить список портов: код netstat {process.ExitCode}.");
            return Parse(output, port);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Истекло время ожидания списка портов.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }

    internal static Binding[] Parse(string output, int port)
    {
        using var lines = new StringReader(output);
        var matches = new HashSet<Binding>();
        while (lines.ReadLine() is { } line)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[0] is not ("TCP" or "UDP") ||
                (fields[0] == "TCP" && fields.Length < 5) ||
                !IPEndPoint.TryParse(fields[1], out var local) || local.Port != port ||
                !int.TryParse(fields[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            matches.Add(new Binding(fields[0], local, string.Join(" ", fields[3..^1]), pid));
        }
        return matches.OrderBy(row => row.Protocol).ThenBy(row => row.Pid).ToArray();
    }
}
