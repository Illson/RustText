using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Headless;
using Xunit;

namespace RustText.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessCollection
{
    public const string Name = "Avalonia headless";
}

public static class HeadlessTests
{
    internal static int[] FindColorRows(Control control, int x, Color color)
    {
        var size = new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        using var pixels = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = pixels.Lock();
        bitmap.Render(control);
        bitmap.CopyPixels(frame);
        var packed = color.B | color.G << 8 | color.R << 16 | color.A << 24;
        return Enumerable.Range(0, size.Height).Where(y =>
            Marshal.ReadInt32(frame.Address + y * frame.RowBytes + x * 4) == packed).ToArray();
    }

    public static async Task RunAsync(Func<string, Task> action)
    {
        using var folder = new TempDirectory();
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessAppBuilder));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            await session.Dispatch(async () =>
            {
                const string variable = "RUSTTEXT_PROFILE_DIR";
                var previous = Environment.GetEnvironmentVariable(variable);
                Environment.SetEnvironmentVariable(variable, folder.Path);
                try
                {
                    await action(folder.Path);
                    return true;
                }
                finally
                {
                    Environment.SetEnvironmentVariable(variable, previous);
                }
            }, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            // Dispose waits for the dispatcher thread to finish. Dispatch
            // may complete on that thread, so give teardown a different one.
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }
    }

    public static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            await Task.Delay(15, timeout.Token);
        }
    }
}

public sealed class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<RustText.Demo.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("rusttext-tests-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
