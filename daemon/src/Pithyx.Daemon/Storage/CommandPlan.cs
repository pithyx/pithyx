using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage;

/// <summary>Which rule of <see cref="DiskGuard"/> checks the disks of a step.</summary>
public enum GuardRule
{
    /// <summary><see cref="DiskGuard.Evaluate"/>: disks with data need their serial typed.</summary>
    NewData,

    /// <summary><see cref="DiskGuard.EvaluateImport"/>: the disks of a pool that is imported, with their labels.</summary>
    Import,
}

/// <param name="Command">The exact command, run without a shell.</param>
/// <param name="DiskIds">The disks it writes to; the guard checks them right before it runs.</param>
/// <param name="Rule">The guard's rule for those disks.</param>
/// <param name="ImportingPool">For <see cref="GuardRule.Import"/>: the imported pool whose disks the step may touch, or null.</param>
/// <param name="ReachablePool">
/// A pool the command writes to on all its disks (destroy, export): right before it runs, the pool must not be suspended
/// or have lost a vdev (<see cref="PoolHealth.Unreachable"/>), since ZFS would then wait in the kernel forever.
/// </param>
public sealed record PlanStep(CommandRequest Command, IReadOnlyList<string> DiskIds, GuardRule Rule = GuardRule.NewData, string? ImportingPool = null, string? ReachablePool = null);

/// <param name="DiskId">The disk's id.</param>
/// <param name="Serial">Its serial when the plan was made.</param>
/// <param name="SizeBytes">Its size when the plan was made.</param>
/// <param name="Fingerprint">Serial, size, partitions and signatures when the plan was shown (<see cref="DiskGuard.Fingerprint"/>).</param>
public sealed record PlannedDisk(string DiskId, string? Serial, long SizeBytes, string Fingerprint);

/// <summary>
/// A destructive action as a list of exact commands. It is shown to the user first and run only as it was shown: every
/// disk it touches is checked by <see cref="DiskGuard"/> again before each command.
/// </summary>
/// <param name="Kind">What the plan does, for example <c>pool.create</c>.</param>
/// <param name="Steps">The commands in order.</param>
/// <param name="Disks">The disks as the user saw them.</param>
/// <param name="Confirmations">Serials the user typed, by disk id, for disks that were not empty.</param>
/// <param name="ExpiresAt">After this the plan must be shown again.</param>
/// <param name="AfterRun">
/// Runs in the plan's job once every command succeeded, for the daemon's own bookkeeping (for example registering the
/// share a new dataset belongs to). It never runs a command of its own.
/// </param>
/// <param name="Releases">
/// Mount points the plan unmounts (destroy, rename, export). Before the first command the daemon closes its own
/// handles below them, the live folder views of Files, because ZFS cannot unmount a dataset that a handle holds.
/// </param>
/// <param name="Within">
/// Wraps the whole run for a condition that must hold while the commands run, for example that a rollback still
/// discards exactly the snapshots the user saw. It calls the run it is given once, or throws without running it.
/// </param>
public sealed record CommandPlan(
    string Kind,
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<PlannedDisk> Disks,
    IReadOnlyDictionary<string, string> Confirmations,
    DateTimeOffset ExpiresAt,
    Func<CancellationToken, Task>? AfterRun = null,
    IReadOnlyList<string>? Releases = null,
    Func<Func<CancellationToken, Task>, CancellationToken, Task>? Within = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
}

/// <summary>Finds the mount points of datasets for <see cref="CommandPlan.Releases"/>.</summary>
public static class PlanReleases
{
    /// <summary>The mount points of the named datasets (snapshot names are skipped), as the pools were last read.</summary>
    public static IReadOnlyList<string> MountPointsOf(PoolList pools, IEnumerable<string> datasets)
    {
        ArgumentNullException.ThrowIfNull(pools);
        var names = datasets.ToHashSet(StringComparer.Ordinal);
        return [.. pools.Datasets.Where(dataset => names.Contains(dataset.Name) && dataset.MountPoint is not null).Select(dataset => dataset.MountPoint!).Distinct(StringComparer.Ordinal)];
    }
}
