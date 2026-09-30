using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Storage;

/// <summary>Where the mount table, swap list and ZFS pool states are read from; simulation mode reads recorded copies.</summary>
/// <param name="MountInfo">The mount table.</param>
/// <param name="Swaps">Active swap.</param>
/// <param name="ZfsKstat">Folder with one folder per imported pool whose <c>state</c> file says for example <c>SUSPENDED</c>.</param>
public sealed record SystemFiles(string MountInfo, string Swaps, string ZfsKstat = "/proc/spl/kstat/zfs")
{
    public static SystemFiles Host { get; } = new("/proc/self/mountinfo", "/proc/swaps");
}

/// <summary>Collects what <see cref="DiskGuard"/> decides on from memory and <c>/proc</c>; starts no process.</summary>
public sealed partial class StorageFacts(DiskInventory disks, PoolInventory pools, DeploymentEnvironment environment, SystemFiles files)
{
    public DeploymentEnvironment Environment => environment;

    public GuardFacts Current()
    {
        var devices = disks.Devices;
        var guardDisks = disks.Current.Disks
            .Where(disk => devices.ContainsKey(disk.Id))
            .Select(disk => new GuardDisk(disk, devices[disk.Id], disks.SignaturesOf(disk.Id)))
            .ToList();
        return new GuardFacts(
            environment,
            guardDisks,
            // Without the mount table the system disk is unknown; the read fails rather than guessing.
            MountTable.ParseMountInfo(File.ReadAllText(files.MountInfo)),
            MountTable.ParseSwaps(ReadOrEmpty(files.Swaps)),
            PoolMembers(pools.Current));
    }

    /// <summary>Leaf vdev paths of every pool, each also resolved from its <c>/dev/disk/by-id</c> link.</summary>
    public static IReadOnlyDictionary<string, string> PoolMembers(PoolList pools)
    {
        ArgumentNullException.ThrowIfNull(pools);
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pool in pools.Pools)
        {
            foreach (var leaf in pool.Vdevs.SelectMany(vdev => vdev.Leaves()))
            {
                if (leaf.Path is not { } path)
                {
                    continue;
                }
                members[path] = pool.Name;
                // ZFS partitions a whole disk it is given (part1 and part9); its by-id link without the suffix names the disk.
                if (path.StartsWith("/dev/disk/by-id/", StringComparison.Ordinal) && PartitionSuffix().Replace(path, "") is var disk && disk != path)
                {
                    members[disk] = pool.Name;
                }
                if (Resolve(path) is { } target)
                {
                    members[target] = pool.Name;
                }
            }
        }
        return members;
    }

    /// <summary>
    /// Whether a leaf vdev's device path names a device of the disk inventory: by node, by its by-id link, by the whole
    /// disk of a by-id partition link, or by the node another by-id link resolves to. Once udev removed a disk, none holds.
    /// </summary>
    public Func<string, bool> DevicePresence()
    {
        var devices = disks.Devices.Values.SelectMany(device => device.SelfAndDescendants()).ToList();
        return path =>
        {
            var disk = path.StartsWith("/dev/disk/by-id/", StringComparison.Ordinal) ? PartitionSuffix().Replace(path, "") : path;
            return devices.Any(device => device.IsNamed(path) || device.IsNamed(disk))
                || (Resolve(path) is { } target && devices.Any(device => device.Path == target));
        };
    }

    /// <summary>The serial of the disk a leaf vdev's path names, if it is connected.</summary>
    public string? SerialOf(string path)
    {
        var disk = path.StartsWith("/dev/disk/by-id/", StringComparison.Ordinal) ? PartitionSuffix().Replace(path, "") : path;
        var devices = disks.Devices;
        return disks.Current.Disks
            .FirstOrDefault(candidate => devices.TryGetValue(candidate.Id, out var device) && device.SelfAndDescendants().Any(node => node.IsNamed(path) || node.IsNamed(disk)))
            ?.Serial;
    }

    /// <summary>
    /// The pools whose I/O ZFS suspended, from the kernel's statistics files. Reading them never blocks, also when every
    /// <c>zpool</c> command hangs behind a suspended pool.
    /// </summary>
    public IReadOnlySet<string> SuspendedPools(IEnumerable<string> poolNames) =>
        poolNames.Where(name => ReadOrEmpty(Path.Combine(files.ZfsKstat, name, "state")).Trim() == PoolHealth.Suspended).ToHashSet(StringComparer.Ordinal);

    /// <summary>The ids of disks that belong to an imported pool; those are never probed.</summary>
    public IReadOnlySet<string> PoolDiskIds()
    {
        var members = PoolMembers(pools.Current);
        return disks.Devices
            .Where(entry => entry.Value.SelfAndDescendants().Any(device => members.Keys.Any(device.IsNamed)))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? Resolve(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"-part\d+$")]
    private static partial System.Text.RegularExpressions.Regex PartitionSuffix();
}
