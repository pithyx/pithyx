using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Network;
using Pithyx.Daemon.Tls;
using Yarp.ReverseProxy.Forwarder;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>A proxy host ready to forward: its settings, the client to its target and what it presents on 443.</summary>
public sealed record HostRuntime(
    ProxyHostConfig Config,
    HttpMessageInvoker Client,
    ForwarderRequestConfig Requests,
    ForwarderRequestConfig Sockets,
    IReadOnlyList<IPNetwork> Allow,
    IReadOnlyList<IPNetwork> Deny,
    string LogSource);

/// <summary>The proxy's current configuration; replaced as a whole on every change.</summary>
public sealed record ProxyState(
    ProxyTable Table,
    UnknownHostAction UnknownHosts,
    string? SignInAddress,
    IReadOnlyDictionary<long, HostRuntime> Hosts,
    IReadOnlyDictionary<string, PresentedCertificate> Certificates)
{
    public static ProxyState Empty { get; } = new(ProxyTable.Empty, UnknownHostAction.Redirect, null, new Dictionary<long, HostRuntime>(), new Dictionary<string, PresentedCertificate>());

    /// <summary>The certificate a route presents, if the box has one for it.</summary>
    public PresentedCertificate? CertificateOf(ProxyRoute route) => Certificates.GetValueOrDefault(KeyOf(route));

    /// <summary><c>host:&lt;id&gt;</c> or <c>redirect:&lt;id&gt;</c>.</summary>
    public static string KeyOf(ProxyRoute route) => route switch
    {
        HostRoute { Host.Id: var id } => string.Create(CultureInfo.InvariantCulture, $"host:{id}"),
        RedirectRoute { Redirect.Id: var id } => string.Create(CultureInfo.InvariantCulture, $"redirect:{id}"),
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };
}

/// <summary>A certificate a host presents on the TLS ports, with the TLS settings for its handshakes.</summary>
public sealed record PresentedCertificate(X509Certificate2 Certificate, SslServerAuthenticationOptions Options, string Key);

/// <summary>
/// The reverse proxy's configuration (D347): proxy hosts, redirect hosts and streams in <c>pithyx.db</c>, loaded at
/// start and swapped in as a whole after every change, so a change applies to the next request without a restart.
/// Hosts present a certificate of their own on the TLS ports, chosen by the server name of the handshake: one the
/// box's authority makes in memory for the host's domains (again when the authority changes or the certificate is
/// due), or one an administrator uploaded, kept as <c>tls/proxy-host-&lt;id&gt;.pem</c> and <c>.key</c> next to the box's
/// own, so the backup part Certificates takes it along. Administrators only.
/// </summary>
public sealed partial class ProxyService(
    SqliteConnectionFactory connections,
    BoxCertificates certificates,
    NetworkHost host,
    WebTransport transport,
    TlsOptions tlsOptions,
    AcmeRuntime acme,
    PasswordHasher hasher,
    TimeProvider time,
    ILogger<ProxyService> logger) : BackgroundService
{
    public const string UnknownHostsSetting = "proxy.unknownHosts";
    public const string SignInAddressSetting = "proxy.signInAddress";

    /// <summary>The header the proxy adds to every forwarded request, to notice a target that is the proxy itself.</summary>
    public const string LoopHeader = "X-Pithyx-Proxy";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, HttpMessageInvoker> _clients = new(StringComparer.Ordinal);
    private Dictionary<string, PresentedCertificate> _issued = new(StringComparer.Ordinal);
    private volatile ProxyState _state = ProxyState.Empty;

    /// <summary>The configuration requests use now.</summary>
    public ProxyState State => _state;

    /// <summary>Changes with every start of the daemon; a request that carries it came from this proxy.</summary>
    public string LoopToken { get; } = Secrets.NewToken(16);

    /// <summary>Loads the configuration; runs at start before the first request and after the authority changed.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await LoadLockedAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The TLS settings for a handshake with <paramref name="serverName"/>: the certificate of the host that has the
    /// name, or the box's own.
    /// </summary>
    public SslServerAuthenticationOptions ServerOptions(string? serverName)
    {
        var state = _state;
        return state.Table.Resolve(serverName) is { } route && state.CertificateOf(route) is { } presented
            ? presented.Options
            : certificates.ServerOptions;
    }

    /// <summary>Settings > Reverse proxy; <paramref name="listening"/> tells whether a stream listens now.</summary>
    public async Task<ProxySettings> SettingsAsync(Func<long, bool> listening, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listening);
        var (hosts, redirects, streams, unknown, signIn) = await ReadAsync(cancellationToken);
        var acmeRows = await AcmeRowsAsync(cancellationToken);
        var state = _state;
        return new ProxySettings(
            transport.ProxyPlainPort,
            transport.ProxyTlsPort,
            unknown,
            signIn,
            [.. hosts.Select(config => ToHost(config, state.Certificates.GetValueOrDefault(ProxyState.KeyOf(new HostRoute(config))), AcmeStatus(config.Certificate, FileName(isHost: true, config.Id), config.Domains, acmeRows)))],
            [.. redirects.Select(config => ToRedirect(config, state.Certificates.GetValueOrDefault(ProxyState.KeyOf(new RedirectRoute(config))), AcmeStatus(config.Certificate, FileName(isHost: false, config.Id), config.Domains, acmeRows)))],
            [.. streams.Select(config => ToStream(config, listening(config.Id)))]);
    }

    public async Task SetOptionsAsync(ProxyOptionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var address = ProxyValidator.SignInAddress(request.SignInAddress);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                    new { key = UnknownHostsSetting, value = request.UnknownHosts == UnknownHostAction.NotFound ? "notFound" : "redirect" });
                if (address is null)
                {
                    await connection.ExecuteAsync("DELETE FROM settings WHERE key = @key", new { key = SignInAddressSetting });
                }
                else
                {
                    await connection.ExecuteAsync(
                        "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                        new { key = SignInAddressSetting, value = address });
                }
            }
            await LoadLockedAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates a proxy host, or replaces the one with <paramref name="id"/>.</summary>
    public async Task<long> SaveHostAsync(long? id, ProxyHostRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var domains = ProxyValidator.Domains(request.Domains);
        var target = ProxyValidator.Target(request.Target, Identity());
        var requestHeaders = ProxyValidator.Headers(request.RequestHeaders);
        var responseHeaders = ProxyValidator.Headers(request.ResponseHeaders);
        var hostHeader = ProxyValidator.HostHeader(request.HostHeader);
        var allow = ProxyValidator.Addresses(request.AllowAddresses);
        var deny = ProxyValidator.Addresses(request.DenyAddresses);
        if (request.MaxBodyMegabytes is { } maxBody)
        {
            ProxyValidator.Limit("maxBodyMegabytes", maxBody, 1, ProxyValidator.MaxBodyMegabytes);
        }
        ProxyValidator.Limit("connectTimeoutSeconds", request.ConnectTimeoutSeconds, 1, ProxyValidator.MaxConnectTimeoutSeconds);
        ProxyValidator.Limit("responseTimeoutSeconds", request.ResponseTimeoutSeconds, 1, ProxyValidator.MaxResponseTimeoutSeconds);
        var signIn = request.SignIn ?? new ProxySignInRule(false, [], []);
        var users = (signIn.Users ?? []).Select(name => name.Trim().ToLowerInvariant()).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var groups = (signIn.Groups ?? []).Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var (hosts, redirects, _, _, _) = await ReadAsync(cancellationToken);
            var existing = id is { } current ? hosts.FirstOrDefault(entry => entry.Id == current) ?? throw HostNotFound(current) : null;
            EnsureFree(domains, hosts.Where(entry => entry.Id != id).SelectMany(entry => entry.Domains).Concat(redirects.SelectMany(entry => entry.Domains)));
            var stored = existing?.BasicAuth.ToDictionary(entry => entry.Username, entry => entry.Hash, StringComparer.Ordinal) ?? [];
            var basicUsers = ProxyValidator.BasicAuth(request.BasicAuth, stored.Keys.ToHashSet(StringComparer.Ordinal));
            var basicAuth = new List<BasicAuthEntry>();
            foreach (var user in basicUsers)
            {
                basicAuth.Add(new BasicAuthEntry(user.Username, user.Password is { } password ? await hasher.HashAsync(password, cancellationToken) : stored[user.Username]));
            }
            await EnsureAccountsAsync(users, groups, cancellationToken);
            await CheckAcmeAsync(request.Certificate, domains, cancellationToken);
            var pending = CheckCertificate(request.Certificate, domains, request.CertificatePem, request.PrivateKeyPem, id is { } known ? FileName(isHost: true, known) : null);

            var now = Format(time.GetUtcNow());
            var parameters = new
            {
                id,
                domains = JsonSerializer.Serialize(domains, _json),
                enabled = request.Enabled,
                targetScheme = target.Scheme,
                targetHost = target.Host,
                targetPort = target.Port,
                targetTls = Name(target.Tls),
                targetOnBox = target.OnBox,
                websockets = request.WebSockets,
                http2 = request.Http2,
                certificate = SourceName(request.Certificate),
                forceHttps = request.ForceHttps,
                hsts = request.Hsts,
                forwardedHeaders = request.ForwardedHeaders,
                preserveHost = request.PreserveHost,
                hostHeader,
                requestHeaders = JsonSerializer.Serialize(requestHeaders, _json),
                responseHeaders = JsonSerializer.Serialize(responseHeaders, _json),
                maxBody = request.MaxBodyMegabytes,
                connectTimeout = request.ConnectTimeoutSeconds,
                responseTimeout = request.ResponseTimeoutSeconds,
                allow = JsonSerializer.Serialize(allow, _json),
                deny = JsonSerializer.Serialize(deny, _json),
                basicAuth = JsonSerializer.Serialize(basicAuth, _json),
                signIn = signIn.Required,
                signInUsers = JsonSerializer.Serialize(users, _json),
                signInGroups = JsonSerializer.Serialize(groups, _json),
                accessLog = request.AccessLog,
                now,
            };
            long saved;
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                saved = id is null
                    ? await connection.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO proxy_hosts (domains, enabled, target_scheme, target_host, target_port, target_tls, target_on_box,
                            websockets, http2, certificate, force_https, hsts, forwarded_headers, preserve_host, host_header,
                            request_headers, response_headers, max_body_megabytes, connect_timeout_seconds, response_timeout_seconds,
                            allow_addresses, deny_addresses, basic_auth, sign_in, sign_in_users, sign_in_groups, access_log, created_at, updated_at)
                        VALUES (@domains, @enabled, @targetScheme, @targetHost, @targetPort, @targetTls, @targetOnBox,
                            @websockets, @http2, @certificate, @forceHttps, @hsts, @forwardedHeaders, @preserveHost, @hostHeader,
                            @requestHeaders, @responseHeaders, @maxBody, @connectTimeout, @responseTimeout,
                            @allow, @deny, @basicAuth, @signIn, @signInUsers, @signInGroups, @accessLog, @now, @now)
                        RETURNING id
                        """,
                        parameters)
                    : await connection.ExecuteAsync(
                        """
                        UPDATE proxy_hosts SET domains = @domains, enabled = @enabled, target_scheme = @targetScheme,
                            target_host = @targetHost, target_port = @targetPort, target_tls = @targetTls, target_on_box = @targetOnBox,
                            websockets = @websockets, http2 = @http2, certificate = @certificate, force_https = @forceHttps,
                            hsts = @hsts, forwarded_headers = @forwardedHeaders, preserve_host = @preserveHost, host_header = @hostHeader,
                            request_headers = @requestHeaders, response_headers = @responseHeaders, max_body_megabytes = @maxBody,
                            connect_timeout_seconds = @connectTimeout, response_timeout_seconds = @responseTimeout,
                            allow_addresses = @allow, deny_addresses = @deny, basic_auth = @basicAuth, sign_in = @signIn,
                            sign_in_users = @signInUsers, sign_in_groups = @signInGroups, access_log = @accessLog, updated_at = @now
                        WHERE id = @id
                        """,
                        parameters) == 1 ? id.Value : throw HostNotFound(id.Value);
            }
            WriteCertificate(pending, FileName(isHost: true, saved), request.Certificate);
            await ForgetAcmeAsync(request.Certificate, FileName(isHost: true, saved), cancellationToken);
            await LoadLockedAsync(cancellationToken);
            LogHostSaved(saved, domains, target.Host, target.Port);
            return saved;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteHostAsync(long id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                if (await connection.ExecuteAsync("DELETE FROM proxy_hosts WHERE id = @id", new { id }) == 0)
                {
                    throw HostNotFound(id);
                }
            }
            DeleteCertificate(FileName(isHost: true, id));
            await ForgetAcmeAsync(ProxyCertificateSource.Box, FileName(isHost: true, id), cancellationToken);
            await LoadLockedAsync(cancellationToken);
            LogHostDeleted(id);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates a redirect host, or replaces the one with <paramref name="id"/>.</summary>
    public async Task<long> SaveRedirectAsync(long? id, ProxyRedirectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var domains = ProxyValidator.Domains(request.Domains);
        var target = ProxyValidator.RedirectTarget(request.Target);
        var status = ProxyValidator.RedirectStatus(request.Status);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var (hosts, redirects, _, _, _) = await ReadAsync(cancellationToken);
            if (id is { } current && redirects.All(entry => entry.Id != current))
            {
                throw RedirectNotFound(current);
            }
            EnsureFree(domains, redirects.Where(entry => entry.Id != id).SelectMany(entry => entry.Domains).Concat(hosts.SelectMany(entry => entry.Domains)));
            await CheckAcmeAsync(request.Certificate, domains, cancellationToken);
            var pending = CheckCertificate(request.Certificate, domains, request.CertificatePem, request.PrivateKeyPem, id is { } known ? FileName(isHost: false, known) : null);
            var parameters = new
            {
                id,
                domains = JsonSerializer.Serialize(domains, _json),
                enabled = request.Enabled,
                target,
                status,
                preservePath = request.PreservePath,
                certificate = SourceName(request.Certificate),
                now = Format(time.GetUtcNow()),
            };
            long saved;
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                saved = id is null
                    ? await connection.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO proxy_redirects (domains, enabled, target, status, preserve_path, certificate, created_at, updated_at)
                        VALUES (@domains, @enabled, @target, @status, @preservePath, @certificate, @now, @now)
                        RETURNING id
                        """,
                        parameters)
                    : await connection.ExecuteAsync(
                        """
                        UPDATE proxy_redirects SET domains = @domains, enabled = @enabled, target = @target, status = @status,
                            preserve_path = @preservePath, certificate = @certificate, updated_at = @now
                        WHERE id = @id
                        """,
                        parameters) == 1 ? id.Value : throw RedirectNotFound(id.Value);
            }
            WriteCertificate(pending, FileName(isHost: false, saved), request.Certificate);
            await ForgetAcmeAsync(request.Certificate, FileName(isHost: false, saved), cancellationToken);
            await LoadLockedAsync(cancellationToken);
            LogRedirectSaved(saved, domains, target);
            return saved;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteRedirectAsync(long id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                if (await connection.ExecuteAsync("DELETE FROM proxy_redirects WHERE id = @id", new { id }) == 0)
                {
                    throw RedirectNotFound(id);
                }
            }
            DeleteCertificate(FileName(isHost: false, id));
            await ForgetAcmeAsync(ProxyCertificateSource.Box, FileName(isHost: false, id), cancellationToken);
            await LoadLockedAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Creates a stream, or replaces the one with <paramref name="id"/>. <paramref name="portFree"/> tells whether
    /// another program holds the port; a port the stream itself already listens on counts as free.
    /// </summary>
    public async Task<long> SaveStreamAsync(long? id, ProxyStreamRequest request, Func<int, bool, bool, long?, bool> portFree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(portFree);
        var stream = ProxyValidator.Stream(request, Identity());
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var (_, _, streams, _, _) = await ReadAsync(cancellationToken);
            if (id is { } current && streams.All(entry => entry.Id != current))
            {
                throw StreamNotFound(current);
            }
            if (streams.Any(entry => entry.Id != id && entry.Port == stream.Port && ((entry.Tcp && stream.Tcp) || (entry.Udp && stream.Udp))))
            {
                throw new PithyxException(ErrorCodes.ProxyStreamPortTaken, new Dictionary<string, object?> { ["port"] = stream.Port });
            }
            if (stream.Enabled && !portFree(stream.Port, stream.Tcp, stream.Udp, id))
            {
                throw new PithyxException(ErrorCodes.ProxyStreamPortInUse, new Dictionary<string, object?> { ["port"] = stream.Port });
            }
            var parameters = new
            {
                id,
                name = stream.Name,
                enabled = stream.Enabled,
                port = stream.Port,
                tcp = stream.Tcp,
                udp = stream.Udp,
                targetHost = stream.TargetHost,
                targetPort = stream.TargetPort,
                now = Format(time.GetUtcNow()),
            };
            long saved;
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                saved = id is null
                    ? await connection.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO proxy_streams (name, enabled, port, tcp, udp, target_host, target_port, created_at, updated_at)
                        VALUES (@name, @enabled, @port, @tcp, @udp, @targetHost, @targetPort, @now, @now)
                        RETURNING id
                        """,
                        parameters)
                    : await connection.ExecuteAsync(
                        """
                        UPDATE proxy_streams SET name = @name, enabled = @enabled, port = @port, tcp = @tcp, udp = @udp,
                            target_host = @targetHost, target_port = @targetPort, updated_at = @now
                        WHERE id = @id
                        """,
                        parameters) == 1 ? id.Value : throw StreamNotFound(id.Value);
            }
            LogStreamSaved(saved, stream.Port, stream.TargetHost, stream.TargetPort);
            return saved;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteStreamAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await connection.ExecuteAsync("DELETE FROM proxy_streams WHERE id = @id", new { id }) == 0)
        {
            throw StreamNotFound(id);
        }
    }

    /// <summary>The enabled and disabled streams as stored.</summary>
    public async Task<IReadOnlyList<ProxyStreamConfig>> StreamsAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Streams;

    /// <summary>Whether a proxy host's target answers: one GET of <c>/</c> with the host's settings, at most ten seconds.</summary>
    public async Task<ProxyTargetStatus> CheckAsync(long id, CancellationToken cancellationToken)
    {
        var (hosts, _, _, _, _) = await ReadAsync(cancellationToken);
        var config = hosts.FirstOrDefault(entry => entry.Id == id) ?? throw HostNotFound(id);
        var client = Client(config);
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(10, config.ConnectTimeoutSeconds + 5)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TargetUri(config.Target));
            if (TargetHostHeader(config, config.Domains.FirstOrDefault(domain => !domain.StartsWith("*.", StringComparison.Ordinal)) ?? config.Domains[0].TrimStart('*', '.')) is { } hostHeader)
            {
                request.Headers.Host = hostHeader;
            }
            request.Headers.TryAddWithoutValidation(LoopHeader, LoopToken);
            using var response = await client.SendAsync(request, timeout.Token);
            return new ProxyTargetStatus(true, (int)response.StatusCode, null, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyTargetStatus(false, null, "timeout", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (HttpRequestException exception)
        {
            return new ProxyTargetStatus(false, null, Failure(exception), (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>The kind of failure for the target status: <c>refused</c>, <c>dns</c>, <c>tls</c>, <c>timeout</c> or <c>other</c>.</summary>
    public static string Failure(HttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case AuthenticationException:
                    return "tls";
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return "refused";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return "dns";
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                case TimeoutException:
                    return "timeout";
            }
        }
        return exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "dns",
            HttpRequestError.SecureConnectionError => "tls",
            HttpRequestError.ConnectionError => "refused",
            _ => "other",
        };
    }

    /// <summary>The target's base address, an IPv6 address in brackets.</summary>
    public static Uri TargetUri(ProxyTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var host = target.Host.Contains(':', StringComparison.Ordinal) ? $"[{target.Host}]" : target.Host;
        return new Uri(string.Create(CultureInfo.InvariantCulture, $"{target.Scheme}://{host}:{target.Port}/"));
    }

    /// <summary>The <c>Host</c> the target gets: the administrator's, the browser's (<paramref name="requestHost"/>) or none for the target's own.</summary>
    public static string? TargetHostHeader(ProxyHostConfig config, string requestHost)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.HostHeader ?? (config.PreserveHost ? requestHost : null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The authority changed (a new one, or a renewed intermediate): the hosts' certificates follow.
        void AuthorityChanged() => _ = Task.Run(() => ReloadQuietlyAsync(stoppingToken), stoppingToken);
        certificates.Changed += AuthorityChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // The hosts' own certificates last 397 days; a daily look renews them 30 days before their end.
                await Task.Delay(TimeSpan.FromDays(1), time, stoppingToken);
                await ReloadQuietlyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            certificates.Changed -= AuthorityChanged;
        }
    }

    public override void Dispose()
    {
        _lock.Dispose();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        base.Dispose();
    }

    private async Task ReloadQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReloadFailed(exception);
        }
    }

    private async Task LoadLockedAsync(CancellationToken cancellationToken)
    {
        var (hosts, redirects, _, unknown, signIn) = await ReadAsync(cancellationToken);
        var table = new ProxyTable(hosts, redirects);
        var runtimes = hosts.Where(config => config.Enabled).ToDictionary(config => config.Id, config => new HostRuntime(
            config,
            Client(config),
            Requests(config, sockets: false),
            Requests(config, sockets: true),
            [.. config.AllowAddresses.Select(ProxyValidator.Network)],
            [.. config.DenyAddresses.Select(ProxyValidator.Network)],
            ProxyValidator.LogSource(config.Domains)));
        var issued = new Dictionary<string, PresentedCertificate>(StringComparer.Ordinal);
        var presented = new Dictionary<string, PresentedCertificate>(StringComparer.Ordinal);
        var routes = hosts.Where(config => config.Enabled).Select(config => (ProxyRoute)new HostRoute(config))
            .Concat(redirects.Where(config => config.Enabled).Select(config => new RedirectRoute(config)));
        foreach (var route in routes)
        {
            var (isHost, id, source, http2) = route switch
            {
                HostRoute { Host: var config } => (true, config.Id, config.Certificate, config.Http2),
                RedirectRoute { Redirect: var config } => (false, config.Id, config.Certificate, true),
                _ => throw new UnreachableException(),
            };
            if (Certificate(route.Domains, isHost, id, source, http2, issued) is { } certificate)
            {
                presented[ProxyState.KeyOf(route)] = certificate;
            }
        }
        _issued = issued;
        _state = new ProxyState(table, unknown, signIn, runtimes, presented);
    }

    // The certificate a route presents: an uploaded one, or one from the box's authority made once and kept in
    // memory while its domains, its HTTP/2 setting and the intermediate stay and it is not due.
    private PresentedCertificate? Certificate(IReadOnlyList<string> domains, bool isHost, long id, ProxyCertificateSource source, bool http2, Dictionary<string, PresentedCertificate> issued)
    {
        if (source is ProxyCertificateSource.Custom or ProxyCertificateSource.Acme)
        {
            var file = source == ProxyCertificateSource.Acme ? AcmeFile(FileName(isHost, id)) : FileName(isHost, id);
            try
            {
                var chain = BoxCertificates.ParseCustom(File.ReadAllText(PathOf(file + ".pem")), File.ReadAllText(PathOf(file + ".key")), time.GetUtcNow());
                return Presented(chain[0], new X509Certificate2Collection(chain.Skip(1).ToArray()), http2, file);
            }
            catch (Exception exception) when (exception is PithyxException or IOException)
            {
                // An ACME host has no certificate until the authority issued one; meanwhile it presents the box's own.
                if (source == ProxyCertificateSource.Custom || File.Exists(PathOf(file + ".pem")))
                {
                    LogCertificateUnusable(file);
                }
                return null;
            }
        }
        var key = $"{string.Join(',', domains)}|{http2}|{CertificateAuthority.Fingerprint(certificates.Root)}";
        if (_issued.TryGetValue(key, out var kept) && kept.Certificate.NotAfter.ToUniversalTime() - CertificateAuthority.ServerRenewal > time.GetUtcNow().UtcDateTime)
        {
            issued[key] = kept;
            return kept;
        }
        if (certificates.IssueServer(domains) is not var (certificate, chainToRoot))
        {
            return null;
        }
        var made = Presented(certificate, chainToRoot, http2, key);
        issued[key] = made;
        return made;
    }

    private static PresentedCertificate Presented(X509Certificate2 certificate, X509Certificate2Collection chain, bool http2, string key) =>
        new(certificate, new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(certificate, chain, offline: true),
            ApplicationProtocols = http2 ? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11] : [SslApplicationProtocol.Http11],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
        }, key);

    // An uploaded certificate is checked before anything is stored; it must cover every domain of the host.
    private (string Certificate, string Key)? CheckCertificate(ProxyCertificateSource source, IReadOnlyList<string> domains, string? certificatePem, string? privateKeyPem, string? existingFile)
    {
        if (source == ProxyCertificateSource.Acme)
        {
            return null;
        }
        if (source == ProxyCertificateSource.Box)
        {
            if (domains.FirstOrDefault(domain => !certificates.Permits(domain)) is { } outside)
            {
                throw new PithyxException(ErrorCodes.TlsNameNotPermitted, new Dictionary<string, object?> { ["name"] = outside });
            }
            return null;
        }
        if (string.IsNullOrWhiteSpace(certificatePem) && string.IsNullOrWhiteSpace(privateKeyPem))
        {
            if (existingFile is not null && File.Exists(PathOf(existingFile + ".pem")))
            {
                certificatePem = File.ReadAllText(PathOf(existingFile + ".pem"));
                privateKeyPem = File.ReadAllText(PathOf(existingFile + ".key"));
                Covers(certificatePem, privateKeyPem, domains);
                return null;
            }
            throw new PithyxException(ErrorCodes.ProxyCertificateMissing);
        }
        Covers(certificatePem, privateKeyPem, domains);
        return (certificatePem!.Trim() + "\n", privateKeyPem!.Trim() + "\n");
    }

    private void Covers(string? certificatePem, string? privateKeyPem, IReadOnlyList<string> domains)
    {
        var chain = BoxCertificates.ParseCustom(certificatePem, privateKeyPem, time.GetUtcNow());
        try
        {
            if (domains.FirstOrDefault(domain => !CoversName(chain[0], domain)) is { } missing)
            {
                throw new PithyxException(ErrorCodes.ProxyCertificateNameMismatch, new Dictionary<string, object?> { ["name"] = missing });
            }
        }
        finally
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>Whether a certificate covers a host's domain; a wildcard domain needs the same wildcard.</summary>
    public static bool CoversName(X509Certificate2 certificate, string domain)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(domain);
        var names = CertificateAuthority.Names(certificate).Select(name => name.ToLowerInvariant()).ToList();
        if (names.Contains(domain, StringComparer.Ordinal))
        {
            return true;
        }
        var dot = domain.IndexOf('.', StringComparison.Ordinal);
        return !domain.StartsWith("*.", StringComparison.Ordinal) && dot > 0 && names.Contains("*" + domain[dot..], StringComparer.Ordinal);
    }

    private void WriteCertificate((string Certificate, string Key)? pending, string file, ProxyCertificateSource source)
    {
        if (source != ProxyCertificateSource.Custom)
        {
            DeleteCertificate(file);
            return;
        }
        if (pending is not { } files)
        {
            return;
        }
        Directory.CreateDirectory(tlsOptions.Directory);
        WriteSecret(PathOf(file + ".key"), files.Key);
        WriteSecret(PathOf(file + ".pem"), files.Certificate);
    }

    private void DeleteCertificate(string file)
    {
        File.Delete(PathOf(file + ".pem"));
        File.Delete(PathOf(file + ".key"));
    }

    // Written next to the target and renamed over it, readable by root only.
    private static void WriteSecret(string path, string content)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, content);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private string PathOf(string file) => Path.Combine(tlsOptions.Directory, file);

    private static string FileName(bool isHost, long id) => string.Create(CultureInfo.InvariantCulture, $"proxy-{(isHost ? "host" : "redirect")}-{id}");

    // One client per kind of connection, shared by the hosts that connect alike. Nothing is decompressed, redirected
    // or given cookies on the way; the proxy writes traceparent itself.
    private HttpMessageInvoker Client(ProxyHostConfig config)
    {
        var tls = config.Target.Scheme == "https" ? config.Target.Tls : ProxyTargetTls.System;
        var root = tls == ProxyTargetTls.Box ? certificates.Root : null;
        var key = $"{tls}|{config.ConnectTimeoutSeconds}|{(root is null ? "" : CertificateAuthority.Fingerprint(root))}";
        lock (_clients)
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                return existing;
            }
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(config.ConnectTimeoutSeconds),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                EnableMultipleHttp2Connections = true,
                ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
            };
            if (tls == ProxyTargetTls.None)
            {
                // The administrator chose not to check a self-signed target.
#pragma warning disable CA5359
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            }
            else if (root is not null)
            {
                handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true,
                };
                handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(root);
            }
            var client = new HttpMessageInvoker(handler);
            _clients[key] = client;
            return client;
        }
    }

    // Targets speak HTTP/1.1 (WebSocket upgrades need it); an answer may stay silent for the host's timeout, an idle
    // WebSocket for a day.
    private static ForwarderRequestConfig Requests(ProxyHostConfig config, bool sockets) => new()
    {
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        ActivityTimeout = sockets ? TimeSpan.FromHours(24) : TimeSpan.FromSeconds(config.ResponseTimeoutSeconds),
        AllowResponseBuffering = false,
    };

    private BoxIdentity Identity()
    {
        IReadOnlyList<IPAddress> addresses = [];
        if (tlsOptions.ReadHost)
        {
            try
            {
                addresses = [.. NetworkInterface.GetAllNetworkInterfaces().SelectMany(card => card.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address))];
            }
            catch (NetworkInformationException)
            {
                // Without the cards only the names and loopback count as the box.
            }
        }
        return new BoxIdentity(host.HostName, addresses, transport.WebPort, [.. new[] { transport.ProxyPlainPort, transport.ProxyTlsPort, WebTransport.PlainPort, WebTransport.TlsPort }.OfType<int>().Distinct()]);
    }

    private static void EnsureFree(IReadOnlyList<string> domains, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.Ordinal);
        if (domains.FirstOrDefault(used.Contains) is { } clash)
        {
            throw new PithyxException(ErrorCodes.ProxyDomainTaken, new Dictionary<string, object?> { ["name"] = clash });
        }
    }

    private async Task EnsureAccountsAsync(List<string> users, List<string> groups, CancellationToken cancellationToken)
    {
        if (users.Count == 0 && groups.Count == 0)
        {
            return;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        var knownUsers = (await connection.QueryAsync<string>("SELECT username FROM users WHERE username IN @users", new { users })).ToHashSet(StringComparer.Ordinal);
        if (users.FirstOrDefault(name => !knownUsers.Contains(name)) is { } user)
        {
            throw new PithyxException(ErrorCodes.ProxySignInUserNotFound, new Dictionary<string, object?> { ["name"] = user });
        }
        var knownGroups = (await connection.QueryAsync<string>("SELECT name FROM groups WHERE name IN @groups", new { groups })).ToHashSet(StringComparer.Ordinal);
        if (groups.FirstOrDefault(name => !knownGroups.Contains(name)) is { } group)
        {
            throw new PithyxException(ErrorCodes.ProxySignInGroupNotFound, new Dictionary<string, object?> { ["name"] = group });
        }
    }

    private async Task<(IReadOnlyList<ProxyHostConfig> Hosts, IReadOnlyList<ProxyRedirectConfig> Redirects, IReadOnlyList<ProxyStreamConfig> Streams, UnknownHostAction Unknown, string? SignInAddress)> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var hosts = (await connection.QueryAsync<HostRow>("SELECT * FROM proxy_hosts ORDER BY id")).Select(row => row.ToConfig()).ToList();
        var redirects = (await connection.QueryAsync<RedirectRow>("SELECT * FROM proxy_redirects ORDER BY id")).Select(row => row.ToConfig()).ToList();
        var streams = (await connection.QueryAsync<StreamRow>("SELECT * FROM proxy_streams ORDER BY id")).Select(row => row.ToConfig()).ToList();
        var settings = (await connection.QueryAsync<(string Key, string Value)>(
            "SELECT key, value FROM settings WHERE key IN (@unknown, @signIn)", new { unknown = UnknownHostsSetting, signIn = SignInAddressSetting }))
            .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var unknown = settings.GetValueOrDefault(UnknownHostsSetting) == "notFound" ? UnknownHostAction.NotFound : UnknownHostAction.Redirect;
        return (hosts, redirects, streams, unknown, settings.GetValueOrDefault(SignInAddressSetting));
    }

    private static ProxyHost ToHost(ProxyHostConfig config, PresentedCertificate? presented, AcmeCertificateStatus? acmeStatus) => new(
        config.Id, config.Domains, config.Enabled, config.Target, config.WebSockets, config.Http2, config.Certificate,
        presented is null ? null : BoxCertificates.Info(presented.Certificate),
        config.ForceHttps, config.Hsts, config.ForwardedHeaders, config.PreserveHost, config.HostHeader,
        config.RequestHeaders, config.ResponseHeaders, config.MaxBodyMegabytes, config.ConnectTimeoutSeconds, config.ResponseTimeoutSeconds,
        config.AllowAddresses, config.DenyAddresses, [.. config.BasicAuth.Select(entry => entry.Username)], config.SignIn, config.AccessLog,
        ProxyValidator.LogSource(config.Domains), config.CreatedAt, config.UpdatedAt, acmeStatus);

    private static ProxyRedirect ToRedirect(ProxyRedirectConfig config, PresentedCertificate? presented, AcmeCertificateStatus? acmeStatus) => new(
        config.Id, config.Domains, config.Enabled, config.Target, config.Status, config.PreservePath, config.Certificate,
        presented is null ? null : BoxCertificates.Info(presented.Certificate), config.CreatedAt, config.UpdatedAt, acmeStatus);

    private static ProxyStream ToStream(ProxyStreamConfig config, bool listening) => new(
        config.Id, config.Name, config.Enabled, config.Port, config.Tcp, config.Udp, config.TargetHost, config.TargetPort, listening, config.CreatedAt, config.UpdatedAt);

    /// <summary>The ACME routes: their file name (<c>proxy-host-&lt;id&gt;</c>), domains and whether they answer.</summary>
    public async Task<IReadOnlyList<AcmeRoute>> AcmeRoutesAsync(CancellationToken cancellationToken)
    {
        var (hosts, redirects, _, _, _) = await ReadAsync(cancellationToken);
        return
        [
            .. hosts.Where(config => config.Certificate == ProxyCertificateSource.Acme).Select(config => new AcmeRoute(FileName(isHost: true, config.Id), config.Domains, config.Enabled)),
            .. redirects.Where(config => config.Certificate == ProxyCertificateSource.Acme).Select(config => new AcmeRoute(FileName(isHost: false, config.Id), config.Domains, config.Enabled)),
        ];
    }

    /// <summary>The path of an ACME certificate file of a route, <c>.pem</c> or <c>.key</c>.</summary>
    public string AcmePath(string route, string extension) => PathOf(AcmeFile(route) + extension);

    /// <summary>The file name of a route of the given kind and number, which also names it in <c>proxy_acme</c>.</summary>
    public static string RouteName(bool isHost, long id) => FileName(isHost, id);

    private static string AcmeFile(string file) => file + "-acme";

    // A host may use the authority only after its terms were agreed and while port 80 answers its checks.
    private async Task CheckAcmeAsync(ProxyCertificateSource source, IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        if (source != ProxyCertificateSource.Acme)
        {
            return;
        }
        AcmeRules.CheckDomains(domains);
        if (transport.ProxyPlainPort is null)
        {
            throw new PithyxException(ErrorCodes.ProxyAcmePortUnavailable);
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = AcmeService.SettingsKey });
        if (json is null || JsonSerializer.Deserialize<AcmeSettings>(json, _json)?.TermsAgreed is null)
        {
            throw new PithyxException(ErrorCodes.ProxyAcmeTermsRequired);
        }
    }

    // A route that no longer uses the authority drops its certificate and state; one that does is looked at now.
    private async Task ForgetAcmeAsync(ProxyCertificateSource source, string file, CancellationToken cancellationToken)
    {
        if (source == ProxyCertificateSource.Acme)
        {
            acme.Wake();
            return;
        }
        File.Delete(PathOf(AcmeFile(file) + ".pem"));
        File.Delete(PathOf(AcmeFile(file) + ".key"));
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM proxy_acme WHERE route = @file", new { file });
    }

    private async Task<Dictionary<string, AcmeRow>> AcmeRowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return (await connection.QueryAsync<AcmeRow>("SELECT * FROM proxy_acme")).ToDictionary(row => row.route, StringComparer.Ordinal);
    }

    private AcmeCertificateStatus? AcmeStatus(ProxyCertificateSource source, string file, IReadOnlyList<string> domains, Dictionary<string, AcmeRow> rows)
    {
        if (source != ProxyCertificateSource.Acme)
        {
            return null;
        }
        var row = rows.GetValueOrDefault(file);
        var (notBefore, notAfter) = AcmeDates(file);
        var ordered = row is null ? null : Json<List<string>>(row.domains);
        var attempted = row is null ? (DateTimeOffset?)null : Parse(row.attempted_at);
        var retry = AcmeRules.NextAttempt(domains, ordered ?? (notAfter is null ? null : domains), notBefore, notAfter, attempted, (int)(row?.failures ?? 0));
        return new AcmeCertificateStatus(
            row?.status ?? (notAfter is null ? "pending" : "valid"),
            notAfter,
            attempted,
            retry == DateTimeOffset.MinValue ? time.GetUtcNow() : retry,
            acme.IsRunning(file),
            row?.error_code,
            row?.error_params is { } parameters ? JsonSerializer.Deserialize<Dictionary<string, object?>>(parameters, _json) : null);
    }

    /// <summary>The validity of a route's ACME certificate file, if it has one.</summary>
    public (DateTimeOffset? NotBefore, DateTimeOffset? NotAfter) AcmeDates(string file)
    {
        var path = PathOf(AcmeFile(file) + ".pem");
        if (!File.Exists(path))
        {
            return (null, null);
        }
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
            return (new DateTimeOffset(certificate.NotBefore.ToUniversalTime()), new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (null, null);
        }
    }

    private static string SourceName(ProxyCertificateSource source) => source switch
    {
        ProxyCertificateSource.Custom => "custom",
        ProxyCertificateSource.Acme => "acme",
        _ => "box",
    };

    private static ProxyCertificateSource ParseSource(string name) => name switch
    {
        "custom" => ProxyCertificateSource.Custom,
        "acme" => ProxyCertificateSource.Acme,
        _ => ProxyCertificateSource.Box,
    };

    private static string Name(ProxyTargetTls tls) => tls switch
    {
        ProxyTargetTls.Box => "box",
        ProxyTargetTls.None => "none",
        _ => "system",
    };

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static T Json<T>(string value) => JsonSerializer.Deserialize<T>(value, _json) ?? throw new InvalidOperationException("A stored list of the reverse proxy is empty.");

    private static PithyxException HostNotFound(long id) => new(ErrorCodes.ProxyHostNotFound, new Dictionary<string, object?> { ["id"] = id });

    private static PithyxException RedirectNotFound(long id) => new(ErrorCodes.ProxyRedirectNotFound, new Dictionary<string, object?> { ["id"] = id });

    private static PithyxException StreamNotFound(long id) => new(ErrorCodes.ProxyStreamNotFound, new Dictionary<string, object?> { ["id"] = id });

#pragma warning disable CA1812, IDE1006 // Dapper fills the rows by column name.
    private sealed class HostRow
    {
        public long id { get; set; }
        public string domains { get; set; } = "";
        public long enabled { get; set; }
        public string target_scheme { get; set; } = "";
        public string target_host { get; set; } = "";
        public long target_port { get; set; }
        public string target_tls { get; set; } = "";
        public long target_on_box { get; set; }
        public long websockets { get; set; }
        public long http2 { get; set; }
        public string certificate { get; set; } = "";
        public long force_https { get; set; }
        public long hsts { get; set; }
        public long forwarded_headers { get; set; }
        public long preserve_host { get; set; }
        public string? host_header { get; set; }
        public string request_headers { get; set; } = "[]";
        public string response_headers { get; set; } = "[]";
        public long? max_body_megabytes { get; set; }
        public long connect_timeout_seconds { get; set; }
        public long response_timeout_seconds { get; set; }
        public string allow_addresses { get; set; } = "[]";
        public string deny_addresses { get; set; } = "[]";
        public string basic_auth { get; set; } = "[]";
        public long sign_in { get; set; }
        public string sign_in_users { get; set; } = "[]";
        public string sign_in_groups { get; set; } = "[]";
        public long access_log { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";

        public ProxyHostConfig ToConfig() => new(
            id,
            Json<List<string>>(domains),
            enabled == 1,
            new ProxyTarget(target_scheme, target_host, (int)target_port, target_tls switch
            {
                "box" => ProxyTargetTls.Box,
                "none" => ProxyTargetTls.None,
                _ => ProxyTargetTls.System,
            }, target_on_box == 1),
            websockets == 1,
            http2 == 1,
            ParseSource(certificate),
            force_https == 1,
            hsts == 1,
            forwarded_headers == 1,
            preserve_host == 1,
            host_header,
            Json<List<ProxyHeader>>(request_headers),
            Json<List<ProxyHeader>>(response_headers),
            (int?)max_body_megabytes,
            (int)connect_timeout_seconds,
            (int)response_timeout_seconds,
            Json<List<string>>(allow_addresses),
            Json<List<string>>(deny_addresses),
            Json<List<BasicAuthEntry>>(basic_auth),
            new ProxySignInRule(sign_in == 1, Json<List<string>>(sign_in_users), Json<List<string>>(sign_in_groups)),
            access_log == 1,
            Parse(created_at),
            Parse(updated_at));
    }

    private sealed class RedirectRow
    {
        public long id { get; set; }
        public string domains { get; set; } = "";
        public long enabled { get; set; }
        public string target { get; set; } = "";
        public long status { get; set; }
        public long preserve_path { get; set; }
        public string certificate { get; set; } = "";
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";

        public ProxyRedirectConfig ToConfig() => new(
            id, Json<List<string>>(domains), enabled == 1, target, (int)status, preserve_path == 1,
            ParseSource(certificate), Parse(created_at), Parse(updated_at));
    }

    private sealed class StreamRow
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public long enabled { get; set; }
        public long port { get; set; }
        public long tcp { get; set; }
        public long udp { get; set; }
        public string target_host { get; set; } = "";
        public long target_port { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";

        public ProxyStreamConfig ToConfig() => new(
            id, name, enabled == 1, (int)port, tcp == 1, udp == 1, target_host, (int)target_port, Parse(created_at), Parse(updated_at));
    }

    private sealed class AcmeRow
    {
        public string route { get; set; } = "";
        public string domains { get; set; } = "";
        public string status { get; set; } = "";
        public string? not_after { get; set; }
        public string attempted_at { get; set; } = "";
        public long failures { get; set; }
        public string? error_code { get; set; }
        public string? error_params { get; set; }
    }
#pragma warning restore CA1812, IDE1006

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved proxy host {Id} for {Domains} to {TargetHost}:{TargetPort}")]
    private partial void LogHostSaved(long id, IReadOnlyList<string> domains, string targetHost, int targetPort);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted proxy host {Id}")]
    private partial void LogHostDeleted(long id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved redirect host {Id} for {Domains} to {Target}")]
    private partial void LogRedirectSaved(long id, IReadOnlyList<string> domains, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved stream {Id} on port {Port} to {TargetHost}:{TargetPort}")]
    private partial void LogStreamSaved(long id, int port, string targetHost, int targetPort);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The uploaded certificate {File} of the reverse proxy is not usable; the host presents the box's own")]
    private partial void LogCertificateUnusable(string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reloading the reverse proxy failed")]
    private partial void LogReloadFailed(Exception exception);
}
