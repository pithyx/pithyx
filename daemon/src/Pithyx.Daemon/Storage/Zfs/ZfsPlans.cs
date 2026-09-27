using System.Text.RegularExpressions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>Builds the commands of ZFS plans. Only this class names destructive ZFS commands.</summary>
public static partial class ZfsPlans
{
    /// <summary>Layouts with the number of disks they need: <c>single</c> exactly one, the others at least the minimum.</summary>
    public static readonly IReadOnlyDictionary<string, int> MinimumDisks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["single"] = 1,
        ["stripe"] = 2,
        ["mirror"] = 2,
        ["raidz1"] = 3,
        ["raidz2"] = 4,
        ["raidz3"] = 5,
    };

    /// <summary>Pool properties (<c>-o</c>) and root dataset properties (<c>-O</c>) every new pool gets.</summary>
    public static readonly IReadOnlyList<string> CreateOptions =
    [
        "-o", "ashift=12",
        "-O", "compression=lz4",
        "-O", "xattr=sa",
        "-O", "acltype=posixacl",
        "-O", "atime=off",
        "-O", "normalization=formD",
        // The pool root holds no data (D100); Pithyx datasets live below <pool>/pithyx.
        "-O", "canmount=off",
    ];

    private static readonly HashSet<string> _reservedNames = ["mirror", "raidz", "raidz1", "raidz2", "raidz3", "draid", "spare", "log", "cache", "special", "dedup", "pithyx"];

    public static void ValidateName(string name, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!PoolName().IsMatch(name) || _reservedNames.Contains(name) || name.StartsWith("c0", StringComparison.Ordinal))
        {
            throw new PithyxException(ErrorCodes.StoragePoolNameInvalid, new Dictionary<string, object?> { ["name"] = name });
        }
        if (existing.Contains(name, StringComparer.Ordinal))
        {
            throw new PithyxException(ErrorCodes.StoragePoolNameTaken, new Dictionary<string, object?> { ["name"] = name });
        }
    }

    public static void ValidateLayout(string layout, int diskCount)
    {
        if (!MinimumDisks.TryGetValue(layout, out var minimum))
        {
            throw new PithyxException(ErrorCodes.StoragePoolLayoutInvalid, new Dictionary<string, object?> { ["layout"] = layout });
        }
        if (diskCount < minimum || (layout == "single" && diskCount != 1))
        {
            throw new PithyxException(
                ErrorCodes.StoragePoolDiskCount,
                new Dictionary<string, object?> { ["layout"] = layout, ["min"] = minimum, ["count"] = diskCount });
        }
    }

    /// <summary>
    /// The steps to create a pool. Disks that are not empty (confirmed by serial) are cleared first, the way
    /// <c>tools/dev-vm/testpool.sh wipe</c> does it: ZFS labels, then the signatures of every partition, then the disk.
    /// Devices are named by their <c>/dev/disk/by-id</c> links, which survive a change of kernel names.
    /// </summary>
    public static IReadOnlyList<PlanStep> CreatePool(string name, string layout, IReadOnlyList<GuardDisk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var steps = new List<PlanStep>();
        foreach (var disk in disks)
        {
            var id = new[] { disk.Disk.Id };
            var descendants = disk.Device.SelfAndDescendants().Skip(1).ToList();
            foreach (var member in descendants.Where(device => device.FileSystem == "zfs_member"))
            {
                steps.Add(new PlanStep(new CommandRequest("zpool", ["labelclear", "-f", StablePath(member)]), id));
            }
            if (disk.Device.FileSystem == "zfs_member" || disk.Signatures?.Types.Contains("zfs_member") == true)
            {
                steps.Add(new PlanStep(new CommandRequest("zpool", ["labelclear", "-f", StablePath(disk.Device)]), id));
            }
            var hasSignatures = descendants.Count > 0 || disk.Device.FileSystem is not null || disk.Signatures is not { Types.Count: 0 };
            if (hasSignatures)
            {
                steps.Add(new PlanStep(new CommandRequest("wipefs", ["--all", .. descendants.Where(d => d.Type == "part").Select(StablePath), StablePath(disk.Device)]), id));
            }
        }
        string[] vdevType = layout switch
        {
            "single" or "stripe" => [],
            _ => [layout],
        };
        steps.Add(new PlanStep(
            new CommandRequest("zpool", ["create", .. CreateOptions, name, .. vdevType, .. disks.Select(disk => StablePath(disk.Device))], TimeSpan.FromMinutes(5)),
            [.. disks.Select(disk => disk.Disk.Id)]));
        return steps;
    }

    private static string StablePath(BlockDevice device) => device.IdLink is { } link ? $"/dev/disk/by-id/{link}" : device.Path;

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex PoolName();
}
