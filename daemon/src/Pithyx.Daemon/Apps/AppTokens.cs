using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps;

/// <summary>What a token is for (D303).</summary>
public enum AppTokenKind
{
    /// <summary>The daemon's API for apps, <c>/api/app/**</c>. Only the desktop holds it, never the app's frame.</summary>
    Host,

    /// <summary>The proxy to the app's backend (A13), as a bearer header or <c>?pithyx-token=</c>.</summary>
    Backend,

    /// <summary>One WebSocket to one endpoint of the backend, once, within 30 seconds (<c>?pithyx-ticket=</c>).</summary>
    Ticket,

    /// <summary>One file of a window (<c>files.url</c>), read through <c>GET /app-files</c> with <c>?pithyx-token=</c> and no session cookie.</summary>
    File,
}

/// <summary>The one file a file token reads.</summary>
/// <param name="Share">The shared folder's id, or null for a storage request.</param>
/// <param name="Storage">The id of a storage request, or null for a shared folder.</param>
/// <param name="Path">The file, relative to the location.</param>
public sealed record AppFileResource(string? Share, string? Storage, string Path);

/// <summary>Whom a token or ticket was issued to. Every use checks again that this still holds.</summary>
/// <param name="Kind">What it is for.</param>
/// <param name="UserId">The signed-in user.</param>
/// <param name="SessionId">The desktop session it came from (hex SHA-256 of the session token); it dies with it.</param>
/// <param name="AppId">The app; the only source of an app's identity on <c>/api/app/**</c> and at the proxy.</param>
/// <param name="Instance">The window instance the desktop asked for.</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
/// <param name="Endpoint">For a ticket: the backend endpoint it opens.</param>
/// <param name="Resource">For a file token: the one file it reads, as <see cref="AppFileResource"/> in JSON.</param>
public sealed record AppTokenGrant(AppTokenKind Kind, long UserId, string SessionId, string AppId, string Instance, DateTimeOffset ExpiresAt, string? Endpoint = null, string? Resource = null);

/// <summary>A token or ticket as it is handed out, once.</summary>
/// <param name="Token">256 random bits, URL-safe.</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
public sealed record IssuedAppToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// App tokens and WebSocket tickets (D303), in memory only: a restart ends them all, and the desktop asks again. Only
/// the SHA-256 of a token is kept, and a token is found by that hash, so neither memory nor the time a lookup takes says
/// anything about another token. Tokens live 5 minutes, tickets 30 seconds and only once. Whether the session, the
/// user, the grant, the app and its permissions still hold is checked on every use by the caller
/// (<see cref="AppTokenAuthenticationHandler"/>, the proxy); the revocations here only make a token go sooner.
/// </summary>
public sealed partial class AppTokens(TimeProvider time)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Live tokens and tickets one session may hold; a desktop renews well below it.</summary>
    public const int MaxPerSession = 512;

    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, AppTokenGrant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _perSession = new(StringComparer.Ordinal);
    private DateTimeOffset _swept = time.GetUtcNow();

    /// <summary>
    /// A window instance as the desktop names it: 1 to 64 letters, digits, dashes and underscores. It is not secret;
    /// it only binds a token to one window.
    /// </summary>
    public static bool IsInstance(string? instance) => instance is not null && InstancePattern().IsMatch(instance);

    /// <summary>A new host or backend token. Callers have checked the session, the user and the grant.</summary>
    public IssuedAppToken Issue(AppTokenKind kind, long userId, string sessionId, string appId, string instance)
    {
        if (kind is AppTokenKind.Ticket or AppTokenKind.File)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Tickets come from IssueTicket and file tokens from IssueFile.");
        }
        return Add(new AppTokenGrant(kind, userId, sessionId, appId, instance, time.GetUtcNow() + TokenLifetime));
    }

    /// <summary>A one-time ticket for one WebSocket to <paramref name="endpoint"/>, bound to what the host token is bound to.</summary>
    public IssuedAppToken IssueTicket(AppTokenGrant host, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Kind != AppTokenKind.Host)
        {
            throw new ArgumentException("Tickets are issued for a host token.", nameof(host));
        }
        return Add(host with { Kind = AppTokenKind.Ticket, ExpiresAt = time.GetUtcNow() + TicketLifetime, Endpoint = endpoint });
    }

    /// <summary>A token that reads one file for five minutes, bound to what the host token is bound to.</summary>
    public IssuedAppToken IssueFile(AppTokenGrant host, AppFileResource file)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Kind != AppTokenKind.Host)
        {
            throw new ArgumentException("File tokens are issued for a host token.", nameof(host));
        }
        return Add(host with { Kind = AppTokenKind.File, ExpiresAt = time.GetUtcNow() + TokenLifetime, Resource = JsonSerializer.Serialize(file) });
    }

    /// <summary>The grant of a token of the given kind while it lives, or null. A ticket is never found here.</summary>
    public AppTokenGrant? Validate(string? token, AppTokenKind kind)
    {
        if (kind == AppTokenKind.Ticket || Hash(token) is not { } hash || !_grants.TryGetValue(hash, out var grant) || grant.Kind != kind)
        {
            return null;
        }
        if (grant.ExpiresAt <= time.GetUtcNow())
        {
            Remove(hash);
            return null;
        }
        return grant;
    }

    /// <summary>The grant of a ticket, which is used up by this call: a second call finds nothing.</summary>
    public AppTokenGrant? RedeemTicket(string? ticket)
    {
        if (Hash(ticket) is not { } hash || !_grants.TryGetValue(hash, out var grant) || grant.Kind != AppTokenKind.Ticket)
        {
            return null;
        }
        // Only the caller whose removal succeeds may use it.
        if (!_grants.TryRemove(KeyValuePair.Create(hash, grant)))
        {
            return null;
        }
        Count(grant.SessionId, -1);
        return grant.ExpiresAt > time.GetUtcNow() ? grant : null;
    }

    /// <summary>Ends every token and ticket of a session, for example at sign-out.</summary>
    public void RevokeSession(string sessionId) => RemoveWhere(grant => grant.SessionId == sessionId);

    /// <summary>Ends every token and ticket of an app, for example when it is removed or disabled.</summary>
    public void RevokeApp(string appId) => RemoveWhere(grant => grant.AppId == appId);

    /// <summary>Live tokens and tickets, for tests and diagnostics.</summary>
    public int Count() => _grants.Count;

    private IssuedAppToken Add(AppTokenGrant grant)
    {
        SweepIfDue();
        if (Count(grant.SessionId, +1) > MaxPerSession)
        {
            Count(grant.SessionId, -1);
            throw new PithyxException(ErrorCodes.AuthRateLimited, new Dictionary<string, object?> { ["retryAfterSeconds"] = (int)_sweepInterval.TotalSeconds });
        }
        var token = Secrets.NewToken(32);
        _grants[Hash(token)!] = grant;
        return new IssuedAppToken(token, grant.ExpiresAt);
    }

    private void SweepIfDue()
    {
        var now = time.GetUtcNow();
        if (now - _swept < _sweepInterval)
        {
            return;
        }
        _swept = now;
        RemoveWhere(grant => grant.ExpiresAt <= now);
    }

    private void RemoveWhere(Func<AppTokenGrant, bool> predicate)
    {
        foreach (var (hash, grant) in _grants)
        {
            if (predicate(grant) && _grants.TryRemove(KeyValuePair.Create(hash, grant)))
            {
                Count(grant.SessionId, -1);
            }
        }
    }

    private void Remove(string hash)
    {
        if (_grants.TryRemove(hash, out var grant))
        {
            Count(grant.SessionId, -1);
        }
    }

    private int Count(string sessionId, int change)
    {
        var count = _perSession.AddOrUpdate(sessionId, change, (_, current) => current + change);
        if (count <= 0)
        {
            _perSession.TryRemove(KeyValuePair.Create(sessionId, count));
        }
        return count;
    }

    private static string? Hash(string? token) =>
        token is { Length: > 0 and <= 128 } ? Convert.ToHexString(Secrets.Sha256(token)) : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex InstancePattern();
}
