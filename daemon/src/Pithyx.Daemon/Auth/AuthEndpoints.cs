using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Layouts;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.SystemSettings;

namespace Pithyx.Daemon.Auth;

public static class AuthEndpoints
{
    public static IServiceCollection AddPithyxAuth(this IServiceCollection services)
    {
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<LoginChallenges>();
        services.AddSingleton<LoginRateLimiter>();
        services.AddSingleton<AuthService>();
        services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
        services.AddAuthorization(Policies.Add);
        return services;
    }

    /// <param name="api">The <c>/api</c> group, which requires a session unless an endpoint allows anonymous access.</param>
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var setup = api.MapGroup("/setup").WithTags("Setup").AllowAnonymous();
        setup.MapGet("", GetSetupStatusAsync).WithName("GetSetupStatus");
        setup.MapPost("", SetupAsync).WithName("Setup");

        var auth = api.MapGroup("/auth").WithTags("Auth");
        auth.MapPost("/login", LoginAsync).WithName("Login").AllowAnonymous();
        auth.MapPost("/login/totp", CompleteTotpLoginAsync).WithName("LoginTotp").AllowAnonymous();
        auth.MapPost("/recover", RecoverAsync).WithName("Recover").AllowAnonymous();
        auth.MapPost("/logout", LogoutAsync).WithName("Logout").AllowAnonymous();
        auth.MapGet("/session", GetSessionAsync).WithName("GetSession");
        auth.MapPost("/totp/enroll", EnrollTotpAsync).WithName("EnrollTotp");
        auth.MapPost("/totp/confirm", ConfirmTotpAsync).WithName("ConfirmTotp");
        auth.MapPost("/totp/disable", DisableTotpAsync).WithName("DisableTotp");
    }

    private static async Task<Ok<SetupStatus>> GetSetupStatusAsync(AuthService auth, SystemSettingsService system, CancellationToken cancellationToken)
    {
        var region = await system.RegionAsync(cancellationToken);
        return TypedResults.Ok(new SetupStatus(await auth.SetupRequiredAsync(cancellationToken), Environment.MachineName, region.Language, region.Region));
    }

    private static async Task<Ok<SetupResponse>> SetupAsync(
        SetupRequest request,
        AuthService auth,
        SessionService sessions,
        Users.LinuxAccounts accounts,
        FileServices.FileServicePasswords passwords,
        FileServices.FileServiceChanges fileServices,
        HttpContext context,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var (user, recoveryKey) = await auth.SetupAsync(request, cancellationToken);
        // The administrator's Linux account; a refusal (a system account with the name) is logged, setup goes on.
        await accounts.ReconcileAsync(cancellationToken);
        await passwords.SetAsync(user.Username, request.Password, cancellationToken);
        fileServices.Announce();
        await StartSessionAsync(context, sessions, environment, user.Id, cancellationToken);
        return TypedResults.Ok(new SetupResponse(user, recoveryKey));
    }

    private static async Task<Ok<LoginResponse>> LoginAsync(
        LoginRequest request,
        AuthService auth,
        SessionService sessions,
        LoginRateLimiter limiter,
        FileServices.FileServicePasswords passwords,
        HttpContext context,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        limiter.EnsureAllowed(context, request.Username);
        var outcome = await CountFailuresAsync(limiter, context, request.Username, () => auth.LoginAsync(request, Address(context), cancellationToken));
        // The password is right: SMB and SFTP learn it if they do not know it yet (users from before them, a lost passdb).
        passwords.SetIfMissing(CredentialRules.NormalizeUsername(request.Username), request.Password);
        if (outcome.User is null)
        {
            return TypedResults.Ok(new LoginResponse(null, TotpRequired: true, outcome.Challenge));
        }
        await StartSessionAsync(context, sessions, environment, outcome.User.Id, cancellationToken);
        return TypedResults.Ok(new LoginResponse(outcome.User, TotpRequired: false, null));
    }

    private static async Task<Ok<UserInfo>> CompleteTotpLoginAsync(
        TotpLoginRequest request, AuthService auth, SessionService sessions, LoginRateLimiter limiter, HttpContext context, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        var username = auth.ChallengeUsername(request.Challenge, Address(context)) ?? "";
        limiter.EnsureAllowed(context, username);
        var user = await CountFailuresAsync(limiter, context, username, () => auth.CompleteTotpLoginAsync(request, Address(context), cancellationToken));
        await StartSessionAsync(context, sessions, environment, user.Id, cancellationToken);
        return TypedResults.Ok(user);
    }

    private static async Task<Ok<RecoverResponse>> RecoverAsync(
        RecoverRequest request,
        AuthService auth,
        LoginRateLimiter limiter,
        TopicHub hub,
        FileServices.FileServicePasswords passwords,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        limiter.EnsureAllowed(context, request.Username);
        var (userId, recoveryKey) = await CountFailuresAsync(limiter, context, request.Username, () => auth.RecoverAsync(request, cancellationToken));
        await passwords.SetAsync(CredentialRules.NormalizeUsername(request.Username), request.NewPassword, cancellationToken);
        hub.CloseUser(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return TypedResults.Ok(new RecoverResponse(recoveryKey));
    }

    private static async Task<NoContent> LogoutAsync(SessionService sessions, TopicHub hub, HttpContext context, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        if (context.Request.Cookies.TryGetValue(SessionService.CookieName, out var token) && !string.IsNullOrEmpty(token))
        {
            await sessions.DeleteAsync(token, cancellationToken);
            hub.CloseSession(SessionService.IdOf(token));
        }
        context.Response.Cookies.Delete(SessionService.CookieName, SessionService.CookieOptions(Secure(environment), null));
        return TypedResults.NoContent();
    }

    private static async Task<Ok<UserInfo>> GetSessionAsync(AuthService auth, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await auth.GetUserAsync(CurrentUser.Id(context.User), cancellationToken)
            ?? throw new PithyxException(ErrorCodes.AuthSessionExpired));

    private static async Task<Ok<TotpEnrollment>> EnrollTotpAsync(AuthService auth, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await auth.EnrollTotpAsync(CurrentUser.Id(context.User), Environment.MachineName, cancellationToken));

    private static async Task<Ok<RecoveryCodes>> ConfirmTotpAsync(TotpConfirmRequest request, AuthService auth, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(new RecoveryCodes(await auth.ConfirmTotpAsync(CurrentUser.Id(context.User), request.Code, cancellationToken)));

    private static async Task<NoContent> DisableTotpAsync(TotpDisableRequest request, AuthService auth, HttpContext context, CancellationToken cancellationToken)
    {
        await auth.DisableTotpAsync(CurrentUser.Id(context.User), request.Password, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<T> CountFailuresAsync<T>(LoginRateLimiter limiter, HttpContext context, string username, Func<Task<T>> attempt)
    {
        try
        {
            return await attempt();
        }
        catch (PithyxException exception) when (exception.Code is ErrorCodes.AuthCredentialsInvalid or ErrorCodes.AuthTotpInvalid or ErrorCodes.AuthRecoveryInvalid)
        {
            limiter.RecordFailure(context, username);
            throw;
        }
    }

    private static async Task StartSessionAsync(HttpContext context, SessionService sessions, IHostEnvironment environment, long userId, CancellationToken cancellationToken)
    {
        var session = await sessions.CreateAsync(userId, context.Request.Headers.UserAgent.ToString(), cancellationToken);
        context.Response.Cookies.Append(SessionService.CookieName, session.Token, SessionService.CookieOptions(Secure(environment), session.AbsoluteExpiresAt));
        // The desktop saves this browser's layout under its device id.
        DeviceCookie.Ensure(context);
    }

    // Development runs over plain HTTP, where browsers drop Secure cookies. HTTPS for every other environment is M5.
    private static bool Secure(IHostEnvironment environment) => !environment.IsDevelopment();

    private static string Address(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
