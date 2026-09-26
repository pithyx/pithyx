using System.Globalization;
using Dapper;
using Vyxis.Daemon.Database;

namespace Vyxis.Daemon.Auth;

/// <summary>
/// Browser sessions. The cookie holds a random token; the database stores only its SHA-256. Sessions expire after
/// seven days without use and thirty days after login at the latest.
/// </summary>
public sealed class SessionService(SqliteConnectionFactory connections, TimeProvider time)
{
    public const string CookieName = "vyxis_session";
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan _touchInterval = TimeSpan.FromMinutes(1);

    public sealed record Session(string Token, DateTimeOffset AbsoluteExpiresAt);

    public async Task<Session> CreateAsync(long userId, string? userAgent, CancellationToken cancellationToken)
    {
        var token = Secrets.NewToken();
        var now = time.GetUtcNow();
        var absolute = now + AbsoluteLifetime;
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO sessions (id, user_id, created_at, last_seen_at, expires_at, absolute_expires_at, user_agent)
            VALUES (@Id, @UserId, @Now, @Now, @ExpiresAt, @AbsoluteExpiresAt, @UserAgent)
            """,
            new
            {
                Id = Secrets.Sha256(token),
                UserId = userId,
                Now = Format(now),
                ExpiresAt = Format(now + IdleLifetime),
                AbsoluteExpiresAt = Format(absolute),
                UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent,
            });
        return new Session(token, absolute);
    }

    /// <summary>Returns the user id of a valid session and extends its idle expiry.</summary>
    public async Task<long?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        var id = Secrets.Sha256(token);
        var now = time.GetUtcNow();
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(
            "SELECT user_id AS UserId, last_seen_at AS LastSeenAt, expires_at AS ExpiresAt, absolute_expires_at AS AbsoluteExpiresAt FROM sessions WHERE id = @id",
            new { id });
        if (row is null)
        {
            return null;
        }
        if (Parse(row.ExpiresAt) <= now || Parse(row.AbsoluteExpiresAt) <= now)
        {
            await connection.ExecuteAsync("DELETE FROM sessions WHERE id = @id", new { id });
            return null;
        }
        if (now - Parse(row.LastSeenAt) >= _touchInterval)
        {
            var expiresAt = Min(now + IdleLifetime, Parse(row.AbsoluteExpiresAt));
            await connection.ExecuteAsync(
                "UPDATE sessions SET last_seen_at = @Now, expires_at = @ExpiresAt WHERE id = @Id",
                new { Id = id, Now = Format(now), ExpiresAt = Format(expiresAt) });
        }
        return row.UserId;
    }

    public async Task DeleteAsync(string token, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM sessions WHERE id = @Id", new { Id = Secrets.Sha256(token) });
    }

    public static CookieOptions CookieOptions(bool secure, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };

    /// <summary>The public id of a session: hex SHA-256 of its token.</summary>
    public static string IdOf(string token) => Convert.ToHexString(Secrets.Sha256(token));

    internal static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private sealed record SessionRow(long UserId, string LastSeenAt, string ExpiresAt, string AbsoluteExpiresAt);
}
