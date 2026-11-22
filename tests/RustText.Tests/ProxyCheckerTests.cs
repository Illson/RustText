using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RustText.Demo.Platform;
using Xunit;

namespace RustText.Tests;

public sealed class ProxyCheckerTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks5")]
    public async Task UsesSelectedProtocolOnlyForAddressesWithoutAnExplicitScheme(string defaultScheme)
    {
        var defaultProtocol = Enum.Parse<ProxyProtocol>(defaultScheme, ignoreCase: true);
        var results = new ConcurrentDictionary<int, ProxyCheckResult>();
        var probes = new ConcurrentDictionary<string, NetworkCredential?>();
        await ProxyChecker.CheckAsync(
            " 192.0.2.18:80 \r\n\r\nHTTP://198.51.100.42:8080\nsocks5://user:p%40ss@[2001:db8::1]:1080",
            defaultProtocol, TimeSpan.FromSeconds(5), 2, result => results[result.LineNumber] = result,
            CancellationToken.None, (uri, credentials, _) =>
            {
                probes[uri.Scheme + "://" + uri.Host + ":" + uri.Port] = credentials;
                Assert.Empty(uri.UserInfo);
                return Task.FromResult(HttpStatusCode.NoContent);
            });

        Assert.Equal(new[] { 1, 3, 4 }, results.Keys.Order());
        Assert.Equal(defaultProtocol, results[1].Protocol);
        Assert.Equal($"{defaultScheme}://192.0.2.18:80", results[1].CopyValue);
        Assert.Equal(ProxyProtocol.Http, results[3].Protocol);
        Assert.Equal(ProxyProtocol.Socks5, results[4].Protocol);
        Assert.Equal("[2001:db8::1]:1080", results[4].Address);
        Assert.Equal("socks5://user:p%40ss@[2001:db8::1]:1080", results[4].CopyValue);
        Assert.DoesNotContain("p%40ss", results[4].ToString());
        Assert.Equal("p@ss", probes["socks5://[2001:db8::1]:1080"]?.Password);
        Assert.Null(probes[$"{defaultScheme}://192.0.2.18:80"]);
        Assert.All(results.Values, result =>
        {
            Assert.Equal(ProxyCheckStatus.Working, result.Status);
            Assert.NotNull(result.ElapsedMilliseconds);
        });
    }

    [Theory]
    [InlineData("http://192.0.2.18")]
    [InlineData("192.0.2.18")]
    [InlineData("192.0.2.18:0")]
    [InlineData("192.0.2.18:65536")]
    [InlineData("192.0.2.18:80/path")]
    [InlineData("ftp://user:secret@192.0.2.18:80")]
    [InlineData("user:secret@192.0.2.18:not-a-port")]
    [InlineData("user:secret@unexpected@192.0.2.18:80")]
    [InlineData("user%00:secret@192.0.2.18:80")]
    [InlineData("proxy.example:3000:login")]
    [InlineData("proxy.example:3000::secret")]
    [InlineData("proxy.example:65536:login:secret")]
    public async Task ReportsMalformedAddressesWithoutProbingOrReturningTheirContents(string input)
    {
        var results = new ConcurrentBag<ProxyCheckResult>();
        await ProxyChecker.CheckAsync(input, ProxyProtocol.Socks5, TimeSpan.FromSeconds(5), 1, results.Add,
            CancellationToken.None, (_, _, _) => throw new InvalidOperationException("Unexpected proxy probe"));

        var result = Assert.Single(results);
        Assert.Equal(1, result.LineNumber);
        Assert.Equal("Строка 1", result.Address);
        Assert.Equal(ProxyCheckStatus.Invalid, result.Status);
        Assert.Null(result.CopyValue);
        Assert.Null(result.ElapsedMilliseconds);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Fact]
    public async Task ResolvesTheCommonProtocolForBareAddressesWithCredentials()
    {
        var results = new ConcurrentBag<ProxyCheckResult>();
        await ProxyChecker.CheckAsync("user:secret@192.0.2.18:443", ProxyProtocol.Https,
            TimeSpan.FromSeconds(5), 1, results.Add, CancellationToken.None, (uri, credentials, _) =>
            {
                Assert.Equal("https", uri.Scheme);
                Assert.Equal("user", credentials?.UserName);
                Assert.Equal("secret", credentials?.Password);
                return Task.FromResult(HttpStatusCode.NoContent);
            });

        var result = Assert.Single(results);
        Assert.Equal("192.0.2.18:443", result.Address);
        Assert.Equal("https://user:secret@192.0.2.18:443", result.CopyValue);
    }

    [Theory]
    [InlineData("192.0.2.18:3000", "https", "192.0.2.18")]
    [InlineData("socks5://[2001:db8::1]:3000", "socks5", "[2001:db8::1]")]
    public async Task AcceptsCredentialsAfterThePort(string endpoint, string scheme, string host)
    {
        const string login = "test-region-Random-sid-example-t-5";
        var results = new ConcurrentBag<ProxyCheckResult>();
        await ProxyChecker.CheckAsync($"{endpoint}:{login}:p%40ss", ProxyProtocol.Https,
            TimeSpan.FromSeconds(5), 1, results.Add, CancellationToken.None, (uri, credentials, _) =>
            {
                Assert.Equal(scheme, uri.Scheme);
                Assert.Equal(host, uri.Host);
                Assert.Equal(3000, uri.Port);
                Assert.Empty(uri.UserInfo);
                Assert.Equal(login, credentials?.UserName);
                Assert.Equal("p@ss", credentials?.Password);
                return Task.FromResult(HttpStatusCode.NoContent);
            });

        var result = Assert.Single(results);
        Assert.Equal(ProxyCheckStatus.Working, result.Status);
        Assert.Equal($"{host}:3000", result.Address);
        Assert.Equal($"{scheme}://{login}:p%40ss@{host}:3000", result.CopyValue);
    }

    [Fact]
    public async Task DistinguishesProxyTimeoutAndUnexpectedHttpStatus()
    {
        var results = new ConcurrentDictionary<int, ProxyCheckResult>();
        await ProxyChecker.CheckAsync("192.0.2.18:80\n192.0.2.19:80", ProxyProtocol.Http,
            TimeSpan.FromMilliseconds(100), 1, result => results[result.LineNumber] = result,
            CancellationToken.None, async (uri, _, token) =>
            {
                if (uri.Host == "192.0.2.18")
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return HttpStatusCode.Found;
            });

        Assert.Equal(ProxyCheckStatus.Timeout, results[1].Status);
        Assert.Equal(ProxyCheckStatus.Failed, results[2].Status);
        Assert.Contains("302", results[2].Detail);
        Assert.All(results.Values, result => Assert.Null(result.CopyValue));
    }

    [Fact]
    public async Task CancelsInFlightProbesWithoutStartingTheRest()
    {
        var results = new ConcurrentBag<ProxyCheckResult>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var check = ProxyChecker.CheckAsync("192.0.2.18:80\n192.0.2.19:80\n192.0.2.20:80", ProxyProtocol.Http,
            TimeSpan.FromSeconds(10), 2, results.Add, cancellation.Token, async (_, _, token) =>
            {
                if (Interlocked.Increment(ref calls) == 2) started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return HttpStatusCode.NoContent;
            });

        await started.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(2, calls);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SupportsMoreThan32SimultaneousChecksAndCancelsThem()
    {
        const int parallelism = 64;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = 0;
        var input = string.Join('\n', Enumerable.Repeat("192.0.2.18:80", parallelism + 1));
        var check = ProxyChecker.CheckAsync(input, ProxyProtocol.Http, TimeSpan.FromSeconds(30), parallelism,
            _ => throw new InvalidOperationException("A cancelled check should not report results"), stop.Token,
            async (_, _, token) =>
            {
                if (Interlocked.Increment(ref started) == parallelism) allStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return HttpStatusCode.NoContent;
            });

        await allStarted.Task.WaitAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(parallelism, started);
    }

    [Fact]
    public async Task LooksUpTheRegionThroughTheWorkingProxyWithoutChangingItsHealth()
    {
        var results = new ConcurrentDictionary<int, ProxyCheckResult>();
        var lookedUp = new ConcurrentBag<string>();
        await ProxyChecker.CheckAsync(
            "user:secret@192.0.2.18:80\n192.0.2.19:80\n192.0.2.20:80",
            ProxyProtocol.Http, TimeSpan.FromSeconds(5), 3, result => results[result.LineNumber] = result,
            CancellationToken.None, (uri, _, _) => Task.FromResult(
                uri.Host == "192.0.2.20" ? HttpStatusCode.BadGateway : HttpStatusCode.NoContent),
            (uri, credentials, _) =>
            {
                lookedUp.Add(uri.Host);
                if (uri.Host == "192.0.2.19") throw new HttpRequestException("Provider unavailable");
                Assert.Equal("secret", credentials?.Password);
                Assert.Empty(uri.UserInfo);
                return Task.FromResult<string?>("US · California");
            });

        Assert.Equal(new[] { "192.0.2.18", "192.0.2.19" }, lookedUp.Order());
        Assert.Equal("US · California", results[1].Region);
        Assert.Equal(ProxyChecker.RegionUnavailable, results[2].Region);
        Assert.Equal(ProxyCheckStatus.Working, results[1].Status);
        Assert.Equal(ProxyCheckStatus.Working, results[2].Status);
        Assert.NotNull(results[2].CopyValue);
        Assert.Equal(ProxyCheckStatus.Failed, results[3].Status);
        Assert.Null(results[3].Region);
    }

    [Fact]
    public async Task RegionTimeoutKeepsTheProxyWorking()
    {
        var results = new ConcurrentBag<ProxyCheckResult>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ProxyChecker.CheckAsync("192.0.2.18:80", ProxyProtocol.Http, TimeSpan.FromMilliseconds(100), 1,
            results.Add, stop.Token, (_, _, _) => Task.FromResult(HttpStatusCode.NoContent),
            async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            });

        var result = Assert.Single(results);
        Assert.Equal(ProxyCheckStatus.Working, result.Status);
        Assert.Equal(ProxyChecker.RegionUnavailable, result.Region);
    }

    [Fact]
    public async Task CanStopWhileLookingUpTheRegion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = ProxyChecker.CheckAsync("192.0.2.18:80", ProxyProtocol.Http, TimeSpan.FromSeconds(30), 1,
            _ => throw new InvalidOperationException("A cancelled check should not report results"), stop.Token,
            (_, _, _) => Task.FromResult(HttpStatusCode.NoContent),
            async (_, _, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            });

        await started.Task.WaitAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Theory]
    [InlineData("""{"success":true,"country_code":"us","region":"California"}""", "US · California")]
    [InlineData("""{"success":true,"country_code":"DE","region":null}""", "DE")]
    [InlineData("""{"success":true,"country_code":"DE","region":"bad\nline"}""", "DE")]
    [InlineData("""{"success":true,"country_code":"??","region":"Unknown"}""", null)]
    [InlineData("""{"success":false,"message":"limit exceeded"}""", null)]
    [InlineData("[]", null)]
    public void ParsesOnlyExpectedRegionFields(string body, string? expected)
    {
        Assert.Equal(expected, ProxyChecker.ParseRegion(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task SendsTheHttpsTunnelRequestToTheSuppliedHttpProxy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var results = new ConcurrentBag<ProxyCheckResult>();

        var check = ProxyChecker.CheckAsync($"{endpoint.Address}:{endpoint.Port}", ProxyProtocol.Http,
            TimeSpan.FromSeconds(5), 1, results.Add, cancellation.Token);

        using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
        using var stream = socket.GetStream();
        using var reader = new StreamReader(stream);
        var requestLine = await reader.ReadLineAsync(cancellation.Token);
        while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
            cancellation.Token);
        await check;

        Assert.Equal($"CONNECT {new Uri(ProxyChecker.ProbeUrl).Host}:443 HTTP/1.1", requestLine);
        var result = Assert.Single(results);
        Assert.Equal(ProxyCheckStatus.Failed, result.Status);
        Assert.Null(result.CopyValue);
    }

    [Fact]
    public async Task SendsTheRegionLookupThroughTheSuppliedHttpProxy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lookup = ProxyChecker.FindRegionAsync(new Uri($"http://{endpoint}"), null, cancellation.Token);

        using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
        using var stream = socket.GetStream();
        using var reader = new StreamReader(stream);
        var requestLine = await reader.ReadLineAsync(cancellation.Token);
        while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
            cancellation.Token);

        Assert.Equal($"CONNECT {new Uri(ProxyChecker.RegionUrl).Host}:443 HTTP/1.1", requestLine);
        await Assert.ThrowsAsync<HttpRequestException>(() => lookup);
    }
}
