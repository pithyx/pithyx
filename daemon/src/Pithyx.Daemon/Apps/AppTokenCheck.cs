using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.Apps;

/// <summary>Who uses an app token or ticket, after <see cref="AppTokenCheck"/> found that it still holds.</summary>
/// <param name="Grant">What the token was issued for.</param>
/// <param name="User">The token's user; <see cref="UserInfo.Groups"/> is filled only when the check asked for it.</param>
/// <param name="Rights">The user's rights now.</param>
public sealed record AppCaller(AppTokenGrant Grant, UserInfo User, EffectivePermissions Rights);

/// <summary>
/// What every use of an app token or ticket checks again (D317), on <c>/api/app/**</c> and at the proxy: the session
/// still exists (sign-out, a revoked session, a deactivated user and a password reset all end it), the user may still
/// sign in to the desktop, and the app is still installed, enabled and granted to the user (administrators have every
/// app).
/// </summary>
public sealed class AppTokenCheck(AppTokens tokens, SessionService sessions, AuthService auth, AppRegistry registry)
{
    /// <summary>The caller while the grant still holds, or null; <paramref name="withGroups"/> also reads the user's groups.</summary>
    public async Task<AppCaller?> CheckAsync(AppTokenGrant grant, bool withGroups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (await sessions.TouchAsync(grant.SessionId, cancellationToken) is null)
        {
            tokens.RevokeSession(grant.SessionId);
            return null;
        }
        if (await auth.SignedInAsync(grant.UserId, withGroups, cancellationToken) is not var (user, rights) || !rights.Has(Resources.DesktopLogin))
        {
            return null;
        }
        return registry.Allows(rights, grant.AppId) ? new AppCaller(grant, user, rights) : null;
    }
}
