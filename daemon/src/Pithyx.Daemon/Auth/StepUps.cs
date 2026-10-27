using System.Collections.Concurrent;
using System.Security.Claims;
using Pithyx.Daemon.Audit;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Auth;

/// <summary>
/// The step-up before an administrator grants an elevated right of an app (D311): the password again and, with two-factor
/// sign-in on, a current authenticator code that was not used before. A confirmed step-up holds for five minutes in the
/// session that made it, kept in memory only, so a restart asks again.
/// </summary>
public sealed class StepUps(AuthService auth, LoginRateLimiter limiter, AuditLog audit, TimeProvider time)
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    /// <summary>Until when the caller's session holds a step-up, or null.</summary>
    public DateTimeOffset? Until(ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.FindFirstValue(SessionAuthenticationHandler.SessionIdClaim) is { } session
            && _until.TryGetValue(session, out var until) && until > time.GetUtcNow()
            ? until
            : null;
    }

    public bool Holds(ClaimsPrincipal caller) => Until(caller) is not null;

    /// <summary>
    /// Makes sure the caller's session holds a step-up: one made in the last five minutes, or a new one from the password
    /// and code given now. Without either it throws <c>auth.step_up.required</c>; wrong credentials count towards the
    /// sign-in limits and are recorded in the audit log.
    /// </summary>
    public async Task<DateTimeOffset> EnsureAsync(HttpContext context, string? password, string? totpCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(password) && Until(context.User) is { } held)
        {
            return held;
        }
        if (string.IsNullOrEmpty(password))
        {
            throw new PithyxException(ErrorCodes.AuthStepUpRequired);
        }
        var session = context.User.FindFirstValue(SessionAuthenticationHandler.SessionIdClaim) ?? throw new PithyxException(ErrorCodes.AuthSessionExpired);
        var username = context.User.Identity?.Name ?? "";
        limiter.EnsureAllowed(context, username);
        try
        {
            await AuthEndpoints.CountFailuresAsync(limiter, context, username, async () =>
            {
                await auth.ConfirmStepUpAsync(CurrentUser.Id(context.User), password, totpCode, cancellationToken);
                return true;
            });
        }
        catch (PithyxException exception) when (exception.Code is ErrorCodes.AuthCredentialsInvalid or ErrorCodes.AuthTotpInvalid)
        {
            await audit.RecordAsync(context.User, AuditLog.StepUpFailed, null, new Dictionary<string, string> { ["reason"] = exception.Code }, CancellationToken.None);
            throw;
        }
        var now = time.GetUtcNow();
        foreach (var (key, until) in _until)
        {
            if (until <= now)
            {
                _until.TryRemove(key, out _);
            }
        }
        var next = now + Validity;
        _until[session] = next;
        await audit.RecordAsync(context.User, AuditLog.StepUpConfirmed, null, null, cancellationToken);
        return next;
    }
}
