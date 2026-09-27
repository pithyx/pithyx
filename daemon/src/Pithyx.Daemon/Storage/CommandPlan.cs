using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage;

/// <param name="Command">The exact command, run without a shell.</param>
/// <param name="DiskIds">The disks it writes to; the guard checks them right before it runs.</param>
public sealed record PlanStep(CommandRequest Command, IReadOnlyList<string> DiskIds);

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
public sealed record CommandPlan(
    string Kind,
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<PlannedDisk> Disks,
    IReadOnlyDictionary<string, string> Confirmations,
    DateTimeOffset ExpiresAt)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
}
