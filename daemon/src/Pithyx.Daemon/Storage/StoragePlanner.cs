using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage;

/// <summary>Turns a request into a <see cref="CommandPlan"/> after probing the chosen disks and asking the guard.</summary>
public sealed class StoragePlanner(SystemStateService state, StorageFacts facts, PoolInventory pools, DatasetPlanner datasetPlanner, TimeProvider time)
{
    public sealed record Result(IReadOnlyList<GuardVerdict> Verdicts, CommandPlan? Plan, IReadOnlyList<string> Datasets);

    public async Task<Result> PlanAsync(StoragePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diskIds = (request.DiskIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        var confirmations = request.Confirmations ?? new Dictionary<string, string>();
        switch (request.Action)
        {
            case "pool.create":
                ZfsPlans.ValidateName(request.Pool, pools.Current.Pools.Select(pool => pool.Name));
                ZfsPlans.ValidateLayout(request.Layout ?? "", diskIds.Count);
                RequireEncryptionAnswer(request.Encryption);
                return await WithDisksAsync(request, diskIds, confirmations, disks => ZfsPlans.CreatePool(request.Pool, request.Layout!, disks), cancellationToken);
            case "pool.replace":
                {
                    var leaf = Leaf(Pool(request.Pool), request.Vdev);
                    RequireOneDisk(diskIds);
                    // A disk is never used as a replacement on its own: the user always types its serial, even when it is empty.
                    return await WithDisksAsync(request, diskIds, confirmations, disks => ZfsPlans.Replace(request.Pool, leaf.VdevGuid, disks[0]), cancellationToken, requireSerial: true);
                }
            case "pool.spare.add":
                Pool(request.Pool);
                RequireOneDisk(diskIds);
                return await WithDisksAsync(request, diskIds, confirmations, disks => ZfsPlans.AddSpare(request.Pool, disks[0]), cancellationToken);
            case "pool.spare.remove":
                {
                    var spare = Leaf(Pool(request.Pool), request.Vdev);
                    if (spare.Role != "spare")
                    {
                        throw new PithyxException(ErrorCodes.StorageVdevNotFound, new Dictionary<string, object?> { ["vdev"] = request.Vdev });
                    }
                    return Plan(request, ZfsPlans.RemoveSpare(request.Pool, spare.VdevGuid));
                }
            case "pool.offline":
                return Plan(request, ZfsPlans.Offline(request.Pool, Leaf(Pool(request.Pool), request.Vdev).VdevGuid));
            case "pool.online":
                return Plan(request, ZfsPlans.Online(request.Pool, Leaf(Pool(request.Pool), request.Vdev).VdevGuid));
            case "dataset.create":
                {
                    var pool = Pool(request.Pool);
                    var name = request.Name ?? "";
                    if (pools.Current.Datasets.Any(dataset => dataset.Name == $"{pool.Name}/pithyx/shares/{name}"))
                    {
                        throw new PithyxException(ErrorCodes.StorageDatasetExists, new Dictionary<string, object?> { ["name"] = name });
                    }
                    return Plan(request, ZfsPlans.CreateShareDataset(pool.Name, name));
                }
            case "pool.destroy":
                {
                    var pool = Pool(request.Pool);
                    if (!string.Equals(request.ConfirmName, pool.Name, StringComparison.Ordinal))
                    {
                        throw new PithyxException(ErrorCodes.StoragePoolNameMismatch, new Dictionary<string, object?> { ["name"] = pool.Name });
                    }
                    var datasets = pools.Current.Datasets.Where(dataset => dataset.Pool == pool.Name).Select(dataset => dataset.Name).ToList();
                    return Plan(request, ZfsPlans.DestroyPool(pool.Name)) with { Datasets = datasets };
                }
            case var action when DatasetPlanner.Actions.Contains(action):
                return await datasetPlanner.PlanAsync(request, cancellationToken);
            default:
                throw new PithyxException(ErrorCodes.StoragePlanActionInvalid, new Dictionary<string, object?> { ["action"] = request.Action });
        }
    }

    private static void RequireEncryptionAnswer(string? encryption)
    {
        switch (encryption)
        {
            case "none":
                return;
            case "key":
                // Step 9 creates <pool>/pithyx as the encryption root with the key handling; until then only the answer exists.
                throw new PithyxException(ErrorCodes.StorageEncryptionUnavailable);
            default:
                throw new PithyxException(ErrorCodes.StoragePoolEncryptionRequired);
        }
    }

    private static void RequireOneDisk(List<string> diskIds)
    {
        if (diskIds.Count != 1)
        {
            throw new PithyxException(ErrorCodes.StoragePoolDiskCount, new Dictionary<string, object?> { ["layout"] = "single", ["min"] = 1, ["count"] = diskIds.Count });
        }
    }

    private Pool Pool(string name) =>
        pools.Current.Pools.FirstOrDefault(pool => pool.Name == name)
        ?? throw new PithyxException(ErrorCodes.StoragePoolNotFound, new Dictionary<string, object?> { ["name"] = name });

    private static Vdev Leaf(Pool pool, string? guid) =>
        pool.Vdevs.SelectMany(vdev => vdev.Leaves()).FirstOrDefault(leaf => leaf.VdevGuid == guid)
        ?? throw new PithyxException(ErrorCodes.StorageVdevNotFound, new Dictionary<string, object?> { ["vdev"] = guid });

    private Result Plan(StoragePlanRequest request, IReadOnlyList<PlanStep> steps) =>
        new([], new CommandPlan(request.Action, steps, [], new Dictionary<string, string>(), time.GetUtcNow() + CommandPlan.Lifetime), []);

    private async Task<Result> WithDisksAsync(
        StoragePlanRequest request,
        List<string> diskIds,
        IReadOnlyDictionary<string, string> confirmations,
        Func<IReadOnlyList<GuardDisk>, IReadOnlyList<PlanStep>> build,
        CancellationToken cancellationToken,
        bool requireSerial = false)
    {
        // Right before a plan the chosen disks are probed again: what the page showed may be minutes old.
        await state.RefreshDisksAsync(diskIds, cancellationToken);
        var current = facts.Current();
        var chosen = diskIds.Select(id => current.Disks.FirstOrDefault(disk => disk.Disk.Id == id)
            ?? throw new PithyxException(ErrorCodes.StorageDiskNotFound, new Dictionary<string, object?> { ["disk"] = id })).ToList();
        var verdicts = DiskGuard.Evaluate(current, confirmations)
            .Where(verdict => diskIds.Contains(verdict.DiskId))
            .Select(verdict => requireSerial && verdict.Decision == GuardDecision.Allowed && !Confirmed(chosen, confirmations, verdict.DiskId)
                ? verdict with { Decision = GuardDecision.NeedsSerialConfirmation, Reason = "confirm_replacement" }
                : verdict)
            .ToList();
        if (verdicts.Any(verdict => verdict.Decision != GuardDecision.Allowed))
        {
            return new Result(verdicts, null, []);
        }
        var plan = new CommandPlan(
            request.Action,
            build(chosen),
            [.. chosen.Select(disk => new PlannedDisk(disk.Disk.Id, disk.Disk.Serial, disk.Disk.SizeBytes, DiskGuard.Fingerprint(disk)))],
            confirmations.Where(entry => diskIds.Contains(entry.Key)).ToDictionary(StringComparer.Ordinal),
            time.GetUtcNow() + CommandPlan.Lifetime);
        return new Result(verdicts, plan, []);
    }

    private static bool Confirmed(List<GuardDisk> disks, IReadOnlyDictionary<string, string> confirmations, string diskId) =>
        disks.First(disk => disk.Disk.Id == diskId).Disk.Serial is { } serial
        && confirmations.TryGetValue(diskId, out var typed)
        && string.Equals(typed.Trim(), serial, StringComparison.Ordinal);
}
