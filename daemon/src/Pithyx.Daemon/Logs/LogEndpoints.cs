using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Serilog.Core;

namespace Pithyx.Daemon.Logs;

/// <summary>The Log Center (administrators only) and the SDK's logging API for apps.</summary>
public static class LogEndpoints
{
    public static IServiceCollection AddPithyxLogs(this IServiceCollection services, string logDirectory)
    {
        services.AddSingleton(new LogStore(logDirectory));
        services.AddSingleton<ILogEventSink>(provider => new LogStoreSink(provider.GetRequiredService<LogStore>()));
        services.AddSingleton<LogArchive>();
        services.AddSingleton<AppLogLimiter>();
        services.AddSingleton<LogService>();
        if (!BuildTimeDocument.IsGenerating)
        {
            services.AddHostedService<LogWriter>();
        }
        return services;
    }

    public static void MapLogEndpoints(this RouteGroupBuilder api)
    {
        var logs = api.MapGroup("/logs").WithTags("Logs");
        logs.MapGet("", async (
                [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to,
                [FromQuery] string[]? level,
                [FromQuery] string[]? source,
                [FromQuery] string? search,
                [FromQuery] string? cursor,
                [FromQuery] int? limit,
                LogService service,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await service.QueryAsync(Query(from, to, level, source, search, cursor, limit ?? LogService.DefaultLimit), cancellationToken));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("GetLogs")
            .WithDescription("Administrators only. Log entries of the daemon, the system services and the apps, newest first, from the current logs and the archive together. Filters: time range (from, to), level (repeat for several: debug, info, warn, error), source (repeat for several), search (text in the message or its fields, or an exact request id). Pass nextCursor as cursor for older entries; limit is 1 to 1000 (default 200).");
        logs.MapGet("/sources", async (LogService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await service.SourcesAsync(cancellationToken));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("GetLogSources")
            .WithDescription("Administrators only. Every source with entries in the current logs or the archive: daemon, system/<service>, app/<id>.");
        logs.MapGet("/export", async (
                [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to,
                [FromQuery] string[]? level,
                [FromQuery] string[]? source,
                [FromQuery] string? search,
                [FromQuery] string? format,
                LogService service,
                TimeProvider time,
                HttpContext context) =>
            {
                RequireAdmin(context.User);
                var query = Query(from, to, level, source, search, null, LogService.MaxLimit);
                var text = format == "text";
                var name = FormattableString.Invariant($"pithyx-logs-{time.GetUtcNow():yyyyMMdd-HHmmss}.{(text ? "log" : "jsonl")}");
                return TypedResults.Stream(
                    stream => service.ExportAsync(query, text, stream, context.RequestAborted),
                    text ? "text/plain; charset=utf-8" : "application/x-ndjson",
                    name);
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("ExportLogs")
            .WithDescription("Administrators only. Every entry matching the same filters as GET /api/logs, newest first, as a download: JSON lines, or text lines with format=text. At most 1,000,000 entries.");
        logs.MapGet("/settings", async (LogService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await service.StatusAsync(cancellationToken));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("GetLogSettings")
            .WithDescription("Administrators only. The retention of the current logs and of the archive, with the space they take, their oldest entries and the pools that can hold the archive.");
        logs.MapPut("/settings", async (LogSettings settings, LogService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await service.SetSettingsAsync(settings, cancellationToken));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("SetLogSettings")
            .WithDescription("Administrators only. Sets the retention. The current logs (system and apps together) stay below maxBytes, at most 4 GB; entries older than retentionDays go. The archive on a pool is optional; archivePool null means the first pool.");

        api.MapPost("/apps/{app}/logs", (string app, AppLogBatch batch, LogService service, Permissions.PermissionService permissions, HttpContext context) =>
            {
                // Only an app the user may open writes in the user's name.
                if (!Permissions.Resources.AllowsApp(permissions.ForUser(context.User), app))
                {
                    throw new PithyxException(ErrorCodes.AuthPermissionDenied);
                }
                return TypedResults.Ok(service.WriteApp(app, context.User.FindFirstValue(ClaimTypes.Name) ?? "", batch));
            })
            .WithName("WriteAppLogs")
            .WithTags("Logs")
            .WithDescription("Stores log entries an app wrote through the SDK for the signed-in user, as source app/<app>; only for an app the user may open (auth.permission.denied otherwise). At most 100 entries per request; each app may write bursts of 200 and then 20 entries a second, the rest is dropped and counted.");
    }

    private static LogQuery Query(DateTimeOffset? from, DateTimeOffset? to, string[]? levels, string[]? sources, string? search, string? cursor, int limit)
    {
        if (limit is < 1 or > LogService.MaxLimit || (from is { } start && to is { } end && start > end))
        {
            throw new PithyxException(ErrorCodes.LogsQueryInvalid);
        }
        LogCursor? before = null;
        if (cursor is not null && (before = LogCursor.Parse(cursor)) is null)
        {
            throw new PithyxException(ErrorCodes.LogsQueryInvalid);
        }
        var levelSet = new HashSet<LogLevelName>();
        foreach (var level in levels ?? [])
        {
            if (!Enum.TryParse<LogLevelName>(level, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                throw new PithyxException(ErrorCodes.LogsQueryInvalid);
            }
            levelSet.Add(parsed);
        }
        var sourceSet = sources is { Length: > 0 } ? sources.ToHashSet(StringComparer.Ordinal) : null;
        return new LogQuery(from, to, levelSet.Count == 0 ? null : levelSet, sourceSet, search, before, limit);
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}
