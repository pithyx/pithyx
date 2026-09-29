using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage.Snapshots;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>
/// Builds the ZFS commands for snapshots. Only this class and <see cref="ZfsPlans"/> name commands that change a
/// pool. Every command names exactly one snapshot; destroy never gets <c>-r</c> or a range, and rollback gets
/// <c>-r</c> only when the caller confirmed the newer snapshots it discards.
/// </summary>
public static class SnapshotPlans
{
    /// <summary>The tag of the holds Pithyx places and releases.</summary>
    public const string HoldTag = "pithyx";

    public static readonly CommandRequest List = new("zfs", ["list", "-j", "--json-int", "-t", "snapshot", "-o", "name,used,referenced,creation,userrefs"]);

    public static CommandRequest Take(string dataset, string name) => new CommandRequest("zfs", ["snapshot", Full(dataset, name)]);

    /// <summary>Destroys one snapshot Pithyx created. Any other name is refused before a command exists.</summary>
    public static CommandRequest Destroy(string dataset, string name)
    {
        if (SnapshotNames.KindOf(name) is null)
        {
            throw new ArgumentException($"Pithyx destroys only snapshots it created, not {name}.", nameof(name));
        }
        return new CommandRequest("zfs", ["destroy", Full(dataset, name)]);
    }

    public static CommandRequest Hold(string dataset, string name) => new CommandRequest("zfs", ["hold", HoldTag, Full(dataset, name)]);

    public static CommandRequest Release(string dataset, string name) => new CommandRequest("zfs", ["release", HoldTag, Full(dataset, name)]);

    /// <param name="dataset">The dataset to roll back.</param>
    /// <param name="name">The snapshot to return to.</param>
    /// <param name="discardNewer">True only after the user saw and confirmed every newer snapshot; ZFS destroys them.</param>
    public static CommandRequest Rollback(string dataset, string name, bool discardNewer)
    {
        var target = Full(dataset, name);
        return discardNewer
            ? new CommandRequest("zfs", ["rollback", "-r", target], TimeSpan.FromMinutes(10))
            : new CommandRequest("zfs", ["rollback", target], TimeSpan.FromMinutes(10));
    }

    private static string Full(string dataset, string name)
    {
        // A name with @, a range (%) or a comma list would reach more than one snapshot.
        if (string.IsNullOrEmpty(dataset) || string.IsNullOrEmpty(name) || dataset.Contains('@', StringComparison.Ordinal)
            || dataset.StartsWith('-') || name.IndexOfAny(['@', '%', ',', '/', ' ']) >= 0)
        {
            throw new ArgumentException($"Not a single snapshot: {dataset}@{name}.", nameof(name));
        }
        return $"{dataset}@{name}";
    }
}
