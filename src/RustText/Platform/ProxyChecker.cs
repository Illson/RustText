using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace RustText.Demo.Platform;

internal enum ProxyProtocol
{
    Http,
    Https,
    Socks5
}

internal enum ProxyCheckStatus
{
    Working,
    Timeout,
    Failed,
    Invalid
}

internal sealed record ProxyCheckResult(
    int LineNumber,
    string Address,
    ProxyProtocol? Protocol,
    ProxyCheckStatus Status,
    long? ElapsedMilliseconds,
    string Detail,
    string? CopyValue,
    string? Region = null)
{
    // CopyValue may contain credentials. Keep it out of diagnostic formatting.
    public override string ToString() => $"{LineNumber}: {Address} ({Status})";
}

internal static class ProxyChecker
{
    public const string ProbeUrl = "https://connectivitycheck.gstatic.com/generate_204";
    internal const string RegionUrl = "https://ipwho.is/?fields=success,country_code,region";
    internal const string RegionUnavailable = "Регион не определён";

    public static Task CheckAsync(
        string input,
        ProxyProtocol defaultProtocol,
        TimeSpan timeout,
        int parallelism,
        Action<ProxyCheckResult> report,
        CancellationToken cancellationToken,
        bool lookupRegion = false) =>
        CheckAsync(input, defaultProtocol, timeout, parallelism, report, cancellationToken,
            ProbeAsync, lookupRegion ? FindRegionAsync : null);

    internal static Task CheckAsync(
        string input,
        ProxyProtocol defaultProtocol,
        TimeSpan timeout,
        int parallelism,
        Action<ProxyCheckResult> report,
        CancellationToken cancellationToken,
        Func<Uri, NetworkCredential?, CancellationToken, Task<HttpStatusCode>> probe,
        Func<Uri, NetworkCredential?, CancellationToken, Task<string?>>? findRegion = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parallelism);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        };
        return Parallel.ForEachAsync(ReadLines(input, cancellationToken), options, async (line, token) =>
        {
            var proxy = Parse(line.Text, defaultProtocol, out var detail);
            var result = proxy is null
                ? new ProxyCheckResult(line.Number, $"Строка {line.Number}", null, ProxyCheckStatus.Invalid, null, detail, null)
                : await CheckProxyAsync(proxy, line.Number, timeout, token, probe, findRegion).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            report(result);
        });
    }

    private static IEnumerable<(int Number, string Text)> ReadLines(string input, CancellationToken token)
    {
        using var reader = new StringReader(input);
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            number++;
            if (!string.IsNullOrWhiteSpace(line)) yield return (number, line.Trim());
        }
    }

    private static ParsedProxy? Parse(string text, ProxyProtocol defaultProtocol, out string detail)
    {
        var schemeSeparator = text.IndexOf("://", StringComparison.Ordinal);
        var protocol = schemeSeparator < 0 ? defaultProtocol : ParseProtocol(text[..schemeSeparator]);
        if (protocol is null)
        {
            detail = "Поддерживаются HTTP, HTTPS и SOCKS5";
            return null;
        }

        detail = "Ожидается адрес и порт от 1 до 65535";
        var authority = schemeSeparator < 0 ? text : text[(schemeSeparator + 3)..];
        if (authority.AsSpan().IndexOfAny("/\\?#") >= 0 || authority.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            return null;

        var (endpoint, userInfo) = SplitAuthority(authority);
        var portSeparator = endpoint.LastIndexOf(':');
        var scheme = ProtocolScheme(protocol.Value);
        if (portSeparator <= 0
            || !int.TryParse(endpoint.AsSpan(portSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535
            || !Uri.TryCreate($"{scheme}://{endpoint}", UriKind.Absolute, out var proxyUri)
            || proxyUri.Host.Length == 0
            || proxyUri.UserInfo.Length != 0)
            return null;

        NetworkCredential? credentials = null;
        if (userInfo is not null)
        {
            detail = "Ожидаются данные авторизации в формате логин:пароль";
            var passwordSeparator = userInfo.IndexOf(':');
            if (passwordSeparator <= 0) return null;

            var username = Uri.UnescapeDataString(userInfo[..passwordSeparator]);
            var password = Uri.UnescapeDataString(userInfo[(passwordSeparator + 1)..]);
            if (username.Any(char.IsControl) || password.Any(char.IsControl)) return null;
            credentials = new NetworkCredential(username, password);
        }

        var copyValue = userInfo is null ? $"{scheme}://{endpoint}" : $"{scheme}://{userInfo}@{endpoint}";
        return new ParsedProxy(proxyUri, credentials, protocol.Value, $"{proxyUri.Host}:{port}", copyValue);
    }

    private static (string Endpoint, string? UserInfo) SplitAuthority(string authority)
    {
        var at = authority.IndexOf('@');
        if (at >= 0) return (authority[(at + 1)..], authority[..at]);

        // In host:port:login:password, skip the colons inside a bracketed IPv6 host.
        var afterHostLiteral = authority.StartsWith('[') ? authority.IndexOf(']') + 1 : 0;
        var portSeparator = authority.IndexOf(':', afterHostLiteral);
        var credentialsSeparator = portSeparator < 0 ? -1 : authority.IndexOf(':', portSeparator + 1);
        return credentialsSeparator < 0
            ? (authority, null)
            : (authority[..credentialsSeparator], authority[(credentialsSeparator + 1)..]);
    }

    private static ProxyProtocol? ParseProtocol(string scheme) => scheme.ToLowerInvariant() switch
    {
        "http" => ProxyProtocol.Http,
        "https" => ProxyProtocol.Https,
        "socks5" => ProxyProtocol.Socks5,
        _ => null
    };

    private static string ProtocolScheme(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Http => "http",
        ProxyProtocol.Https => "https",
        ProxyProtocol.Socks5 => "socks5",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private static async Task<ProxyCheckResult> CheckProxyAsync(
        ParsedProxy proxy,
        int lineNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<Uri, NetworkCredential?, CancellationToken, Task<HttpStatusCode>> probe,
        Func<Uri, NetworkCredential?, CancellationToken, Task<string?>>? findRegion)
    {
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(timeout);
        var timer = Stopwatch.StartNew();
        long elapsed;

        try
        {
            var status = await probe(proxy.Uri, proxy.Credentials, probeCancellation.Token).ConfigureAwait(false);
            if (status != HttpStatusCode.NoContent)
                return Failure(ProxyCheckStatus.Failed, $"Ответ проверки: HTTP {(int)status}");
            elapsed = timer.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && probeCancellation.IsCancellationRequested)
        {
            return Failure(ProxyCheckStatus.Timeout, "Время ожидания истекло");
        }
        catch (HttpRequestException exception)
        {
            var detail = exception.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Адрес не найден",
                HttpRequestError.SecureConnectionError => "Ошибка защищённого соединения",
                HttpRequestError.ProxyTunnelError => "Прокси отклонил туннель",
                _ => "Ошибка подключения"
            };
            return Failure(ProxyCheckStatus.Failed, detail);
        }

        var region = findRegion is null ? null :
            await CheckRegionAsync(proxy, timeout, cancellationToken, findRegion).ConfigureAwait(false);
        return new ProxyCheckResult(lineNumber, proxy.Address, proxy.Protocol, ProxyCheckStatus.Working,
            elapsed, string.Empty, proxy.CopyValue, region);

        ProxyCheckResult Failure(ProxyCheckStatus status, string detail) =>
            new(lineNumber, proxy.Address, proxy.Protocol, status, null, detail, null);
    }

    private static async Task<string> CheckRegionAsync(ParsedProxy proxy, TimeSpan timeout, CancellationToken cancellationToken,
        Func<Uri, NetworkCredential?, CancellationToken, Task<string?>> findRegion)
    {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookup.CancelAfter(timeout);
        try
        {
            return await findRegion(proxy.Uri, proxy.Credentials, lookup.Token).ConfigureAwait(false) ?? RegionUnavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lookup.IsCancellationRequested)
        {
            return RegionUnavailable;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        {
            return RegionUnavailable;
        }
    }

    private static async Task<HttpStatusCode> ProbeAsync(Uri proxy, NetworkCredential? credentials, CancellationToken token)
    {
        using var client = CreateProxyClient(proxy, credentials);
        using var response = await client.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        return response.StatusCode;
    }

    internal static async Task<string?> FindRegionAsync(Uri proxy, NetworkCredential? credentials, CancellationToken token)
    {
        using var client = CreateProxyClient(proxy, credentials);
        using var response = await client.GetAsync(RegionUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        // Do not let an unexpected response allocate an unbounded amount of memory.
        var buffer = new byte[4097];
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var size = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: token)
            .ConfigureAwait(false);
        return size == buffer.Length ? null : ParseRegion(buffer.AsMemory(0, size));
    }

    internal static string? ParseRegion(ReadOnlyMemory<byte> body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True
            || ReadString("country_code") is not { Length: 2 } country || !country.All(char.IsAsciiLetter))
            return null;

        var region = ReadString("region");
        return region is { Length: > 0 and <= 80 } && !region.Any(char.IsControl)
            ? $"{country.ToUpperInvariant()} · {region}"
            : country.ToUpperInvariant();

        string? ReadString(string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() : null;
    }

    private static HttpClient CreateProxyClient(Uri proxy, NetworkCredential? credentials) =>
        new(new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy) { Credentials = credentials },
            UseProxy = true,
            UseCookies = false,
            AllowAutoRedirect = false
        }) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class ParsedProxy(
        Uri uri, NetworkCredential? credentials, ProxyProtocol protocol, string address, string copyValue)
    {
        public Uri Uri { get; } = uri;
        public NetworkCredential? Credentials { get; } = credentials;
        public ProxyProtocol Protocol { get; } = protocol;
        public string Address { get; } = address;
        public string CopyValue { get; } = copyValue;
    }
}
