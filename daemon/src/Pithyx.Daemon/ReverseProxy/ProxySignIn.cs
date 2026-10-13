using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>
/// The Pithyx sign-in in front of a proxy host (D347). A browser without the host's cookie is sent to the box's
/// sign-in on 7484 (or the sign-in address) with <c>?pithyx-continue=</c>; once signed in there, the desktop asks for
/// a one-time ticket (30 seconds, bound to the box session and the host) and goes to
/// <c>/.pithyx/auth?ticket=</c> on the host, which sets the host's own cookie and sends the browser on to where it
/// wanted to go. The cookie holds a random token, the database only its SHA-256 and the box session it belongs to,
/// so signing out of the box, a revoked session or a deleted host ends it. Every request checks the session, the
/// desktop sign-in right and the host's users and groups again (administrators always pass).
/// </summary>
public sealed class ProxySignIn(SqliteConnectionFactory connections, SessionService sessions, AuthService auth, WebTransport transport, TimeProvider time)
{
    public const string CookieName = "pithyx_proxy";
    public const string AuthPath = "/.pithyx/auth";
    public const string ContinueParameter = "pithyx-continue";
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    private sealed record Ticket(string SessionId, long HostId, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="rule"/> lets <paramref name="user"/> in: administrators, listed users and members of listed groups; everybody when nobody is listed.</summary>
    public static bool Allows(ProxySignInRule rule, UserInfo user)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(user);
        if (user.IsAdmin || (rule.Users.Count == 0 && rule.Groups.Count == 0))
        {
            return true;
        }
        return rule.Users.Contains(user.Username, StringComparer.Ordinal)
            || user.Groups.Any(group => rule.Groups.Contains(group.Name, StringComparer.Ordinal));
    }

    /// <summary>
    /// A ticket into the proxy host at <paramref name="request"/>'s address for the signed-in user. Throws
    /// <c>proxy.ticket.target_invalid</c> when no enabled host with the Pithyx sign-in answers there and
    /// <c>proxy.access.denied</c> when the user is not let in.
    /// </summary>
    public async Task<ProxyTicket> IssueAsync(ProxyTicketRequest request, long userId, string sessionId, ProxyState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https")
            || url.UserInfo.Length > 0
            || state.Table.Resolve(url.Host) is not HostRoute { Host: var host }
            || !host.SignIn.Required)
        {
            throw new PithyxException(ErrorCodes.ProxyTicketTargetInvalid);
        }
        if (await auth.SignedInAsync(userId, withGroups: true, cancellationToken) is not var (user, rights)
            || !rights.Has(Resources.DesktopLogin)
            || !Allows(host.SignIn, user))
        {
            throw new PithyxException(ErrorCodes.ProxyAccessDenied, new Dictionary<string, object?> { ["host"] = url.Host });
        }
        var now = time.GetUtcNow();
        foreach (var (key, expired) in _tickets.Where(pair => pair.Value.ExpiresAt <= now).ToList())
        {
            _tickets.TryRemove(new KeyValuePair<string, Ticket>(key, expired));
        }
        var ticket = Secrets.NewToken();
        _tickets[ticket] = new Ticket(sessionId, host.Id, now + TicketLifetime);
        var to = Uri.EscapeDataString(url.PathAndQuery);
        return new ProxyTicket($"{url.GetLeftPart(UriPartial.Authority)}{AuthPath}?ticket={Uri.EscapeDataString(ticket)}&to={to}");
    }

    /// <summary>
    /// Answers <c>/.pithyx/auth</c> on a host: redeems the ticket, sets the host's cookie and sends the browser to the
    /// path it wanted. A missing, used, expired or foreign ticket gets <c>proxy.auth.required</c>.
    /// </summary>
    public async Task<bool> CompleteAsync(HttpContext context, ProxyHostConfig host)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        var request = context.Request;
        if (!_tickets.TryRemove(request.Query["ticket"].ToString(), out var ticket)
            || ticket.HostId != host.Id
            || ticket.ExpiresAt <= time.GetUtcNow()
            || await sessions.TouchAsync(ticket.SessionId, context.RequestAborted) is not { } sessionEnds)
        {
            return false;
        }
        var token = Secrets.NewToken();
        await using (var connection = await connections.OpenAsync(context.RequestAborted))
        {
            await connection.ExecuteAsync(
                "INSERT INTO proxy_sessions (id, session_id, host_id, created_at) VALUES (@id, @sessionId, @hostId, @now)",
                new
                {
                    id = Secrets.Sha256(token),
                    sessionId = Convert.FromHexString(ticket.SessionId),
                    hostId = host.Id,
                    now = time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                });
        }
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = request.IsHttps,
            // Lax: the browser comes back from the box with a top-level navigation.
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = sessionEnds,
            IsEssential = true,
        });
        context.Response.Redirect(SafePath(request.Query["to"]), permanent: false);
        return true;
    }

    /// <summary>The signed-in user of a request to <paramref name="host"/>, or null when the browser has to sign in (again).</summary>
    public async Task<UserInfo?> CheckAsync(HttpContext context, ProxyHostConfig host)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return null;
        }
        var cancellationToken = context.RequestAborted;
        var id = Secrets.Sha256(token);
        (byte[] SessionId, long UserId)? row;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            row = await connection.QuerySingleOrDefaultAsync<(byte[], long)?>(
                "SELECT p.session_id, s.user_id FROM proxy_sessions p JOIN sessions s ON s.id = p.session_id WHERE p.id = @id AND p.host_id = @hostId",
                new { id, hostId = host.Id });
        }
        if (row is not var (sessionId, userId)
            || await sessions.TouchAsync(Convert.ToHexString(sessionId), cancellationToken) is null
            || await auth.SignedInAsync(userId, withGroups: true, cancellationToken) is not var (user, rights)
            || !rights.Has(Resources.DesktopLogin)
            || !Allows(host.SignIn, user))
        {
            await using var connection = await connections.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("DELETE FROM proxy_sessions WHERE id = @id", new { id });
            return null;
        }
        return user;
    }

    /// <summary>
    /// Where a browser signs in for <paramref name="request"/>: the sign-in address, or the requested name on the
    /// box's web port, with the address it wanted as <c>?pithyx-continue=</c>.
    /// </summary>
    public string SignInUrl(HttpRequest request, string? signInAddress)
    {
        ArgumentNullException.ThrowIfNull(request);
        var origin = signInAddress ?? TransportRedirect.Origin(request, transport.Https ? "https" : "http", transport.WebPort);
        var wanted = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
        return $"{origin}/?{ContinueParameter}={Uri.EscapeDataString(wanted)}";
    }

    /// <summary>A path on the same host to go back to; anything else (another host, a scheme) becomes <c>/</c>.</summary>
    public static string SafePath(string? to) =>
        to is { Length: > 0 } && to[0] == '/' && !to.StartsWith("//", StringComparison.Ordinal) && !to.StartsWith("/\\", StringComparison.Ordinal)
            && !to.Any(character => character is '\r' or '\n' or '\0')
            ? to
            : "/";
}
