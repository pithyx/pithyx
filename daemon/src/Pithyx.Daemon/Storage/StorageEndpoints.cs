using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Jobs;

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
        storage.MapPost("/plans", PlanAsync)
            .WithName("PlanStorageAction")
            .WithDescription("Administrators only. Probes the chosen disks, asks the disk guard and returns the exact commands of a destructive storage action, with a plan id to run them. Runs nothing.");
        storage.MapPost("/plans/{planId}/run", RunAsync)
            .WithName("RunStoragePlan")
            .WithDescription("Administrators only. Runs a plan exactly as it was shown, as a job; the disk guard checks every disk again right before each command. A plan runs once and expires after 10 minutes.");
    }

    private static async Task<Ok<StoragePlanPreview>> PlanAsync(StoragePlanRequest request, StoragePlanner planner, CommandPlanStore plans, HttpContext context, CancellationToken cancellationToken)
    {
        RequireAdmin(context.User);
        var result = await planner.PlanAsync(request, cancellationToken);
        var plan = result.Plan;
        return TypedResults.Ok(new StoragePlanPreview(
            plan is null ? null : plans.Save(CurrentUser.Id(context.User), plan),
            result.Verdicts,
            plan?.Steps.Select(step => new PlannedCommand(step.Command.FileName, step.Command.Arguments)).ToList(),
            result.Datasets,
            plan?.ExpiresAt ?? DateTimeOffset.UtcNow));
    }

    private static async Task<Accepted<Job>> RunAsync(string planId, CommandPlanStore plans, CommandPlanRunner runner, JobService jobs, HttpContext context, CancellationToken cancellationToken)
    {
        RequireAdmin(context.User);
        var userId = CurrentUser.Id(context.User);
        var plan = plans.Take(userId, planId);
        var commands = plan.Steps.Select(step => string.Join(' ', [step.Command.FileName, .. step.Command.LoggableArguments])).ToList();
        var job = await jobs.StartAsync(
            userId,
            $"storage.{plan.Kind}",
            new { commands },
            work => runner.RunAsync(
                plan,
                (index, count) => work.Report(new JobProgress(index, count, 0, 0, commands[index])),
                work.Cancellation),
            cancellationToken);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}
