using System.Diagnostics;
using System.Globalization;
using System.Net;

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
    string? CopyValue)
{
    // CopyValue may contain credentials. Keep it out of diagnostic formatting.
    public override string ToString() => $"{LineNumber}: {Address} ({Status})";
}

internal static class ProxyChecker
{
    public const string ProbeUrl = "https://connectivitycheck.gstatic.com/generate_204";

    public static Task CheckAsync(
        string input,
        ProxyProtocol defaultProtocol,
        TimeSpan timeout,
        int parallelism,
        Action<ProxyCheckResult> report,
        CancellationToken cancellationToken) =>
        CheckAsync(input, defaultProtocol, timeout, parallelism, report, cancellationToken, ProbeAsync);

    internal static Task CheckAsync(
        string input,
        ProxyProtocol defaultProtocol,
        TimeSpan timeout,
        int parallelism,
        Action<ProxyCheckResult> report,
        CancellationToken cancellationToken,
        Func<Uri, NetworkCredential?, CancellationToken, Task<HttpStatusCode>> probe)
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
                : await CheckProxyAsync(proxy, line.Number, timeout, token, probe).ConfigureAwait(false);
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
        Func<Uri, NetworkCredential?, CancellationToken, Task<HttpStatusCode>> probe)
    {
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(timeout);
        var timer = Stopwatch.StartNew();

        try
        {
            var status = await probe(proxy.Uri, proxy.Credentials, probeCancellation.Token).ConfigureAwait(false);
            return status == HttpStatusCode.NoContent
                ? new ProxyCheckResult(lineNumber, proxy.Address, proxy.Protocol, ProxyCheckStatus.Working,
                    timer.ElapsedMilliseconds, string.Empty, proxy.CopyValue)
                : Failure(ProxyCheckStatus.Failed, $"Ответ проверки: HTTP {(int)status}");
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

        ProxyCheckResult Failure(ProxyCheckStatus status, string detail) =>
            new(lineNumber, proxy.Address, proxy.Protocol, status, null, detail, null);
    }

    private static async Task<HttpStatusCode> ProbeAsync(Uri proxy, NetworkCredential? credentials, CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy) { Credentials = credentials },
            UseProxy = true,
            UseCookies = false,
            AllowAutoRedirect = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        return response.StatusCode;
    }

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
