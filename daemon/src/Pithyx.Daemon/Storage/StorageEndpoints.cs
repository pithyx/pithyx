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
            .RequireAuthorization(Policies.Admin)
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
            .RequireAuthorization(Policies.Admin)
            .WithName("GetStorageDisks")
            .WithDescription("Administrators only. Every disk with the disk guard's verdict: whether a new pool may use it, and why not.");
        storage.MapPost("/import/scan", async (PoolImportScanner scanner, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await scanner.ScanAsync(cancellationToken));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("ScanImportablePools")
            .WithDescription("Administrators only. Looks for pools on the disks that are not imported (zpool import, which reads every disk's labels and may wake a sleeping disk) and returns each with its layout and the disk guard's import verdict. Imports nothing; importing is the plan pool.import.readonly, then pool.import.");
        storage.MapPost("/encryption/keys", (HttpContext context) =>
            {
                RequireAdmin(context.User);
                var key = Encryption.PoolKey.New();
                return TypedResults.Ok(new NewPoolKey(key, Encryption.PoolKey.ToHex(key)));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("CreatePoolKey")
            .WithDescription("Administrators only. A new random pool key (D384) for an encrypted pool or a key change. The box keeps nothing: the key is used only when it comes back in the request that creates the pool or changes its key.");
        storage.MapPost("/pools/{pool}/unlock", async (string pool, PoolUnlockRequest request, Encryption.PoolEncryptionService encryption, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await encryption.UnlockAsync(pool, string.IsNullOrWhiteSpace(request.Key) ? null : request.Key, request.KeepInVault, cancellationToken);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("UnlockPool")
            .WithDescription("Administrators only. Loads the key of an encrypted pool, typed or from the key vault, and mounts its datasets; its shares, homes and apps follow. Without a key it takes the vault's, and on an unlocked pool it only mounts what is missing.");
        storage.MapPost("/pools/{pool}/key", async (string pool, PoolKeyChange request, Encryption.PoolEncryptionService encryption, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await encryption.ChangeKeyAsync(pool, request.Key, request.KeepInVault, cancellationToken);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("ChangePoolKey")
            .WithDescription("Administrators only. Replaces the key of an unlocked encrypted pool with a new key from POST /api/storage/encryption/keys, which the user stored first; the old key stops working at once. The key vault then holds the new key, or none.");
        storage.MapPost("/pools/{pool}/key/reveal", RevealKeyAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("RevealPoolKey")
            .WithDescription("Administrators only. The pool's key from the key vault, for an administrator who lost the stored copy (D384). Needs the password again and, with two-factor sign-in on, a current authenticator code; failures count like failed sign-ins. Logged without the key.");
        storage.MapGet("/usb-sticks", (Encryption.PoolEncryptionService encryption, HttpContext context) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(new Encryption.UsbStickList(encryption.Sticks()));
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("ListUsbSticks")
            .WithDescription("Administrators only. The file systems on USB sticks and other removable disks a pool key can be written to (D385): FAT, exFAT or ext, on a disk nothing has mounted and no pool uses. From memory; reads no disk.");
        storage.MapPost("/pools/{pool}/key/usb", WriteKeyToUsbAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("WritePoolKeyToUsb")
            .WithDescription("Administrators only. Writes the pool's key to a USB stick as pithyx-keys/<pool GUID>.key (D385); the box then unlocks the pool whenever the stick is plugged in, and the stick may be pulled afterwards. The key is the typed one or the key vault's, which needs the password again and, with two-factor sign-in on, a current authenticator code (failures count like failed sign-ins). The key is checked against the pool first and read back from the stick. Logged without the key.");
        storage.MapDelete("/pools/{pool}/key/vault", async (string pool, Encryption.PoolEncryptionService encryption, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                await encryption.ForgetKeyAsync(pool, cancellationToken);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("RemovePoolKeyFromVault")
            .WithDescription("Administrators only. Deletes the pool's key from the key vault on the system disk. The pool stays unlocked until the box restarts; then it waits for its key to be typed.");
        storage.MapPost("/plans", PlanAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("PlanStorageAction")
            .WithDescription("Administrators only. Probes the chosen disks, asks the disk guard and returns the exact commands of a destructive storage action, with a plan id to run them. Runs nothing.");
        storage.MapPost("/plans/{planId}/run", RunAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("RunStoragePlan")
            .WithDescription("Administrators only. Runs a plan exactly as it was shown, as a job; the disk guard checks every disk again right before each command. A plan runs once and expires after 10 minutes.");
    }

    private static async Task<NoContent> WriteKeyToUsbAsync(
        string pool,
        PoolKeyUsbRequest request,
        Encryption.PoolEncryptionService encryption,
        AuthService auth,
        LoginRateLimiter limiter,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        RequireAdmin(context.User);
        ArgumentNullException.ThrowIfNull(request);
        var username = context.User.Identity?.Name ?? "";
        var typed = string.IsNullOrWhiteSpace(request.Key) ? null : request.Key;
        if (typed is null)
        {
            // The vault's key leaves the box on the stick, as when it is shown: the same proof is asked.
            limiter.EnsureAllowed(context, username);
            await AuthEndpoints.CountFailuresAsync(limiter, context, username, async () =>
            {
                await auth.ConfirmStepUpAsync(CurrentUser.Id(context.User), request.Password ?? "", request.TotpCode, cancellationToken);
                return true;
            });
        }
        await encryption.WriteToUsbAsync(pool, request.Device ?? "", typed, request.RemoveFromVault, username, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<NewPoolKey>> RevealKeyAsync(
        string pool,
        PoolKeyRevealRequest request,
        Encryption.PoolEncryptionService encryption,
        AuthService auth,
        LoginRateLimiter limiter,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        RequireAdmin(context.User);
        ArgumentNullException.ThrowIfNull(request);
        var username = context.User.Identity?.Name ?? "";
        limiter.EnsureAllowed(context, username);
        await AuthEndpoints.CountFailuresAsync(limiter, context, username, async () =>
        {
            await auth.ConfirmStepUpAsync(CurrentUser.Id(context.User), request.Password, request.TotpCode, cancellationToken);
            return true;
        });
        var hex = encryption.RevealKey(pool, username);
        return TypedResults.Ok(new NewPoolKey(Encryption.PoolKey.FromHex(hex), hex));
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
            plan?.ExpiresAt ?? DateTimeOffset.UtcNow,
            result.Warnings,
            result.RequiresConfirmName,
            result.Unmounted));
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
