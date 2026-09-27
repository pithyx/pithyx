using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage;

/// <summary>Turns a request into a <see cref="CommandPlan"/> after probing the chosen disks and asking the guard.</summary>
public sealed class StoragePlanner(SystemStateService state, StorageFacts facts, PoolInventory pools, TimeProvider time)
{
    public async Task<(IReadOnlyList<GuardVerdict> Verdicts, CommandPlan? Plan)> PlanPoolCreateAsync(PoolCreatePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diskIds = request.DiskIds.Distinct(StringComparer.Ordinal).ToList();
        ZfsPlans.ValidateName(request.Name, pools.Current.Pools.Select(pool => pool.Name));
        ZfsPlans.ValidateLayout(request.Layout, diskIds.Count);

        // Right before a plan the chosen disks are probed again: what the page showed may be minutes old.
        await state.RefreshDisksAsync(diskIds, cancellationToken);
        var current = facts.Current();
        var chosen = diskIds.Select(id => current.Disks.FirstOrDefault(disk => disk.Disk.Id == id)
            ?? throw new PithyxException(ErrorCodes.StorageDiskNotFound, new Dictionary<string, object?> { ["disk"] = id })).ToList();
        var confirmations = request.Confirmations ?? new Dictionary<string, string>();
        var verdicts = DiskGuard.Evaluate(current, confirmations).Where(verdict => diskIds.Contains(verdict.DiskId)).ToList();
        if (verdicts.Any(verdict => verdict.Decision != GuardDecision.Allowed))
        {
            return (verdicts, null);
        }
        var plan = new CommandPlan(
            "pool.create",
            ZfsPlans.CreatePool(request.Name, request.Layout, chosen),
            [.. chosen.Select(disk => new PlannedDisk(disk.Disk.Id, disk.Disk.Serial, disk.Disk.SizeBytes, DiskGuard.Fingerprint(disk)))],
            confirmations.Where(entry => diskIds.Contains(entry.Key)).ToDictionary(StringComparer.Ordinal),
            time.GetUtcNow() + CommandPlan.Lifetime);
        return (verdicts, plan);
    }
}
