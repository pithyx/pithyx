using Microsoft.AspNetCore.Authorization;

namespace Pithyx.Daemon.Auth;

/// <summary>
/// The authorization policies of the API (D290). Every endpoint that needs a right carries its policy, so a request
/// without it is refused with <c>auth.permission.denied</c> before anything else runs; the endpoint matrix test checks
/// that every endpoint carries the policy it should.
/// </summary>
public static class Policies
{
    /// <summary>Administrators only.</summary>
    public const string Admin = "admin";

    /// <summary>The right to restart and shut down the box; administrators have it anyway.</summary>
    public const string Power = "power";

    /// <summary>
    /// The daemon's API for apps (<c>/api/app/**</c>): a host token, never the session cookie (D303). The policy names
    /// only the app token scheme, so a cookie on the same request is not even read.
    /// </summary>
    public const string AppToken = "appToken";

    /// <summary>One claim per special right, protocol and app the user has, set for every request.</summary>
    public const string RightClaim = "pithyx:right";

    public static void Add(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(Admin, policy => policy.RequireRole(SessionAuthenticationHandler.AdminRole));
        options.AddPolicy(AppToken, policy => policy
            .AddAuthenticationSchemes(Apps.AppTokenAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim(Apps.AppTokenAuthenticationHandler.AppClaim));
        options.AddPolicy(Power, policy => policy.RequireAssertion(context =>
            context.User.IsInRole(SessionAuthenticationHandler.AdminRole) || context.User.HasClaim(RightClaim, Permissions.Resources.Power)));
    }
}
