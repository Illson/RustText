using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources["TextControlBackground"] = FocusTheme.Field;
        Resources["TextControlBackgroundPointerOver"] = FocusTheme.Field;
        Resources["TextControlBackgroundFocused"] = FocusTheme.Field;
        Resources["TextControlBorderBrush"] = FocusTheme.Border;
        Resources["TextControlPlaceholderForeground"] = FocusTheme.Muted;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(desktop.Args ?? []);
            _window = window;
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = window;
            TrackWindow(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static void TrackWindow(MainWindow window)
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime) return;

        window.Opened += (_, _) =>
        {
            _window = window;
            DrainRequests();
        };
        window.Activated += (_, _) => _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = window.FindOtherWindow();
                DrainRequests();
            }
        };
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
            while (window.IsVisible && Pending.TryDequeue(out var request))
            {
                await window.ReceivePathsAsync(request.AbsolutePaths, request.Activate);
                if (!window.IsVisible && _window is { IsVisible: true } next)
                {
                    window = next;
                }
            }
        }
        finally
        {
            _drainingRequests = false;
        }
    }
}
