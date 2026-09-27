using System.Text.RegularExpressions;
using Pithyx.Daemon.Auth;

namespace Pithyx.Daemon.Layouts;

/// <summary>
/// Identifies a browser profile for per-device desktop layouts: a random id in a long-lived HttpOnly cookie. It is not a
/// credential and says nothing about the user; layouts are keyed by user and device.
/// </summary>
public static partial class DeviceCookie
{
    public const string CookieName = "pithyx_device";

    // Browsers cap cookie lifetimes at about 400 days; every session start renews it.
    private static readonly TimeSpan _lifetime = TimeSpan.FromDays(400);

    /// <summary>The device id from the request, if it has a well-formed one.</summary>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.TryGetValue(CookieName, out var value) && value is not null && IdFormat().IsMatch(value) ? value : null;
    }

    /// <summary>
    /// Returns the device id and (re)sets the cookie, creating a new id if the request has none. The cookie is
    /// <c>Secure</c> on HTTPS; plain HTTP exists only in development until TLS lands.
    /// </summary>
    public static string Ensure(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = Read(context.Request) ?? Secrets.NewToken(16);
        context.Response.Cookies.Append(CookieName, id, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = _lifetime,
            IsEssential = true,
        });
        return id;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex IdFormat();
}
