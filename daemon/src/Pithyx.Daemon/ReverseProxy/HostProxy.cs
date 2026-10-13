using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Logs;
using Yarp.ReverseProxy.Forwarder;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>
/// The reverse proxy on the proxy ports (80 and 443 on a box, D347), before everything else of the daemon: a request
/// whose host name belongs to an enabled proxy host is forwarded with YARP's forwarder, one of a redirect host is
/// redirected, and any other goes on to <see cref="TransportRedirect"/> (to 7484, or <c>/api/install-id</c> on 80) or
/// gets 404 when the administrator chose so. 7484 never forwards. In front of the target the proxy checks, in this
/// order, a loop back into itself, the host's allowed and denied addresses, its Basic Auth and its Pithyx sign-in. The
/// target never gets incoming <c>X-Pithyx-*</c>, <c>X-Forwarded-*</c>, <c>Forwarded</c> or trace headers, nor the
/// box's own cookies; answers cannot set them either. Answers stream through unbuffered (server-sent events, large
/// downloads).
/// </summary>
public sealed partial class HostProxy(
    RequestDelegate next,
    WebTransport transport,
    ProxyService proxy,
    ProxySignIn signIn,
    IHttpForwarder forwarder,
    PasswordHasher hasher,
    LoginRateLimiter limiter,
    LogStore logs,
    IProblemDetailsService problemDetails,
    TimeProvider time)
{
    private const int MaxBasicAuthCache = 1000;
    private static readonly TimeSpan _basicAuthCacheLifetime = TimeSpan.FromMinutes(5);

    // Successful Basic Auth checks by hash of host, its last change and the header, so Argon2 runs once per user and five minutes.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _basicAuth = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var port = context.Connection.LocalPort;
        var tls = port == transport.ProxyTlsPort;
        if (!tls && port != transport.ProxyPlainPort)
        {
            await next(context);
            return;
        }
        var state = proxy.State;
        var request = context.Request;
        var route = state.Table.Resolve(request.Host.Host);
        switch (ProxyTable.Decide(route, tls, transport.ProxyTlsPort is not null, state.UnknownHosts, request.Path))
        {
            case ProxyAction.Next:
                await next(context);
                return;
            case ProxyAction.NotFound:
                await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyHostUnknown);
                return;
            case ProxyAction.Redirect:
                var redirect = ((RedirectRoute)route!).Redirect;
                context.Response.StatusCode = redirect.Status;
                context.Response.Headers.Location = ProxyTable.RedirectLocation(redirect, request.Path, request.QueryString);
                return;
            case ProxyAction.RedirectToHttps:
                var safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
                context.Response.StatusCode = safe ? StatusCodes.Status301MovedPermanently : StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = HttpsLocation(request, transport.ProxyTlsPort!.Value);
                return;
            case ProxyAction.Forward:
                if (state.Hosts.TryGetValue(((HostRoute)route!).Host.Id, out var host))
                {
                    try
                    {
                        await ForwardAsync(context, host, state, tls);
                    }
                    catch (PithyxException exception) when (!context.Response.HasStarted)
                    {
                        // The proxy runs before the daemon's exception handler; a refusal (too many failed sign-ins) answers here.
                        await problemDetails.WriteProblemAsync(context, exception.Code);
                    }
                    return;
                }
                await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyHostUnknown);
                return;
        }
    }

    /// <summary>The same address on HTTPS: the default port 443 is left out, any other is written.</summary>
    public static string HttpsLocation(HttpRequest request, int tlsPort)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host.Host;
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }
        var port = tlsPort == WebTransport.TlsPort ? "" : string.Create(CultureInfo.InvariantCulture, $":{tlsPort}");
        return $"https://{host}{port}{request.PathBase}{request.Path}{request.QueryString}";
    }

    /// <summary>Whether <paramref name="address"/> may reach a host with these lists: never when denied, and only when allowed if any are.</summary>
    public static bool AddressAllowed(IPAddress? address, IReadOnlyList<IPNetwork> allow, IReadOnlyList<IPNetwork> deny)
    {
        ArgumentNullException.ThrowIfNull(allow);
        ArgumentNullException.ThrowIfNull(deny);
        if (address is null)
        {
            return allow.Count == 0 && deny.Count == 0;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (deny.Any(network => network.Contains(address)))
        {
            return false;
        }
        return allow.Count == 0 || allow.Any(network => network.Contains(address));
    }

    /// <summary>The <c>Cookie</c> header without the box's own cookies (<c>pithyx_*</c>), or null when nothing is left.</summary>
    public static string? WithoutBoxCookies(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }
        var kept = header.Split(';')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0 && !part.StartsWith("pithyx_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    private async Task ForwardAsync(HttpContext context, HostRuntime host, ProxyState state, bool tls)
    {
        var config = host.Config;
        var request = context.Request;
        var started = Stopwatch.GetTimestamp();
        string? user = null;
        try
        {
            if (request.Headers[ProxyService.LoopHeader] == proxy.LoopToken)
            {
                await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyLoopDetected);
                return;
            }
            if (!AddressAllowed(context.Connection.RemoteIpAddress, host.Allow, host.Deny))
            {
                await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyAddressDenied);
                return;
            }
            if (config.BasicAuth.Count > 0)
            {
                user = await BasicAuthAsync(context, config);
                if (user is null)
                {
                    context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{config.Domains[0]}\", charset=\"UTF-8\"";
                    await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyAuthRequired);
                    return;
                }
            }
            Auth.UserInfo? signedIn = null;
            if (config.SignIn.Required)
            {
                if (request.Path.Equals(ProxySignIn.AuthPath, StringComparison.Ordinal))
                {
                    if (!await signIn.CompleteAsync(context, config))
                    {
                        await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyAuthRequired);
                    }
                    return;
                }
                signedIn = await signIn.CheckAsync(context, config);
                if (signedIn is null)
                {
                    if (IsNavigation(request))
                    {
                        context.Response.Redirect(signIn.SignInUrl(request, state.SignInAddress), permanent: false);
                    }
                    else
                    {
                        await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyAuthRequired);
                    }
                    return;
                }
                user = signedIn.Username;
            }
            var upgrade = context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true
                || context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true;
            if (upgrade && !config.WebSockets)
            {
                await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyWebSocketsDisabled);
                return;
            }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = config.MaxBodyMegabytes is { } megabytes ? megabytes * 1024L * 1024L : null;
            }
            var transformer = new Transformer(config, signedIn, proxy.LoopToken, tls);
            var error = await forwarder.SendAsync(
                context,
                ProxyService.TargetUri(config.Target).AbsoluteUri,
                host.Client,
                upgrade ? host.Sockets : host.Requests,
                transformer,
                context.RequestAborted);
            if (error != ForwarderError.None && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                var status = context.Response.StatusCode;
                context.Response.Clear();
                if (error is ForwarderError.RequestTimedOut)
                {
                    await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyTargetTimeout);
                }
                else if (error is ForwarderError.Request or ForwarderError.ResponseHeaders or ForwarderError.UpgradeRequestDestination or ForwarderError.NoAvailableDestinations)
                {
                    await problemDetails.WriteProblemAsync(context, ErrorCodes.ProxyTargetUnavailable);
                }
                else
                {
                    // YARP answers the client's own failures (a broken body, a cancelled upload) with its status.
                    context.Response.StatusCode = status;
                }
            }
        }
        finally
        {
            if (config.AccessLog)
            {
                Log(context, host, user, Stopwatch.GetElapsedTime(started));
            }
        }
    }

    // A browser loading a page: the sign-in redirect is only useful there; scripts get 401.
    private static bool IsNavigation(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && (request.Headers["Sec-Fetch-Mode"] == "navigate"
            || (request.Headers["Sec-Fetch-Mode"].Count == 0 && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)));

    // The Basic Auth user, or null. Failures count towards the sign-in limits of the address and the name.
    private async Task<string?> BasicAuthAsync(HttpContext context, ProxyHostConfig config)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!AuthenticationHeaderValue.TryParse(header, out var value)
            || !string.Equals(value.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || value.Parameter is null)
        {
            return null;
        }
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value.Parameter));
        }
        catch (FormatException)
        {
            return null;
        }
        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }
        var username = decoded[..colon];
        var password = decoded[(colon + 1)..];
        var now = time.GetUtcNow();
        var key = Convert.ToHexString(Secrets.Sha256(string.Create(CultureInfo.InvariantCulture, $"{config.Id}|{config.UpdatedAt:O}|{header}")));
        if (_basicAuth.TryGetValue(key, out var until) && until > now)
        {
            return username;
        }
        var limitName = string.Create(CultureInfo.InvariantCulture, $"proxy:{config.Id}:{username}");
        limiter.EnsureAllowed(context, limitName);
        var entry = config.BasicAuth.FirstOrDefault(candidate => candidate.Username == username);
        if (entry is null)
        {
            await hasher.WasteTimeAsync(password, context.RequestAborted);
            limiter.RecordFailure(context, limitName);
            return null;
        }
        if (!(await hasher.VerifyAsync(password, entry.Hash, context.RequestAborted)).Valid)
        {
            limiter.RecordFailure(context, limitName);
            return null;
        }
        if (_basicAuth.Count >= MaxBasicAuthCache)
        {
            _basicAuth.Clear();
        }
        _basicAuth[key] = now + _basicAuthCacheLifetime;
        return username;
    }

    // One entry per request in the Log Center under the host's source; the path without its query, which may hold secrets.
    private void Log(HttpContext context, HostRuntime host, string? user, TimeSpan duration)
    {
        var request = context.Request;
        var status = context.Response.StatusCode;
        var client = context.Connection.RemoteIpAddress;
        var milliseconds = (long)duration.TotalMilliseconds;
        logs.Enqueue(new PendingLogEntry(
            time.GetUtcNow(),
            status >= 500 ? LogLevelName.Warn : LogLevelName.Info,
            host.LogSource,
            string.Create(CultureInfo.InvariantCulture, $"{request.Method} {request.Host.Host}{request.Path} {status} {milliseconds} ms"),
            Tracing.CurrentRequestId,
            user,
            new Dictionary<string, string>
            {
                ["method"] = request.Method,
                ["host"] = request.Host.Host,
                ["path"] = request.Path.Value ?? "/",
                ["status"] = status.ToString(CultureInfo.InvariantCulture),
                ["durationMs"] = milliseconds.ToString(CultureInfo.InvariantCulture),
                ["client"] = (client is { IsIPv4MappedToIPv6: true } ? client.MapToIPv4() : client)?.ToString() ?? "",
                ["protocol"] = request.Protocol,
            }));
    }

    /// <summary>The headers between the browser and the target.</summary>
    private sealed class Transformer(ProxyHostConfig config, Auth.UserInfo? user, string loopToken, bool tls) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            var request = httpContext.Request;
            var headers = proxyRequest.Headers;
            Strip(headers);
            if (proxyRequest.Content is { } content)
            {
                Strip(content.Headers);
            }
            headers.Host = ProxyService.TargetHostHeader(config, request.Host.Value ?? "");
            headers.Remove(HeaderNames.Cookie);
            if (WithoutBoxCookies(request.Headers.Cookie.ToString()) is { } cookies)
            {
                headers.TryAddWithoutValidation(HeaderNames.Cookie, cookies);
            }
            if (config.BasicAuth.Count > 0)
            {
                headers.Authorization = null;
            }
            headers.TryAddWithoutValidation("traceparent", Activity.Current?.Id ?? $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01");
            headers.TryAddWithoutValidation(ProxyService.LoopHeader, loopToken);
            if (config.ForwardedHeaders)
            {
                var client = httpContext.Connection.RemoteIpAddress;
                headers.TryAddWithoutValidation("X-Forwarded-For", (client is { IsIPv4MappedToIPv6: true } ? client.MapToIPv4() : client)?.ToString() ?? "");
                headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
                headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value ?? "");
            }
            if (user is not null)
            {
                headers.TryAddWithoutValidation("X-Pithyx-User", user.Username);
                headers.TryAddWithoutValidation("X-Pithyx-User-Id", user.Id.ToString(CultureInfo.InvariantCulture));
                headers.TryAddWithoutValidation("X-Pithyx-Groups", string.Join(',', user.Groups.Select(group => group.Name)));
                headers.TryAddWithoutValidation("X-Pithyx-Admin", user.IsAdmin ? "true" : "false");
            }
            foreach (var header in config.RequestHeaders)
            {
                headers.Remove(header.Name);
                proxyRequest.Content?.Headers.Remove(header.Name);
                if (header.Value.Length > 0 && !headers.TryAddWithoutValidation(header.Name, header.Value))
                {
                    proxyRequest.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
                }
            }
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var proceed = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            var headers = httpContext.Response.Headers;
            // A target must not set the box's own cookies, which the same name on 7484 would receive.
            if (headers.SetCookie.Count > 0)
            {
                headers.SetCookie = headers.SetCookie.Where(cookie => cookie is not null && !cookie.TrimStart().StartsWith("pithyx_", StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            if (tls && config.Hsts)
            {
                headers.StrictTransportSecurity = "max-age=31536000";
            }
            foreach (var header in config.ResponseHeaders)
            {
                if (header.Value.Length == 0)
                {
                    headers.Remove(header.Name);
                }
                else
                {
                    headers[header.Name] = header.Value;
                }
            }
            return proceed;
        }

        private static void Strip(HttpHeaders headers)
        {
            foreach (var name in headers.Select(header => header.Key).Where(IsStripped).ToList())
            {
                headers.Remove(name);
            }
        }

        private static bool IsStripped(string name) =>
            name.StartsWith("X-Pithyx-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            || name.Equals(HeaderNames.ProxyAuthorization, StringComparison.OrdinalIgnoreCase)
            || name.Equals("traceparent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tracestate", StringComparison.OrdinalIgnoreCase);
    }
}
