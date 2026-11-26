using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace RustText.Demo.Platform;

public sealed partial class SingleInstanceCoordinator : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private const int MaximumMessageBytes = 256 * 1024;
    private const int MaximumPathCount = 512;
    private static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectAttempt = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);

    private readonly string _pipeName;
    private readonly FileStream _instanceLock;
    private readonly Func<LaunchRequest, CancellationToken, Task> _onRequest;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listener;
    private bool _disposed;

    private SingleInstanceCoordinator(
        string pipeName,
        FileStream instanceLock,
        Func<LaunchRequest, CancellationToken, Task> onRequest)
    {
        _pipeName = pipeName;
        _instanceLock = instanceLock;
        _onRequest = onRequest;
        _listener = ListenAsync(_shutdown.Token);
    }

    /// <summary>
    /// Starts the primary instance or forwards its launch request to the existing owner.
    /// The primary coordinator must be disposed when the application exits.
    /// </summary>
    public static Task<SingleInstanceCoordinator?> StartAsync(
        string profileDirectory,
        LaunchRequest initialRequest,
        Func<LaunchRequest, CancellationToken, Task> onRequest,
        CancellationToken cancellationToken = default) =>
        StartAsync(profileDirectory, initialRequest, onRequest, AllowSetForegroundWindow, cancellationToken);

    internal static async Task<SingleInstanceCoordinator?> StartAsync(
        string profileDirectory,
        LaunchRequest initialRequest,
        Func<LaunchRequest, CancellationToken, Task> onRequest,
        Func<uint, bool> allowForeground,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentNullException.ThrowIfNull(initialRequest);
        ArgumentNullException.ThrowIfNull(onRequest);
        initialRequest.Validate();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Single-instance coordination is available on Windows.");
        }

        var canonicalProfile = Path.GetFullPath(profileDirectory);
        Directory.CreateDirectory(canonicalProfile);
        var pipeName = CreatePipeName(canonicalProfile);
        var lockPath = Path.Combine(canonicalProfile, "instance.lock");
        var deadline = DateTime.UtcNow + StartupRetry;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instanceLock = TryAcquireLock(lockPath);
            if (instanceLock is not null)
            {
                return new SingleInstanceCoordinator(pipeName, instanceLock, onRequest);
            }

            try
            {
                await ForwardAsync(pipeName, initialRequest, allowForeground, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new IOException("The existing RustText instance did not accept the launch request.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _listener.ConfigureAwait(false);
        }
        finally
        {
            _instanceLock.Dispose();
            _shutdown.Dispose();
        }
    }

    private static FileStream? TryAcquireLock(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (stream.Length == 0)
            {
                stream.SetLength(1);
            }

            stream.Lock(0, 1);
            return stream;
        }
        catch (IOException)
        {
            stream.Dispose();
            return null;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                MaximumMessageBytes,
                4);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(MessageTimeout);
                var request = await ReadRequestAsync(pipe, readTimeout.Token).ConfigureAwait(false);
                await _onRequest(request, cancellationToken).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and
                not StackOverflowException and
                not AccessViolationException)
            {
                if (pipe.IsConnected)
                {
                    try
                    {
                        await pipe.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
    }

    private static async Task<LaunchRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The launch request size is outside the supported limit.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize(payload, SingleInstanceJsonContext.Default.LaunchEnvelope)
            ?? throw new InvalidDataException("The launch request was empty.");
        if (envelope.Version != ProtocolVersion || envelope.Request is null)
        {
            throw new InvalidDataException("The launch request protocol is unsupported.");
        }

        var request = new LaunchRequest(envelope.Request.WorkingDirectory, envelope.Request.Paths, envelope.Request.Activate);
        request.Validate();
        return request;
    }

    private static async Task ForwardAsync(
        string pipeName,
        LaunchRequest request,
        Func<uint, bool> allowForeground,
        CancellationToken cancellationToken)
    {
        var envelope = new LaunchEnvelope(ProtocolVersion, request);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, SingleInstanceJsonContext.Default.LaunchEnvelope);
        if (payload.Length is 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("The launch request exceeds the supported message size.");
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(ConnectAttempt);
        try
        {
            await pipe.ConnectAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The primary instance pipe is not ready yet.", exception);
        }

        // A launcher started from Explorer can pass its foreground permission to the running instance.
        if (request.Activate && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId))
        {
            _ = allowForeground(serverId);
        }

        using var messageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        messageTimeout.CancelAfter(MessageTimeout);
        try
        {
            var prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
            await pipe.WriteAsync(prefix, messageTimeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(payload, messageTimeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(messageTimeout.Token).ConfigureAwait(false);

            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, messageTimeout.Token).ConfigureAwait(false);
            if (acknowledgement[0] != 1)
            {
                throw new IOException("The primary instance rejected the launch request.");
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The primary instance did not acknowledge the launch request in time.", exception);
        }
    }

    private static string CreatePipeName(string profileDirectory)
    {
        var canonical = profileDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant()));
        return "RustText-" + Convert.ToHexString(hash.AsSpan(0, 16));
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}

internal sealed record LaunchEnvelope(int Version, LaunchRequest Request);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LaunchEnvelope))]
internal partial class SingleInstanceJsonContext : JsonSerializerContext;
