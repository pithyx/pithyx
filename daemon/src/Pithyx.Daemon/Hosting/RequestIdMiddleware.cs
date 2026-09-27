using System.Diagnostics;

namespace Pithyx.Daemon.Hosting;

/// <summary>
/// Returns the request id to the client so it can be shown next to errors and found in the logs, and the
/// <see cref="ServerTiming"/> of the request.
/// </summary>
public sealed class RequestIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers[HeaderName] = Tracing.CurrentRequestId;
        var activity = Activity.Current;
        context.Response.OnStarting(() =>
        {
            if (ServerTiming.HeaderFor(activity) is { } timing)
            {
                context.Response.Headers["Server-Timing"] = timing;
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}
