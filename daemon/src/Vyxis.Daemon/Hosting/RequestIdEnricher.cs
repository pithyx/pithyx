using Serilog.Core;
using Serilog.Events;

namespace Vyxis.Daemon.Hosting;

/// <summary>
/// Sets <c>RequestId</c> to the trace id, the id users see. ASP.NET Core would otherwise log its connection-based
/// request identifier under the same name.
/// </summary>
public sealed class RequestIdEnricher : ILogEventEnricher
{
    public const string PropertyName = "RequestId";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var requestId = Tracing.CurrentRequestId;
        if (requestId.Length > 0)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(PropertyName, new ScalarValue(requestId)));
        }
        else
        {
            logEvent.RemovePropertyIfPresent(PropertyName);
        }
    }
}
