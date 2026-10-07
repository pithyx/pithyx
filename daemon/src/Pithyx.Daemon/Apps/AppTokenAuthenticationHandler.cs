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
/// never enough there. Every request checks again what the token was issued for (<see cref="AppTokenCheck"/>). The principal is
/// the user, as the session handler builds it, plus the app; the app's identity comes from the token alone.
/// </summary>
public sealed class AppTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppTokens tokens,
    AppTokenCheck check,
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
        if (await check.CheckAsync(grant, withGroups: false, Context.RequestAborted) is not { User: var user })
        {
            return AuthenticateResult.Fail("Session, user or app no longer valid");
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
