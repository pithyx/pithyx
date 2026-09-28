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
        ["mirrors"] = 4,
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
        if (diskCount < minimum || (layout == "single" && diskCount != 1) || (layout == "mirrors" && diskCount % 2 != 0))
        {
            throw new PithyxException(
                ErrorCodes.StoragePoolDiskCount,
                new Dictionary<string, object?> { ["layout"] = layout, ["min"] = minimum, ["count"] = diskCount });
        }
    }

    /// <summary>
    /// The steps to create a pool with the Pithyx dataset layout. Disks that are not empty (confirmed by serial) are
    /// cleared first. Devices are named by their <c>/dev/disk/by-id</c> links, which survive a change of kernel names.
    /// </summary>
    public static IReadOnlyList<PlanStep> CreatePool(string name, string layout, IReadOnlyList<GuardDisk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var steps = disks.SelectMany(Clear).ToList();
        var paths = disks.Select(disk => StablePath(disk.Device)).ToList();
        var vdevs = layout switch
        {
            "single" or "stripe" => paths.AsEnumerable(),
            // Pairs in the order chosen: mirror a b mirror c d.
            "mirrors" => paths.Chunk(2).SelectMany(pair => pair.Prepend("mirror")),
            _ => paths.Prepend(layout),
        };
        steps.Add(new PlanStep(
            new CommandRequest("zpool", ["create", .. CreateOptions, name, .. vdevs], TimeSpan.FromMinutes(5)),
            [.. disks.Select(disk => disk.Disk.Id)]));
        steps.AddRange(Datasets(name).Select(arguments => new PlanStep(new CommandRequest("zfs", ["create", .. arguments]), [])));
        return steps;
    }

    /// <summary>
    /// The dataset layout of D100 below a new pool's root, which holds no data: everything Pithyx stores lives in
    /// <c>&lt;pool&gt;/pithyx</c>, the encryption root of step 9 (D106). Shares mount at <c>/&lt;pool&gt;/&lt;share&gt;</c>,
    /// homes at <c>/&lt;pool&gt;/homes/&lt;user&gt;</c>, apps and system data below <c>/&lt;pool&gt;/.pithyx</c>.
    /// </summary>
    public static IReadOnlyList<string[]> Datasets(string pool) =>
    [
        ["-o", "canmount=off", "-o", "mountpoint=none", $"{pool}/pithyx"],
        ["-o", "canmount=off", "-o", $"mountpoint=/{pool}", $"{pool}/pithyx/shares"],
        ["-o", $"mountpoint=/{pool}/homes", $"{pool}/pithyx/homes"],
        ["-o", "canmount=off", "-o", $"mountpoint=/{pool}/.pithyx/apps", $"{pool}/pithyx/apps"],
        ["-o", $"mountpoint=/{pool}/.pithyx/system", $"{pool}/pithyx/system"],
    ];

    /// <summary>Replaces a device (named by its GUID, so a missing one works too) with a new disk; ZFS resilvers onto it.</summary>
    public static IReadOnlyList<PlanStep> Replace(string pool, string vdevGuid, GuardDisk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return [.. Clear(disk), new PlanStep(new CommandRequest("zpool", ["replace", pool, vdevGuid, StablePath(disk.Device)]), [disk.Disk.Id])];
    }

    /// <summary>Adds a hot spare that ZFS uses when a device fails.</summary>
    public static IReadOnlyList<PlanStep> AddSpare(string pool, GuardDisk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return [.. Clear(disk), new PlanStep(new CommandRequest("zpool", ["add", pool, "spare", StablePath(disk.Device)]), [disk.Disk.Id])];
    }

    public static IReadOnlyList<PlanStep> RemoveSpare(string pool, string vdevGuid) =>
        [new PlanStep(new CommandRequest("zpool", ["remove", pool, vdevGuid]), [])];

    public static IReadOnlyList<PlanStep> Offline(string pool, string vdevGuid) =>
        [new PlanStep(new CommandRequest("zpool", ["offline", pool, vdevGuid]), [])];

    /// <summary>Brings a device back; ZFS resilvers what it missed on its own.</summary>
    public static IReadOnlyList<PlanStep> Online(string pool, string vdevGuid) =>
        [new PlanStep(new CommandRequest("zpool", ["online", pool, vdevGuid]), [])];

    /// <summary>A dataset for a share, mounted at <c>/&lt;pool&gt;/&lt;name&gt;</c> through the <c>shares</c> dataset.</summary>
    public static IReadOnlyList<PlanStep> CreateShareDataset(string pool, string name)
    {
        if (!DatasetName().IsMatch(name))
        {
            throw new PithyxException(ErrorCodes.StorageDatasetNameInvalid, new Dictionary<string, object?> { ["name"] = name });
        }
        return [new PlanStep(new CommandRequest("zfs", ["create", $"{pool}/pithyx/shares/{name}"]), [])];
    }

    /// <summary>Destroys a pool; never forced, so ZFS refuses while a dataset is busy.</summary>
    public static IReadOnlyList<PlanStep> DestroyPool(string pool) =>
        [new PlanStep(new CommandRequest("zpool", ["destroy", pool], TimeSpan.FromMinutes(5)), [])];

    /// <summary>
    /// Clears a disk that is not empty (the user confirmed it by serial), the way <c>tools/dev-vm/testpool.sh wipe</c>
    /// does: ZFS labels, then the signatures of every partition, then the disk.
    /// </summary>
    private static IEnumerable<PlanStep> Clear(GuardDisk disk)
    {
        var id = new[] { disk.Disk.Id };
        var descendants = disk.Device.SelfAndDescendants().Skip(1).ToList();
        foreach (var member in descendants.Where(device => device.FileSystem == "zfs_member"))
        {
            yield return new PlanStep(new CommandRequest("zpool", ["labelclear", "-f", StablePath(member)]), id);
        }
        if (disk.Device.FileSystem == "zfs_member" || disk.Signatures?.Types.Contains("zfs_member") == true)
        {
            yield return new PlanStep(new CommandRequest("zpool", ["labelclear", "-f", StablePath(disk.Device)]), id);
        }
        if (descendants.Count > 0 || disk.Device.FileSystem is not null || disk.Signatures is not { Types.Count: 0 })
        {
            yield return new PlanStep(new CommandRequest("wipefs", ["--all", .. descendants.Where(d => d.Type == "part").Select(StablePath), StablePath(disk.Device)]), id);
        }
    }

    /// <summary>One component of a dataset name that Pithyx creates or renames to.</summary>
    public static bool IsDatasetName(string name) => name is not null && DatasetName().IsMatch(name);

    private static string StablePath(BlockDevice device) => device.IdLink is { } link ? $"/dev/disk/by-id/{link}" : device.Path;

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex PoolName();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex DatasetName();
}
