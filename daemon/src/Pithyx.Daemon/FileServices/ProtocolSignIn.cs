using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.FileServices;

/// <summary>
/// Signs in the clients of WebDAV and FTP with a Pithyx user name and password (D291). The password is checked against
/// the user's hash like a desktop sign-in, with the same rate limit per address and user; a check that succeeded is
/// remembered for five minutes, since WebDAV sends the password with every request. The user must be active and have
/// the protocol's right; the right is checked again for every request by the file service and the resolver. Users
/// with two-factor sign-in use their password here, as over SMB and SFTP.
/// </summary>
public sealed class ProtocolSignIn(SqliteConnectionFactory connections, PasswordHasher hasher, PermissionService permissions, LoginRateLimiter limiter, TimeProvider time)
{
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _verified = new(StringComparer.Ordinal);

    /// <summary>
    /// The user as a principal for the file service, or null when the name, the password, the account or the right does
    /// not fit. Throws <c>auth.rate.limited</c> after too many failures.
    /// </summary>
    /// <param name="username">The user name as typed.</param>
    /// <param name="password">The password.</param>
    /// <param name="protocol"><c>protocol:webdav</c> or <c>protocol:ftp</c>.</param>
    /// <param name="address">The client's address, for the rate limit.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<ClaimsPrincipal?> SignInAsync(string username, string password, string protocol, string address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        var name = username.Trim().ToLowerInvariant();
        limiter.EnsureAllowed(address, name);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var user = await connection.QuerySingleOrDefaultAsync<(long Id, string Username, string PasswordHash, string? DisabledAt)?>(
            "SELECT id, username, password_hash, disabled_at FROM users WHERE username = @name",
            new { name });
        if (user is not { } found)
        {
            await hasher.WasteTimeAsync(password, cancellationToken);
            limiter.RecordFailure(address, name);
            return null;
        }
        // The hash is part of the key, so a new password ends what was remembered for the old one.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{found.Username}\n{found.PasswordHash}\n{password}")));
        var now = time.GetUtcNow();
        if (!(_verified.TryGetValue(key, out var until) && until > now))
        {
            if (!(await hasher.VerifyAsync(password, found.PasswordHash, cancellationToken)).Valid)
            {
                limiter.RecordFailure(address, name);
                return null;
            }
            foreach (var (stale, expiry) in _verified)
            {
                if (expiry <= now)
                {
                    _verified.TryRemove(stale, out _);
                }
            }
            _verified[key] = now + Remember;
        }
        if (found.DisabledAt is not null || !(await permissions.ForUserAsync(found.Id, cancellationToken)).Has(protocol))
        {
            return null;
        }
        return Principal(found.Id, found.Username, (await permissions.ForUserAsync(found.Id, cancellationToken)).IsAdmin);
    }

    /// <summary>A principal like a desktop session's, so the file service checks the same rights.</summary>
    public static ClaimsPrincipal Principal(long userId, string username, bool admin) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, username),
                .. admin ? [new Claim(ClaimTypes.Role, SessionAuthenticationHandler.AdminRole)] : Array.Empty<Claim>(),
            ],
            "PithyxProtocol"));

    /// <summary>
    /// The user of a long session with its current rights, or null when it may no longer use the protocol (its right or
    /// its account went). FTP asks before every command.
    /// </summary>
    public ClaimsPrincipal? Refresh(ClaimsPrincipal user, string protocol)
    {
        ArgumentNullException.ThrowIfNull(user);
        var rights = permissions.ForUser(user);
        return rights.Has(protocol)
            ? Principal(Auth.CurrentUser.Id(user), user.FindFirstValue(ClaimTypes.Name) ?? "", rights.IsAdmin)
            : null;
    }

}
