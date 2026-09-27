using System.Security.Claims;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Storage.Snapshots;

/// <summary>Snapshots and their schedules for the Storage settings page. Administrators only.</summary>
public static class SnapshotEndpoints
{
    public static IServiceCollection AddPithyxSnapshots(this IServiceCollection services)
    {
        services.AddSingleton<SnapshotInventory>();
        services.AddSingleton<SnapshotService>();
        // The build-time OpenAPI run must not run zfs on the build machine.
        if (!BuildTimeDocument.IsGenerating)
        {
            services.AddHostedService<SnapshotScheduler>();
        }
        return services;
    }

    public static void MapSnapshotEndpoints(this RouteGroupBuilder api)
    {
        var snapshots = api.MapGroup("/storage/snapshots").WithTags("Storage");
        snapshots.MapGet("", (string dataset, SnapshotService service, HttpContext context) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(service.List(dataset));
            })
            .WithName("ListSnapshots")
            .WithDescription("Administrators only. The snapshots of a dataset as last read, oldest first. pithyxKind is set for snapshots Pithyx took.");
        snapshots.MapPost("", async (SnapshotCreateRequest request, SnapshotService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Created((string?)null, await service.TakeAsync(request.Dataset, SnapshotKinds.Manual, cancellationToken));
            })
            .WithName("CreateSnapshot")
            .WithDescription("Administrators only. Takes pithyx-manual-<utc> of the dataset now. Manual snapshots are never pruned by a schedule.");
        snapshots.MapPost("/delete", async (SnapshotRequest request, SnapshotService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await service.DeleteAsync(request.Dataset, request.Name, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteSnapshot")
            .WithDescription("Administrators only. Destroys one snapshot, named exactly. Only snapshots Pithyx took (pithyx-...) can be deleted; held ones are refused.");
        snapshots.MapPost("/hold", async (SnapshotRequest request, SnapshotService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await service.HoldAsync(request.Dataset, request.Name, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("HoldSnapshot")
            .WithDescription("Administrators only. Places the hold \"pithyx\" on the snapshot, so neither a schedule nor anyone else can destroy it.");
        snapshots.MapPost("/release", async (SnapshotRequest request, SnapshotService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await service.ReleaseAsync(request.Dataset, request.Name, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("ReleaseSnapshot")
            .WithDescription("Administrators only. Releases the hold \"pithyx\" of the snapshot. Holds of other tools stay.");
        snapshots.MapPost("/rollback", async (SnapshotRollbackRequest request, SnapshotService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await service.RollbackAsync(request, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("RollbackSnapshot")
            .WithDescription(
                "Administrators only. Rolls the whole dataset back to the snapshot; changes since are lost. confirmDataset is the dataset name as the user typed it. "
                + "When newer snapshots exist they are destroyed too, so discardNewer must list every one of them by name, or nothing happens (storage.snapshot.newer_exist).");
        snapshots.MapGet("/schedules", async (string dataset, SnapshotService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await service.GetSchedulesAsync(dataset, cancellationToken));
            })
            .WithName("GetSnapshotSchedules")
            .WithDescription("Administrators only. The hourly, daily, weekly and monthly schedules of a dataset; off with the default counts (24, 7, 4, 6) until saved.");
        snapshots.MapPut("/schedules", async (SnapshotScheduleList request, SnapshotService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await service.SetSchedulesAsync(request, cancellationToken));
            })
            .WithName("SetSnapshotSchedules")
            .WithDescription(
                "Administrators only. Saves the given schedules of a dataset. A schedule takes pithyx-<kind>-<utc> once per UTC hour, day, week (from Monday) or month "
                + "and keeps its newest keep snapshots; it never deletes other snapshots, held ones or ones dated after the current time.");
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}
