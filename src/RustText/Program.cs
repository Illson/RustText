using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using RustText.Demo.Platform;

namespace RustText.Demo;

internal static class Program
{
    private static long _measurementStart;
    private static int _firstFrame;

    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("RUSTTEXT_MEASURE") is not null)
        {
            _measurementStart = Stopwatch.GetTimestamp();
        }

        if (UpdateService.TryHandleApplyArguments(args) is { } updateExit)
        {
            return updateExit;
        }

        if (args is ["--register-file-associations"])
        {
            try
            {
                FileAssociations.RegisterCurrentUser(Environment.ProcessPath ?? throw new IOException("Путь программы недоступен."));
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception or ArgumentException or System.Security.SecurityException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        SingleInstanceCoordinator? coordinator;
        try
        {
            var storage = StorageService.OpenDefault();
            coordinator = SingleInstanceCoordinator.StartAsync(storage.RootDirectory,
                new LaunchRequest(Environment.CurrentDirectory, args),
                App.ForwardAsync).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException
            or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }

        if (coordinator is null)
        {
            return 0;
        }

        try
        {
            try
            {
                NativePayloadBootstrap.EnsureLoaded();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception or DllNotFoundException or BadImageFormatException
                or System.Security.SecurityException or CryptographicException)
            {
                Console.Error.WriteLine($"Не удалось подготовить графические библиотеки RustText. {error.Message}");
                return 1;
            }

            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    internal static void MarkFirstLogFrame()
    {
        if (_measurementStart != 0 && Interlocked.Exchange(ref _firstFrame, 1) == 0)
        {
            Console.WriteLine($"RUSTTEXT first_log_frame_ms={(long)Stopwatch.GetElapsedTime(_measurementStart).TotalMilliseconds}");
        }
    }
}
