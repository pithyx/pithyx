using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// Authenticates <c>/api/app/**</c> by a host token in <c>Authorization: Bearer</c> (D303); a session cookie alone is
/// never enough there. Every request checks again what the token was issued for: the session still exists (sign-out,
/// a revoked session, a deactivated user and a password reset all end it), the user may still sign in to the desktop,
/// and the app is still installed, enabled and granted to the user (administrators have every app). The principal is
/// the user, as the session handler builds it, plus the app; the app's identity comes from the token alone.
/// </summary>
public sealed class AppTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppTokens tokens,
    SessionService sessions,
    AuthService auth,
    PermissionService permissions,
    AppRegistry registry,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PithyxAppToken";

    /// <summary>The app the host token was issued for.</summary>
    public const string AppClaim = "pithyx:app";

    /// <summary>The window instance the host token was issued for.</summary>
    public const string InstanceClaim = "pithyx:instance";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers[HeaderNames.Authorization].ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }
        if (tokens.Validate(header["Bearer ".Length..].Trim(), AppTokenKind.Host) is not { } grant)
        {
            return AuthenticateResult.Fail("Unknown or expired app token");
        }
        if (await sessions.TouchAsync(grant.SessionId, Context.RequestAborted) is null)
        {
            tokens.RevokeSession(grant.SessionId);
            return AuthenticateResult.Fail("Session ended");
        }
        var user = await auth.GetUserAsync(grant.UserId, Context.RequestAborted);
        if (user is null || !user.Permissions.Rights.Contains(Resources.DesktopLogin))
        {
            return AuthenticateResult.Fail("User may no longer sign in");
        }
        if (!registry.Allows(await permissions.ForUserAsync(grant.UserId, Context.RequestAborted), grant.AppId))
        {
            return AuthenticateResult.Fail("App no longer available to the user");
        }
        Context.Items[typeof(AppTokenGrant)] = grant;
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(SessionAuthenticationHandler.SessionIdClaim, grant.SessionId),
                new Claim(AppClaim, grant.AppId),
                new Claim(InstanceClaim, grant.Instance),
                .. user.IsAdmin ? [new Claim(ClaimTypes.Role, SessionAuthenticationHandler.AdminRole)] : Array.Empty<Claim>(),
                .. user.Permissions.Rights.Concat(user.Permissions.Protocols).Concat(user.Permissions.Apps.Select(Resources.App)).Select(right => new Claim(Policies.RightClaim, right)),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>The grant of the host token that authenticated the request.</summary>
    public static AppTokenGrant GrantOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(typeof(AppTokenGrant), out var grant) && grant is AppTokenGrant found
            ? found
            : throw new PithyxException(ErrorCodes.AppTokenInvalid);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(ErrorCodes.AppTokenInvalid).AsTask();

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(ErrorCodes.AuthPermissionDenied).AsTask();

    private ValueTask<bool> WriteProblemAsync(string code)
    {
        var status = (int)ErrorCodes.StatusOf(code);
        Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status };
        problem.Extensions[ErrorHandling.CodeKey] = code;
        return problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = Context, ProblemDetails = problem });
    }
}
