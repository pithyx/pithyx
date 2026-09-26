using Serilog;
using Serilog.Core;
using Serilog.Events;
using Vyxis.Daemon.Hosting;

namespace Vyxis.Daemon.Tests.Hosting;

public sealed class SecretMaskingEnricherTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static LogEvent Log(Action<ILogger> write)
    {
        var sink = new CollectingSink();
        using (var logger = new LoggerConfiguration().Enrich.With<SecretMaskingEnricher>().WriteTo.Sink(sink).CreateLogger())
        {
            write(logger);
        }
        return Assert.Single(sink.Events);
    }

    [Fact]
    public void MasksSensitiveTopLevelProperties()
    {
        var logEvent = Log(logger => logger.Information("Login {Username} with {Password}", "admin", "hunter2"));

        Assert.Equal("\"admin\"", logEvent.Properties["Username"].ToString());
        Assert.Equal("\"***\"", logEvent.Properties["Password"].ToString());
        Assert.DoesNotContain("hunter2", logEvent.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void MasksNestedPropertiesOfDestructuredObjects()
    {
        var logEvent = Log(logger => logger.Information("Request {@Body}", new { Username = "admin", SessionToken = "abc", Items = new[] { new { ApiSecret = "x" } } }));

        var rendered = logEvent.Properties["Body"].ToString();
        Assert.Contains("admin", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksDictionaryKeys()
    {
        var logEvent = Log(logger => logger.Information("Headers {Headers}", new Dictionary<string, string> { ["Cookie"] = "vyxis_session=abc", ["Accept"] = "json" }));

        var rendered = logEvent.Properties["Headers"].ToString();
        Assert.DoesNotContain("abc", rendered, StringComparison.Ordinal);
        Assert.Contains("json", rendered, StringComparison.Ordinal);
    }
}
