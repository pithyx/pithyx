using System.Diagnostics;

namespace Pithyx.Daemon.Hosting;

/// <summary>
/// Every request, WebSocket message, job and command runs inside an <see cref="Activity"/>. Its trace id is the
/// request id shown to users and written to every log entry.
/// </summary>
public static class Tracing
{
    public const string SourceName = "Pithyx.Daemon";

    private static readonly ActivityListener _listener = CreateListener();

    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>The current request id (W3C trace id, 32 hex characters), or an empty string outside a request.</summary>
    public static string CurrentRequestId => Activity.Current?.TraceId.ToHexString() ?? "";

    /// <summary>Makes ASP.NET Core and the daemon create activities even without an external collector.</summary>
    public static void EnsureActivities() => GC.KeepAlive(_listener);

    private static ActivityListener CreateListener()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is SourceName or "Microsoft.AspNetCore",
            Sample = (ref _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
