namespace Pithyx.Daemon.ReverseProxy;

/// <summary>A Basic Auth user as stored: the name and an Argon2id hash of the password.</summary>
public sealed record BasicAuthEntry(string Username, string Hash);

/// <summary>A proxy host as stored, with the hashes of its Basic Auth users.</summary>
public sealed record ProxyHostConfig(
    long Id,
    IReadOnlyList<string> Domains,
    bool Enabled,
    ProxyTarget Target,
    bool WebSockets,
    bool Http2,
    ProxyCertificateSource Certificate,
    bool ForceHttps,
    bool Hsts,
    bool ForwardedHeaders,
    bool PreserveHost,
    string? HostHeader,
    IReadOnlyList<ProxyHeader> RequestHeaders,
    IReadOnlyList<ProxyHeader> ResponseHeaders,
    int? MaxBodyMegabytes,
    int ConnectTimeoutSeconds,
    int ResponseTimeoutSeconds,
    IReadOnlyList<string> AllowAddresses,
    IReadOnlyList<string> DenyAddresses,
    IReadOnlyList<BasicAuthEntry> BasicAuth,
    ProxySignInRule SignIn,
    bool AccessLog,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A redirect host as stored.</summary>
public sealed record ProxyRedirectConfig(
    long Id,
    IReadOnlyList<string> Domains,
    bool Enabled,
    string Target,
    int Status,
    bool PreservePath,
    ProxyCertificateSource Certificate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A stream as stored.</summary>
public sealed record ProxyStreamConfig(long Id, string Name, bool Enabled, int Port, bool Tcp, bool Udp, string TargetHost, int TargetPort, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What a name on the proxy's ports belongs to: a proxy host or a redirect host.</summary>
public abstract record ProxyRoute(IReadOnlyList<string> Domains);

public sealed record HostRoute(ProxyHostConfig Host) : ProxyRoute(Host.Domains);

public sealed record RedirectRoute(ProxyRedirectConfig Redirect) : ProxyRoute(Redirect.Domains);

/// <summary>What the proxy does with a request on 80 or 443.</summary>
public enum ProxyAction
{
    /// <summary>Not the proxy's: the next middleware redirects to 7484 or answers <c>/api/install-id</c>.</summary>
    Next,

    /// <summary>Forward to the proxy host's target.</summary>
    Forward,

    /// <summary>Redirect plain HTTP to HTTPS on the same name.</summary>
    RedirectToHttps,

    /// <summary>Redirect to the redirect host's target.</summary>
    Redirect,

    /// <summary>Answer 404 for an unknown name.</summary>
    NotFound,
}

/// <summary>
/// The enabled hosts by name (D347): an exact name first, then a wildcard <c>*.rest</c> for a name with one more
/// label in front, as a certificate's wildcard covers it. Names compare in lower case without a final dot. Immutable;
/// the proxy swaps a new table in on every change.
/// </summary>
public sealed class ProxyTable
{
    private readonly Dictionary<string, ProxyRoute> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProxyRoute> _wildcards = new(StringComparer.Ordinal);

    public static ProxyTable Empty { get; } = new([], []);

    public ProxyTable(IEnumerable<ProxyHostConfig> hosts, IEnumerable<ProxyRedirectConfig> redirects)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(redirects);
        var routes = hosts.Where(host => host.Enabled).Select(host => (ProxyRoute)new HostRoute(host))
            .Concat(redirects.Where(redirect => redirect.Enabled).Select(redirect => new RedirectRoute(redirect)));
        foreach (var route in routes)
        {
            foreach (var domain in route.Domains)
            {
                if (domain.StartsWith("*.", StringComparison.Ordinal))
                {
                    _wildcards.TryAdd(domain[2..], route);
                }
                else
                {
                    _exact.TryAdd(domain, route);
                }
            }
        }
    }

    /// <summary>The route of a host name (a <c>Host</c> header without its port, or a TLS server name), or null.</summary>
    public ProxyRoute? Resolve(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }
        var name = host.TrimEnd('.').ToLowerInvariant();
        if (_exact.TryGetValue(name, out var exact))
        {
            return exact;
        }
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && _wildcards.TryGetValue(name[(dot + 1)..], out var wildcard) ? wildcard : null;
    }

    /// <summary>
    /// The decision for a request on a proxy port. A proxy host forwards, unless it forces HTTPS on the plain port and
    /// HTTPS is there; a redirect host redirects; an unknown name goes on to the redirect to 7484 (and the plain port's
    /// <c>/api/install-id</c>) or, when the administrator chose so, gets 404, except <c>/api/install-id</c>.
    /// </summary>
    public static ProxyAction Decide(ProxyRoute? route, bool tls, bool tlsAvailable, UnknownHostAction unknown, PathString path)
    {
        switch (route)
        {
            case HostRoute { Host: var host }:
                return !tls && host.ForceHttps && tlsAvailable ? ProxyAction.RedirectToHttps : ProxyAction.Forward;
            case RedirectRoute:
                return ProxyAction.Redirect;
            default:
                if (unknown == UnknownHostAction.Redirect || (!tls && path.Equals("/api/install-id", StringComparison.OrdinalIgnoreCase)))
                {
                    return ProxyAction.Next;
                }
                return ProxyAction.NotFound;
        }
    }

    /// <summary>The location of a redirect host: its target, with the request's path and query appended when it keeps them.</summary>
    public static string RedirectLocation(ProxyRedirectConfig redirect, PathString path, QueryString query)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        if (!redirect.PreservePath)
        {
            return redirect.Target;
        }
        var target = new Uri(redirect.Target);
        var combined = target.AbsolutePath.TrimEnd('/') + (path.HasValue ? path.ToUriComponent() : "/");
        return target.GetLeftPart(UriPartial.Authority) + combined + (query.HasValue ? query.Value : target.Query);
    }
}
