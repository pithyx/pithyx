using System.Globalization;
using System.Security.Claims;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Auth;

public static class CurrentUser
{
    public static long Id(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new PithyxException(ErrorCodes.AuthSessionExpired);
        return long.Parse(value, CultureInfo.InvariantCulture);
    }
}
