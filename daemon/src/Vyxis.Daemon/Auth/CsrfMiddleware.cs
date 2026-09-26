using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Auth;

/// <summary>
/// State-changing API requests must carry <c>X-Vyxis-Request: 1</c>. Browsers only send custom headers cross-origin
/// after a CORS preflight, which the daemon never approves, so together with SameSite=Strict cookies this blocks CSRF.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Vyxis-Request";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method)
            && request.Headers[HeaderName] != "1")
        {
            throw new VyxisException(ErrorCodes.AuthCsrfHeaderMissing);
        }
        return next(context);
    }
}
