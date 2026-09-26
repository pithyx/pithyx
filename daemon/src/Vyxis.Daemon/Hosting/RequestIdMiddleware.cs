namespace Vyxis.Daemon.Hosting;

/// <summary>Returns the request id to the client so it can be shown next to errors and found in the logs.</summary>
public sealed class RequestIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers[HeaderName] = Tracing.CurrentRequestId;
        return next(context);
    }
}
