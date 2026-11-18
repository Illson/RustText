using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RustText.Demo.Platform;

namespace RustText.Demo;

internal sealed class App : Application
{
    private static readonly ConcurrentQueue<LaunchRequest> Pending = new();
    private static MainWindow? _window;
    private static bool _drainingRequests;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(desktop.Args ?? []);
            _window = window;
            desktop.MainWindow = window;
            window.Opened += (_, _) => DrainRequests();
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static Task ForwardAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pending.Enqueue(request);
        Dispatcher.UIThread.Post(DrainRequests);
        return Task.CompletedTask;
    }

    private static async void DrainRequests()
    {
        if (_drainingRequests || _window is not { IsVisible: true } window)
        {
            return;
        }

        _drainingRequests = true;
        try
        {
            while (Pending.TryDequeue(out var request))
            {
                await window.ReceivePathsAsync(request.AbsolutePaths, request.Activate);
            }
        }
        finally
        {
            _drainingRequests = false;
        }
    }
}
