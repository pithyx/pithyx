using System.Globalization;
using Pithyx.Daemon.Hosting;
using Serilog.Core;
using Serilog.Events;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// Hands the daemon's own log events to the <see cref="LogStore"/> after the secret masking. Request logging at
/// information level stays in the log files only: one line per API call would bury everything else, and the Log Center
/// would log its own queries.
/// </summary>
public sealed class LogStoreSink(LogStore store) : ILogEventSink
{
    public const string RequestLogging = "Serilog.AspNetCore.RequestLoggingMiddleware";
    private const int MaxFields = 20;
    private const int MaxFieldLength = 1000;
    private const int MaxMessageLength = 4000;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var context = Scalar(logEvent, "SourceContext");
        if (context == RequestLogging && logEvent.Level < LogEventLevel.Warning)
        {
            return;
        }
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in logEvent.Properties)
        {
            if (data.Count >= MaxFields || name == RequestIdEnricher.PropertyName)
            {
                continue;
            }
            data[name] = Cut(value is ScalarValue { Value: string text } ? text : value.ToString(), MaxFieldLength);
        }
        if (logEvent.Exception is { } exception)
        {
            data["exception"] = Cut(exception.ToString(), MaxMessageLength);
        }
        store.Enqueue(new PendingLogEntry(
            logEvent.Timestamp.ToUniversalTime(),
            LevelOf(logEvent.Level),
            LogSources.Daemon,
            Cut(logEvent.RenderMessage(CultureInfo.InvariantCulture), MaxMessageLength),
            Scalar(logEvent, RequestIdEnricher.PropertyName),
            Data: data.Count == 0 ? null : data));
    }

    public static LogLevelName LevelOf(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose or LogEventLevel.Debug => LogLevelName.Debug,
        LogEventLevel.Information => LogLevelName.Info,
        LogEventLevel.Warning => LogLevelName.Warn,
        _ => LogLevelName.Error,
    };

    private static string? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string text } ? text : null;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
