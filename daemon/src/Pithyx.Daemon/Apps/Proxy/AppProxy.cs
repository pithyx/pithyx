using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Errors;
using Yarp.ReverseProxy.Forwarder;

namespace Pithyx.Daemon.Apps.Proxy;

/// <summary>
/// The proxy to app backends (D303, D316, D323) at <c>/apps/&lt;id&gt;/api/...</c> (the endpoint <c>api</c>) and
/// <c>/apps/&lt;id&gt;/api-&lt;name&gt;/...</c>, with YARP's forwarder in the daemon. A preflight from a sandboxed window
/// (<c>Origin: null</c>) is answered here, any other origin's is refused. A request needs a backend token
/// (<c>Authorization: Bearer</c> or <c>?pithyx-token=</c>) and a WebSocket a one-time ticket for its endpoint
/// (<c>?pithyx-ticket=</c>); a cookie never counts, and every use checks the session, the user and the app again
/// (<see cref="AppTokenCheck"/>). The backend gets the request without <c>Cookie</c>, <c>Authorization</c>, the
/// <c>pithyx-*</c> parameters, <c>Forwarded</c> and any incoming <c>X-Pithyx-*</c> or <c>X-Forwarded-*</c> header, plus
/// the checked user (<c>X-Pithyx-User</c>, <c>-User-Id</c>, <c>-Groups</c>, <c>-Admin</c>), <c>traceparent</c> of the
/// request's activity and where it came from (<c>X-Forwarded-Prefix</c>, <c>-For</c>, <c>-Host</c>, <c>-Proto</c>).
/// Answers lose the headers that act on the whole origin (<c>Set-Cookie</c>, <c>Clear-Site-Data</c>, HSTS and the like),
/// are readable from any origin, and carry the frontend's sandboxing policy, so a
/// backend page opened in a tab never runs in the desktop's origin. Placed before the frontend hosting, which answers
/// every other path under <c>/apps/</c>.
/// </summary>
public sealed partial class AppProxy(
    RequestDelegate next,
    IHttpForwarder forwarder,
    AppRegistry registry,
    AppTokens tokens,
    AppTokenCheck check,
    AppBackends backends,
    AppProxySockets sockets,
    Docker.AppBackendSupervisor supervisor,
    IProblemDetailsService problemDetails)
{
    public const string TokenParameter = "pithyx-token";
    public const string TicketParameter = "pithyx-ticket";

    private const string PreflightMethods = "GET, HEAD, POST, PUT, PATCH, DELETE";

    // Container backends speak HTTP/1.1; nothing is decompressed, redirected, cached or given cookies on the way.
    private static readonly HttpMessageInvoker _client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        // The proxy writes traceparent itself, from the request's activity.
        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
    });

    private static readonly ForwarderRequestConfig _requests = new()
    {
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    // An idle WebSocket (a chat nobody types in) stays open; a revoke closes it through AppProxySockets.
    private static readonly ForwarderRequestConfig _sockets = _requests with { ActivityTimeout = TimeSpan.FromHours(24) };

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Match(context.Request.Path) is not { } match)
        {
            await next(context);
            return;
        }
        var request = context.Request;
        var app = registry.Find(match.AppId);
        var internet = app?.HasInternet == true;
        AppFrontendHosting.Sandbox(context.Response.Headers, internet);
        if (HttpMethods.IsOptions(request.Method) && request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod))
        {
            Preflight(context);
            return;
        }
        var upgrade = IsUpgrade(context);
        var grant = upgrade
            ? tokens.RedeemTicket(request.Query[TicketParameter]) is { } ticket && ticket.Endpoint == match.Endpoint ? ticket : null
            : tokens.Validate(AppTokenAuthenticationHandler.BearerToken(request) ?? request.Query[TokenParameter].FirstOrDefault(), AppTokenKind.Backend);
        if (grant is null || grant.AppId != match.AppId)
        {
            await problemDetails.WriteProblemAsync(context, ErrorCodes.AppTokenInvalid);
            return;
        }
        // A socket is tracked before its check, so a sign-out while the check runs closes it as well.
        using var socket = upgrade ? sockets.Open(grant, context) : null;
        if (await check.CheckAsync(grant, withGroups: true, context.RequestAborted) is not { } caller || socket?.Closing.IsCancellationRequested == true)
        {
            await problemDetails.WriteProblemAsync(context, ErrorCodes.AppTokenInvalid);
            return;
        }
        if (app is null || !app.HasEndpoint(match.Endpoint))
        {
            await problemDetails.WriteProblemAsync(context, ErrorCodes.RequestRouteNotFound);
            return;
        }
        var destination = backends.Find(match.AppId, match.Endpoint);
        // A stopped onDemand backend starts for the request, which waits up to 30 seconds for it.
        if (destination is null && await supervisor.WakeAsync(match.AppId, context.RequestAborted))
        {
            destination = backends.Find(match.AppId, match.Endpoint);
        }
        if (destination is null)
        {
            await problemDetails.WriteProblemAsync(context, ErrorCodes.AppBackendUnavailable);
            return;
        }
        // The backend decides how large an upload may be.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }
        var transformer = new Transformer(caller, match, internet);
        ForwarderError error;
        if (socket is not null)
        {
            socket.Destination = destination;
            error = await forwarder.SendAsync(context, destination.AbsoluteUri, _client, _sockets, transformer, socket.Closing);
        }
        else
        {
            error = await forwarder.SendAsync(context, destination.AbsoluteUri, _client, _requests, transformer, context.RequestAborted);
        }
        if (error == ForwarderError.None || context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        // YARP answers the client's own failures (a broken body, a cancelled upload) with its status; only a backend that
        // cannot be reached or does not answer is unavailable.
        var status = context.Response.StatusCode;
        context.Response.Clear();
        AppFrontendHosting.Sandbox(context.Response.Headers, internet);
        if (error is ForwarderError.Request or ForwarderError.RequestTimedOut or ForwarderError.ResponseHeaders or ForwarderError.UpgradeRequestDestination)
        {
            await problemDetails.WriteProblemAsync(context, ErrorCodes.AppBackendUnavailable);
        }
        else
        {
            context.Response.StatusCode = status;
        }
    }

    /// <summary>The app and endpoint of a backend path, or null for any other path.</summary>
    internal static ProxyMatch? Match(PathString path)
    {
        if (!path.StartsWithSegments(AppFrontendHosting.Prefix, StringComparison.Ordinal, out var rest) || rest.Value is not { Length: > 1 } value)
        {
            return null;
        }
        var parts = value[1..].Split('/', 3);
        if (parts.Length < 2 || EndpointSegment().Match(parts[1]) is not { Success: true } segment)
        {
            return null;
        }
        var endpoint = segment.Groups["name"].Success ? segment.Groups["name"].Value : "api";
        // One URL per endpoint: api-api is not another name for api.
        if (segment.Groups["name"].Success && endpoint == "api")
        {
            return null;
        }
        return new ProxyMatch(parts[0], endpoint, $"{AppFrontendHosting.Prefix}/{parts[0]}/{parts[1]}", parts.Length == 3 ? "/" + parts[2] : "/");
    }

    /// <summary>The query as written without the <c>pithyx-*</c> parameters; empty or starting with <c>?</c>.</summary>
    internal static string StripQuery(string? query)
    {
        var kept = (query ?? "").TrimStart('?').Split('&').Where(pair =>
        {
            if (pair.Length == 0)
            {
                return false;
            }
            var name = pair.Split('=', 2)[0];
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(name.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                decoded = name;
            }
            return !decoded.StartsWith("pithyx-", StringComparison.OrdinalIgnoreCase);
        }).ToList();
        return kept.Count == 0 ? "" : "?" + string.Join('&', kept);
    }

    private static bool IsUpgrade(HttpContext context) =>
        context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true
        || context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true;

    private static void Preflight(HttpContext context)
    {
        var headers = context.Response.Headers;
        if (context.Request.Headers[HeaderNames.Origin] != "null")
        {
            headers.Remove(HeaderNames.AccessControlAllowOrigin);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        headers[HeaderNames.AccessControlAllowMethods] = PreflightMethods;
        headers[HeaderNames.AccessControlAllowHeaders] = context.Request.Headers[HeaderNames.AccessControlRequestHeaders];
        headers[HeaderNames.AccessControlMaxAge] = "600";
    }

    [GeneratedRegex("^api(?:-(?<name>[a-z][a-z0-9-]{0,31}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointSegment();

    /// <summary>The headers between the browser and the backend (D316).</summary>
    private sealed class Transformer(AppCaller caller, ProxyMatch match, bool internet) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            var request = httpContext.Request;
            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, new PathString(match.Path), new QueryString(StripQuery(request.QueryString.Value)));
            proxyRequest.Headers.Host = null;
            Strip(proxyRequest.Headers);
            if (proxyRequest.Content is { } content)
            {
                Strip(content.Headers);
            }
            var user = caller.User;
            var headers = proxyRequest.Headers;
            headers.TryAddWithoutValidation("traceparent", Activity.Current?.Id ?? $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01");
            headers.TryAddWithoutValidation("X-Pithyx-User", user.Username);
            headers.TryAddWithoutValidation("X-Pithyx-User-Id", user.Id.ToString(CultureInfo.InvariantCulture));
            headers.TryAddWithoutValidation("X-Pithyx-Groups", string.Join(',', user.Groups.Select(group => group.Name)));
            headers.TryAddWithoutValidation("X-Pithyx-Admin", user.IsAdmin ? "true" : "false");
            headers.TryAddWithoutValidation("X-Forwarded-Prefix", match.Prefix);
            var client = httpContext.Connection.RemoteIpAddress;
            headers.TryAddWithoutValidation("X-Forwarded-For", (client is { IsIPv4MappedToIPv6: true } ? client.MapToIPv4() : client)?.ToString() ?? "");
            headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value ?? "");
            headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var proceed = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            var headers = httpContext.Response.Headers;
            foreach (var name in headers.Keys.Where(IsOriginWide).ToList())
            {
                headers.Remove(name);
            }
            AppFrontendHosting.Sandbox(headers, internet);
            headers[HeaderNames.AccessControlExposeHeaders] = "*";
            return proceed;
        }

        // Answer headers that act on the whole box's origin, not on the one answer: cookies, wiping site data, HSTS (which
        // would end the click-through on the default self-signed certificate), other protocols and reporting, CORS with
        // credentials, and a page refresh.
        private static readonly HashSet<string> _originWide = new(StringComparer.OrdinalIgnoreCase)
        {
            "Set-Cookie", "Set-Cookie2", "Clear-Site-Data", "Strict-Transport-Security", "Alt-Svc", "Refresh",
            "Report-To", "Reporting-Endpoints", "NEL", "Public-Key-Pins", "Public-Key-Pins-Report-Only", "Expect-CT",
            "Content-Security-Policy-Report-Only", "Origin-Agent-Cluster", "Service-Worker-Allowed",
            "Access-Control-Allow-Credentials", "Access-Control-Allow-Methods", "Access-Control-Allow-Headers", "Access-Control-Max-Age",
        };

        private static bool IsOriginWide(string name) => _originWide.Contains(name);

        private static void Strip(System.Net.Http.Headers.HttpHeaders headers)
        {
            foreach (var name in headers.Select(header => header.Key).Where(IsStripped).ToList())
            {
                headers.Remove(name);
            }
        }

        private static bool IsStripped(string name) =>
            name.Equals(HeaderNames.Cookie, StringComparison.OrdinalIgnoreCase)
            || name.Equals(HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase)
            || name.Equals(HeaderNames.ProxyAuthorization, StringComparison.OrdinalIgnoreCase)
            || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            // A referrer may carry ?pithyx-token= of a page opened in a tab.
            || name.Equals(HeaderNames.Referer, StringComparison.OrdinalIgnoreCase)
            || name.Equals("traceparent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tracestate", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Pithyx-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A path of the proxy: the app, the endpoint name, the prefix up to the endpoint segment and the path below it.</summary>
internal sealed record ProxyMatch(string AppId, string Endpoint, string Prefix, string Path);
