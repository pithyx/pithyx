using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Storage;

/// <summary>Pools and disks for the Storage settings page. Administrators only.</summary>
public static class StorageEndpoints
{
    public static void MapStorageEndpoints(this RouteGroupBuilder api)
    {
        var storage = api.MapGroup("/storage").WithTags("Storage");
        storage.MapGet("/pools", (PoolInventory pools, HttpContext context) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(pools.Current);
            })
            .WithName("GetPools")
            .WithDescription("Administrators only. Pools with their vdevs, and datasets, as last read; live through the storage/pools topic.");
        storage.MapGet("/disks", (StorageFacts facts, HttpContext context) =>
            {
                RequireAdmin(context.User);
                var current = facts.Current();
                var verdicts = DiskGuard.Evaluate(current).ToDictionary(verdict => verdict.DiskId, StringComparer.Ordinal);
                return TypedResults.Ok(new StorageDiskList(
                    [.. current.Disks.Select(disk => new StorageDisk(disk.Disk, verdicts[disk.Disk.Id]))],
                    facts.Environment.Kind == DeploymentKind.Dev ? "dev" : "prod"));
            })
            .WithName("GetStorageDisks")
            .WithDescription("Administrators only. Every disk with the disk guard's verdict: whether a new pool may use it, and why not.");
        storage.MapPost("/pools/preview", PreviewAsync)
            .WithName("PreviewPoolCreate")
            .WithDescription("Administrators only. Probes the chosen disks, asks the disk guard and returns the exact commands that would create the pool. Runs nothing.");
    }

    private static async Task<Ok<PoolCreatePreview>> PreviewAsync(PoolCreatePreviewRequest request, StoragePlanner planner, HttpContext context, CancellationToken cancellationToken)
    {
        RequireAdmin(context.User);
        var (verdicts, plan) = await planner.PlanPoolCreateAsync(request, cancellationToken);
        return TypedResults.Ok(new PoolCreatePreview(
            verdicts,
            plan?.Steps.Select(step => new PlannedCommand(step.Command.FileName, step.Command.Arguments)).ToList(),
            plan?.ExpiresAt ?? DateTimeOffset.UtcNow));
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}
