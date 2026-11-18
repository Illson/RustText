using System.Diagnostics;
using Avalonia;

namespace RustText.Demo;

internal static class Program
{
    internal static long? MeasurementStart { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("RUSTTEXT_MEASURE") is not null)
        {
            MeasurementStart = Stopwatch.GetTimestamp();
        }

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}
