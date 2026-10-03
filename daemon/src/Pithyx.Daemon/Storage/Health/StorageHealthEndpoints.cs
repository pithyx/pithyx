using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage.Health;

/// <summary>Alerts, SMART, scrubs and schedules for the Storage settings page. Administrators only.</summary>
public static class StorageHealthEndpoints
{
    public static IServiceCollection AddPithyxStorageHealth(this IServiceCollection services)
    {
        services.AddSingleton<SmartInventory>();
        services.AddSingleton<StorageSchedules>();
        services.AddSingleton<StorageAlertService>();
        services.AddSingleton(provider => provider.GetRequiredService<StorageAlertService>().Definition());
        services.AddSingleton<StorageAlertNotifier>();
        services.AddSingleton<StorageHealthMonitor>();
        return services;
    }

    /// <summary>Registered after <see cref="SystemStateService"/>, so pools and disks are read before the first evaluation.</summary>
    public static IServiceCollection AddPithyxStorageHealthMonitor(this IServiceCollection services) =>
        services.AddHostedService(provider => provider.GetRequiredService<StorageHealthMonitor>());

    public static void MapStorageHealthEndpoints(this RouteGroupBuilder api)
    {
        var storage = api.MapGroup("/storage").WithTags("Storage");
        storage.MapGet("/alerts", (StorageAlertService alerts, HttpContext context) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(alerts.For(context.User, alerts.Current));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("GetStorageAlerts")
            .WithDescription("Administrators only. Current storage alerts with the caller's mute flags, and the SMART state of every disk; live through the storage/alerts topic.");
        storage.MapPost("/alerts/{key}/mute", async (string key, StorageAlertService alerts, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await alerts.MuteAsync(context.User, key, cancellationToken);
                hub.Publish(StorageAlertService.Topic, alerts.Current);
                return TypedResults.Ok(alerts.For(context.User, alerts.Current));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("MuteStorageAlert")
            .WithDescription("Administrators only. Hides a current alert for the caller until it goes away or becomes more severe. Alerts cannot be deleted.");
        storage.MapDelete("/alerts/{key}/mute", async (string key, StorageAlertService alerts, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await alerts.UnmuteAsync(context.User, key, cancellationToken);
                hub.Publish(StorageAlertService.Topic, alerts.Current);
                return TypedResults.Ok(alerts.For(context.User, alerts.Current));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("UnmuteStorageAlert")
            .WithDescription("Administrators only. Shows a muted alert again.");
        storage.MapPost("/pools/{pool}/scrub", (string pool, PoolInventory pools, ICommandRunner commands, HttpContext context, CancellationToken cancellationToken) =>
                ScrubAsync(pool, "start", pools, commands, context.User, cancellationToken))
            .RequireAuthorization(Policies.Admin)
            .WithName("StartScrub")
            .WithDescription("Administrators only. Starts a scrub of the pool, or resumes a paused one, and returns at once; progress arrives on storage/pools.");
        storage.MapPost("/pools/{pool}/scrub/pause", (string pool, PoolInventory pools, ICommandRunner commands, HttpContext context, CancellationToken cancellationToken) =>
                ScrubAsync(pool, "pause", pools, commands, context.User, cancellationToken))
            .RequireAuthorization(Policies.Admin)
            .WithName("PauseScrub")
            .WithDescription("Administrators only. Pauses the running scrub; starting it again resumes where it stopped.");
        storage.MapDelete("/pools/{pool}/scrub", (string pool, PoolInventory pools, ICommandRunner commands, HttpContext context, CancellationToken cancellationToken) =>
                ScrubAsync(pool, "stop", pools, commands, context.User, cancellationToken))
            .RequireAuthorization(Policies.Admin)
            .WithName("StopScrub")
            .WithDescription("Administrators only. Stops the running scrub; the next one starts from the beginning.");
        storage.MapGet("/schedules", async (StorageHealthMonitor monitor, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await monitor.ListAsync(cancellationToken));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("GetStorageSchedules")
            .WithDescription("Administrators only. Scrub schedules per pool and the SMART self-test schedules, with their last and next runs.");
        storage.MapPut("/schedules", async (StorageScheduleUpdate update, StorageSchedules schedules, StorageHealthMonitor monitor, PoolInventory pools, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                StorageSchedules.Validate(update, pools.Current.Pools.Select(pool => pool.Name));
                await schedules.SetAsync(update, cancellationToken);
                monitor.Wake();
                return TypedResults.Ok(await monitor.ListAsync(cancellationToken));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("SetStorageSchedule")
            .WithDescription("Administrators only. Sets how often a pool is scrubbed or the disks run a self-test: off, weekly or monthly.");
    }

    private static async Task<NoContent> ScrubAsync(string pool, string action, PoolInventory pools, ICommandRunner commands, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        RequireAdmin(user);
        var current = pools.Current.Pools.FirstOrDefault(candidate => candidate.Name == pool)
            ?? throw new PithyxException(ErrorCodes.StoragePoolNotFound, new Dictionary<string, object?> { ["pool"] = pool });
        var scrubbing = current.Scan is { Kind: "scrub", State: "scanning" } scan ? scan : null;
        if (action == "start" && scrubbing is { Paused: false })
        {
            throw new PithyxException(ErrorCodes.StorageScrubRunning, new Dictionary<string, object?> { ["pool"] = pool });
        }
        if (action != "start" && scrubbing is null)
        {
            throw new PithyxException(ErrorCodes.StorageScrubNotRunning, new Dictionary<string, object?> { ["pool"] = pool });
        }
        var command = action switch
        {
            "pause" => ZfsScrubPlans.Pause(pool),
            "stop" => ZfsScrubPlans.Stop(pool),
            _ => ZfsScrubPlans.Start(pool),
        };
        var result = await commands.RunAsync(command, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(
                ErrorCodes.StorageCommandFailed,
                new Dictionary<string, object?> { ["command"] = command.FileName, ["exitCode"] = result.ExitCode });
        }
        return TypedResults.NoContent();
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}
