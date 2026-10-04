namespace Pithyx.Daemon.Hosting;

/// <summary>
/// Port 80 sends every request to the same address on 7484 (CLAUDE.md, Network; 443 once TLS lands). Only
/// <c>/api/install-id</c> is answered on port 80 itself: the installer page finds the rebooted box with it, and a
/// redirect would turn its cross-origin request into a failure.
/// </summary>
public sealed class PlainHttpRedirect(RequestDelegate next, int plainPort, int targetPort)
{
    public const int Port = 80;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Connection.LocalPort != plainPort || context.Request.Path.Equals("/api/install-id", StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }
        context.Response.Redirect(Target(context.Request, targetPort), permanent: false, preserveMethod: true);
        return Task.CompletedTask;
    }

    /// <summary>The same URL on <paramref name="port"/>; an IPv6 address keeps its brackets.</summary>
    public static string Target(HttpRequest request, int port)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host.HasValue ? request.Host.Host : request.HttpContext.Connection.LocalIpAddress?.ToString() ?? "localhost";
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }
        return $"http://{host}:{port}{request.PathBase}{request.Path}{request.QueryString}";
    }
}
