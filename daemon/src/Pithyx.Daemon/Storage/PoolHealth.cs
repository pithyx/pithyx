using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage;

/// <summary>
/// Adds to what ZFS reports what it has not noticed yet. Without ZED, ZFS marks a hard-removed disk only on its next
/// failing write, so an idle pool stays ONLINE while its disks are gone. A leaf disk whose device is gone is shown as
/// <c>REMOVED</c> and <see cref="Vdev.Missing"/>, its parents and the pool degrade the way ZFS would degrade them, and a
/// pool whose I/O is suspended says so. Pure: the caller passes what the disk inventory and <c>/proc</c> show.
/// </summary>
public static class PoolHealth
{
    public const string Suspended = "SUSPENDED";

    private static readonly HashSet<string> _working = new(StringComparer.Ordinal) { "ONLINE", "DEGRADED" };

    /// <param name="read">The pools as ZFS reported them.</param>
    /// <param name="present">True when a leaf's device path names a connected device.</param>
    /// <param name="suspended">Pools whose I/O ZFS suspended.</param>
    /// <param name="reconnecting">Leaf GUIDs that went missing and were not brought back yet; they stay missing even when their device is back.</param>
    /// <param name="serials">Serials of the disks the leaves were last seen on, by leaf GUID.</param>
    public static PoolList Apply(
        PoolList read,
        Func<string, bool> present,
        IReadOnlySet<string> suspended,
        IReadOnlySet<string> reconnecting,
        IReadOnlyDictionary<string, string> serials)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(present);
        var pools = read.Pools.Select(pool =>
        {
            var vdevs = pool.Vdevs.Select(vdev => Derive(vdev, present, reconnecting, serials)).ToList();
            var state = Worse(pool.State, PoolState(vdevs));
            if (suspended.Contains(pool.Name))
            {
                state = Suspended;
            }
            return pool with { State = state, Vdevs = vdevs };
        });
        return read with { Pools = [.. pools] };
    }

    /// <summary>Leaves whose device is gone from the machine now.</summary>
    public static IEnumerable<(Pool Pool, Vdev Leaf)> MissingLeaves(PoolList pools, Func<string, bool> present)
    {
        ArgumentNullException.ThrowIfNull(pools);
        return pools.Pools.SelectMany(pool => pool.Vdevs.SelectMany(vdev => vdev.Leaves()).Where(leaf => IsGone(leaf, present)).Select(leaf => (pool, leaf)));
    }

    /// <summary>
    /// What brings back disks that returned (D204): <c>zpool clear</c> resumes a suspended pool once none of its disks
    /// is missing, and <c>zpool online</c> reopens a returned leaf that ZFS marked <c>REMOVED</c> or that went missing
    /// while ZFS still held its old device. Only the leaf's own device path is opened, and ZFS checks its label, so
    /// nothing but the same disk can come back this way. Faulted and offline leaves are left to the user.
    /// </summary>
    /// <param name="read">The pools as ZFS reported them, without <see cref="Apply"/>.</param>
    /// <param name="present">True when a leaf's device path names a connected device.</param>
    /// <param name="suspended">Pools whose I/O ZFS suspended.</param>
    /// <param name="reconnecting">Leaf GUIDs that went missing and were not brought back yet.</param>
    public static IReadOnlyList<CommandRequest> Reconnects(PoolList read, Func<string, bool> present, IReadOnlySet<string> suspended, IReadOnlySet<string> reconnecting)
    {
        ArgumentNullException.ThrowIfNull(read);
        var commands = new List<CommandRequest>();
        foreach (var pool in read.Pools)
        {
            var leaves = pool.Vdevs.SelectMany(vdev => vdev.Leaves()).ToList();
            if (leaves.Any(leaf => IsGone(leaf, present)))
            {
                continue;
            }
            if (suspended.Contains(pool.Name))
            {
                commands.Add(Zfs.ZfsPlans.Resume(pool.Name));
            }
            foreach (var leaf in leaves)
            {
                var stale = reconnecting.Contains(leaf.VdevGuid) && _working.Contains(leaf.State);
                if ((leaf.State == "REMOVED" || stale) && leaf.Path is not null)
                {
                    commands.Add(Zfs.ZfsPlans.Reconnect(pool.Name, leaf.VdevGuid));
                }
            }
        }
        return commands;
    }

    /// <summary>Why a pool cannot take a command that writes to all its disks (destroy, export), or null when it can.</summary>
    public static string? Unreachable(Pool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (pool.State == Suspended)
        {
            return Errors.ErrorCodes.StoragePoolSuspended;
        }
        return pool.Vdevs.Any(vdev => IsData(vdev) && vdev.State is "UNAVAIL" or "REMOVED" or "FAULTED")
            ? Errors.ErrorCodes.StoragePoolDisksMissing
            : null;
    }

    private static bool IsGone(Vdev leaf, Func<string, bool> present) =>
        leaf.Children.Count == 0 && leaf.Type == "disk" && leaf.Path is { } path && !present(path);

    private static Vdev Derive(Vdev vdev, Func<string, bool> present, IReadOnlySet<string> reconnecting, IReadOnlyDictionary<string, string> serials)
    {
        if (vdev.Children.Count == 0)
        {
            var serial = serials.GetValueOrDefault(vdev.VdevGuid);
            var missing = IsGone(vdev, present) || (reconnecting.Contains(vdev.VdevGuid) && _working.Contains(vdev.State));
            return vdev with { State = missing && _working.Contains(vdev.State) ? "REMOVED" : vdev.State, Missing = missing, Serial = serial };
        }
        var children = vdev.Children.Select(child => Derive(child, present, reconnecting, serials)).ToList();
        var working = children.Count(child => _working.Contains(child.State));
        var lost = children.Count - working;
        var derived = vdev.Type switch
        {
            _ when lost == 0 => "ONLINE",
            "raidz" or "draid" => lost > (vdev.ParityDisks ?? 1) ? "UNAVAIL" : "DEGRADED",
            // Mirrors, spares and replacements work while one child does.
            _ => working == 0 ? "UNAVAIL" : "DEGRADED",
        };
        return vdev with { State = Worse(vdev.State, derived), Children = children };
    }

    /// <summary>A lost data, special, dedup or log vdev takes the pool down with it; cache and spares never do.</summary>
    private static string PoolState(IReadOnlyList<Vdev> vdevs)
    {
        var relevant = vdevs.Where(vdev => vdev.Role is not ("cache" or "spare")).ToList();
        if (relevant.Any(vdev => IsData(vdev) && !_working.Contains(vdev.State)))
        {
            return "UNAVAIL";
        }
        return relevant.All(vdev => vdev.State == "ONLINE") ? "ONLINE" : "DEGRADED";
    }

    private static bool IsData(Vdev vdev) => vdev.Role is "data" or "special" or "dedup";

    private static int Rank(string state) => state switch
    {
        "ONLINE" => 0,
        "DEGRADED" => 1,
        Suspended => 3,
        _ => 2,
    };

    private static string Worse(string reported, string derived) => Rank(derived) > Rank(reported) ? derived : reported;
}
