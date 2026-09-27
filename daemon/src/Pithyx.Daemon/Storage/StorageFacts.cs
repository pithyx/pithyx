using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Storage;

/// <summary>Where the mount table and swap list are read from; simulation mode reads recorded copies.</summary>
public sealed record SystemFiles(string MountInfo, string Swaps)
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
