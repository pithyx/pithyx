using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Auth;

/// <summary>Authenticates requests by the session cookie. Failures answer with problem details, never a redirect.</summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions,
    AuthService auth,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PithyxSession";
    public const string AdminRole = "admin";

    /// <summary>Hex SHA-256 of the session token, so WebSocket connections can be closed when the session ends.</summary>
    public const string SessionIdClaim = "pithyx:session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionService.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }
        var userId = await sessions.ValidateAsync(token, Context.RequestAborted);
        var user = userId is null ? null : (await auth.SignedInAsync(userId.Value, Context.RequestAborted))?.User;
        // Rights are resolved for every request, so a user who lost the desktop login right is out at the next one.
        if (user is null || !user.Permissions.Rights.Contains(Permissions.Resources.DesktopLogin))
        {
            return AuthenticateResult.Fail("Session expired");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(SessionIdClaim, SessionService.IdOf(token)),
                .. user.IsAdmin ? [new Claim(ClaimTypes.Role, AdminRole)] : Array.Empty<Claim>(),
                .. user.Permissions.Rights.Concat(user.Permissions.Protocols).Concat(user.Permissions.Apps.Select(Permissions.Resources.App)).Select(right => new Claim(Policies.RightClaim, right)),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>The same session's principal with or without the administrator role, after the user's rights changed.</summary>
    public static ClaimsPrincipal WithRole(ClaimsPrincipal user, bool admin)
    {
        ArgumentNullException.ThrowIfNull(user);
        var claims = user.Claims.Where(claim => claim.Type != ClaimTypes.Role).ToList();
        if (admin)
        {
            claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(ErrorCodes.AuthSessionExpired).AsTask();

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
