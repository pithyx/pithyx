using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.Storage;

/// <summary>
/// Runs a <see cref="CommandPlan"/> exactly as it was shown. Before every command it reads the disks again, probes the
/// ones the plan touches and asks <see cref="DiskGuard"/> once more: a disk that changed or is no longer allowed stops
/// the plan before anything is written to it. Steps of an import are checked with the import rule, after the pools were
/// read again. When the plan ends, the pools are read again, so an imported or exported pool shows at once.
/// </summary>
public sealed class CommandPlanRunner(ICommandRunner commands, SystemStateService state, StorageFacts facts, TimeProvider time, FolderWatches? watches = null)
{
    /// <param name="plan">The plan as shown.</param>
    /// <param name="progress">Called before each step with its index and the step count.</param>
    /// <param name="cancellationToken">Stops before the next command; a running command is not interrupted.</param>
    public async Task RunAsync(CommandPlan plan, Action<int, int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            if (plan.Within is { } within)
            {
                await within(token => RunStepsAsync(plan, progress, token), cancellationToken);
            }
            else
            {
                await RunStepsAsync(plan, progress, cancellationToken);
            }
        }
        catch
        {
            // A plan that stopped halfway may still have changed pools; the page shows what is there now.
            await state.RefreshPoolsAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task RunStepsAsync(CommandPlan plan, Action<int, int>? progress, CancellationToken cancellationToken)
    {
        if (plan.Releases is { Count: > 0 } releases)
        {
            watches?.ReleaseBelow(releases);
        }
        var touched = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = plan.Steps[index];
            if (time.GetUtcNow() > plan.ExpiresAt)
            {
                throw new PithyxException(ErrorCodes.StoragePlanExpired);
            }
            progress?.Invoke(index, plan.Steps.Count);
            if (step.Rule == GuardRule.Import)
            {
                // Which pool a disk belongs to decides the import rule, so the pools must be current, not as of the last event.
                await state.RefreshPoolsAsync(cancellationToken);
            }
            await state.RefreshDisksAsync(step.DiskIds, cancellationToken);
            CheckDisks(plan, step, touched);

            var result = await commands.RunAsync(step.Command, cancellationToken);
            if (result.ExitCode != 0 && Zfs.ImportPlans.AsksForForce(step.Command, result))
            {
                // ZFS found the pool in use by another system after all; only a new plan with the typed name may force it.
                throw new PithyxException(ErrorCodes.StorageImportForceRequired);
            }
            if (result.ExitCode != 0)
            {
                throw new PithyxException(
                    ErrorCodes.StorageCommandFailed,
                    new Dictionary<string, object?> { ["command"] = step.Command.FileName, ["exitCode"] = result.ExitCode });
            }
            touched.UnionWith(step.DiskIds);
        }
        // The state is read again first, so bookkeeping sees what the commands changed.
        await state.RefreshPoolsAsync(cancellationToken);
        if (plan.AfterRun is { } afterRun)
        {
            await afterRun(cancellationToken);
        }
    }

    /// <summary>
    /// A disk must still be the one the user saw: the same serial and size always, and until the plan first writes to it
    /// also the same partitions and signatures. Its guard verdict must be Allowed with the plan's confirmations.
    /// </summary>
    private void CheckDisks(CommandPlan plan, PlanStep step, HashSet<string> touched)
    {
        var current = facts.Current();
        var verdicts = (step.Rule == GuardRule.Import ? DiskGuard.EvaluateImport(current, step.ImportingPool) : DiskGuard.Evaluate(current, plan.Confirmations))
            .ToDictionary(verdict => verdict.DiskId, StringComparer.Ordinal);
        foreach (var diskId in step.DiskIds)
        {
            var planned = plan.Disks.Single(disk => disk.DiskId == diskId);
            var disk = current.Disks.FirstOrDefault(candidate => candidate.Disk.Id == diskId);
            var sameDisk = disk is not null && disk.Disk.Serial == planned.Serial && disk.Disk.SizeBytes == planned.SizeBytes;
            if (!sameDisk || (!touched.Contains(diskId) && DiskGuard.Fingerprint(disk!, step.Rule) != planned.Fingerprint))
            {
                throw new PithyxException(ErrorCodes.StoragePlanStale, new Dictionary<string, object?> { ["disk"] = diskId });
            }
            if (verdicts[diskId] is { Decision: not GuardDecision.Allowed } refused)
            {
                throw new PithyxException(ErrorCodes.StorageDiskRefused, new Dictionary<string, object?> { ["disk"] = diskId, ["reason"] = refused.Reason });
            }
        }
    }
}
