using System.Globalization;
using System.Security.Claims;
using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Auth;

public static class CurrentUser
{
    public static long Id(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new VyxisException(ErrorCodes.AuthSessionExpired);
        return long.Parse(value, CultureInfo.InvariantCulture);
    }
}
