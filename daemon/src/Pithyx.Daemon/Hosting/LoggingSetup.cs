using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Pithyx.Daemon.Hosting;

public static class LoggingSetup
{
    private const long MaxLogFileBytes = 20 * 1024 * 1024;
    private const int RetainedLogFiles = 10;

    /// <summary>Structured JSON logs to stdout (journald in production) and a size-capped rotating file.</summary>
    public static void AddPithyxLogging(this WebApplicationBuilder builder, string logDirectory)
    {
        var formatter = new RenderedCompactJsonFormatter();
        builder.Services.AddSerilog(
            configuration => configuration
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .Enrich.With<RequestIdEnricher>()
                .Enrich.With<SecretMaskingEnricher>()
                .WriteTo.Console(formatter)
                .WriteTo.File(
                    formatter,
                    Path.Combine(logDirectory, "daemon.json"),
                    fileSizeLimitBytes: MaxLogFileBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedLogFiles),
            preserveStaticLogger: true);
    }
}
