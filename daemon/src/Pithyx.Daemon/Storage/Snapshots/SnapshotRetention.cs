namespace Pithyx.Daemon.Storage.Snapshots;

/// <summary>
/// Decides which snapshots of one schedule to delete. Pure: the same snapshots, count and clock give the same answer.
/// <para>
/// Only snapshots Pithyx created for this schedule are candidates (D104): their name is exactly
/// <c>pithyx-&lt;kind&gt;-&lt;utc&gt;</c>. Snapshots of users, other tools, other schedules and manual ones are never
/// touched, and neither are held ones. The newest <c>keep</c> candidates by creation stay.
/// </para>
/// <para>
/// The clock may be wrong. A snapshot dated after <c>now</c> (the clock went back, or was ahead when it was taken) is
/// never deleted and does not count towards <c>keep</c>, so a backwards jump neither deletes it nor makes the
/// schedule delete every new snapshot right after taking it. When <c>now</c> lies before every snapshot, nothing is
/// deleted. At least one candidate always stays. Retention counts snapshots rather than measuring age, so a box that
/// was off for months keeps its last snapshots instead of finding them all expired.
/// </para>
/// </summary>
public static class SnapshotRetention
{
    /// <param name="snapshots">Snapshots of one dataset; others are ignored by kind, not by dataset.</param>
    /// <param name="kind">The schedule: hourly, daily, weekly or monthly.</param>
    /// <param name="keep">How many to keep; values below 1 count as 1.</param>
    /// <param name="now">The clock now, which may be wrong.</param>
    /// <returns>The snapshots to destroy, oldest first.</returns>
    public static IReadOnlyList<Snapshot> Prune(IEnumerable<Snapshot> snapshots, string kind, int keep, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (!SnapshotKinds.Scheduled.Contains(kind))
        {
            return [];
        }
        var countable = snapshots
            .Where(snapshot => snapshot.PithyxKind == kind && snapshot.Holds == 0 && snapshot.CreatedAt <= now)
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ThenByDescending(snapshot => snapshot.Name, StringComparer.Ordinal)
            .ToList();
        return [.. countable.Skip(Math.Max(keep, 1)).Reverse()];
    }
}
