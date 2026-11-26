using System.Collections.Concurrent;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForwardingGrantsForegroundOnlyForActivationBeforeDeliveringPaths(bool activate)
    {
        using var profile = new TempDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var grants = new ConcurrentQueue<uint>();
        var received = new TaskCompletionSource<(LaunchRequest Request, uint[] Grants)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new LaunchRequest(profile.Path, ["notes.txt"], activate);

        await using var primary = await SingleInstanceCoordinator.StartAsync(profile.Path, request, (forwarded, _) =>
        {
            received.TrySetResult((forwarded, grants.ToArray()));
            return Task.CompletedTask;
        }, timeout.Token);
        Assert.NotNull(primary);

        await using var secondary = await SingleInstanceCoordinator.StartAsync(profile.Path, request,
            (_, _) => Task.CompletedTask, pid =>
            {
                grants.Enqueue(pid);
                return false;
            }, timeout.Token);

        Assert.Null(secondary);
        var (forwardedRequest, grantsBeforeDelivery) = await received.Task.WaitAsync(timeout.Token);
        Assert.Equal(activate, forwardedRequest.Activate);
        Assert.Equal(request.AbsolutePaths, forwardedRequest.AbsolutePaths);
        if (activate) Assert.Equal((uint)Environment.ProcessId, Assert.Single(grantsBeforeDelivery));
        else Assert.Empty(grants);
    }
}
