using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Storage;

public enum GuardDecision
{
    Allowed,
    NeedsSerialConfirmation,
    Refused,
}

/// <param name="DiskId">The disk the verdict is for.</param>
/// <param name="Decision">Whether a destructive command may touch the disk.</param>
/// <param name="Reason">
/// Why, as a stable code: <c>system_disk</c>, <c>read_only</c>, <c>mounted</c>, <c>pool_member</c>, <c>not_test_disk</c>,
/// <c>duplicate_serial</c>, <c>no_serial</c>, <c>not_checked</c>, <c>has_data</c>, <c>confirmed</c> or <c>empty</c>; when a
/// pool is imported, <c>importable</c> for an allowed disk.
/// </param>
/// <param name="Pool">The imported pool the disk belongs to, for <c>pool_member</c>.</param>
/// <param name="Contents">What was found on the disk: partitions and signature types.</param>
public sealed record GuardVerdict(string DiskId, GuardDecision Decision, string Reason, string? Pool, IReadOnlyList<string> Contents);

/// <param name="Disk">The disk as the API shows it.</param>
/// <param name="Device">Its device tree from lsblk.</param>
/// <param name="Signatures">What the last probe found, or null if the disk was not probed.</param>
public sealed record GuardDisk(Disk Disk, BlockDevice Device, DiskSignatures? Signatures);

/// <param name="Environment">dev or prod, and the test disks of a dev machine.</param>
/// <param name="Disks">Every disk with its device tree.</param>
/// <param name="Mounts">The mount table.</param>
/// <param name="SwapDevices">Active swap devices.</param>
/// <param name="PoolMembers">Device path of every leaf vdev of every imported pool, also resolved from its by-id link, to the pool's name.</param>
public sealed record GuardFacts(
    DeploymentEnvironment Environment,
    IReadOnlyList<GuardDisk> Disks,
    IReadOnlyList<MountEntry> Mounts,
    IReadOnlyList<string> SwapDevices,
    IReadOnlyDictionary<string, string> PoolMembers);

/// <summary>
/// Decides for every disk whether a destructive command may touch it. A pure function of what the daemon knows, so it
/// can be checked with generated trees, and run again right before every command of a plan.
/// </summary>
public static class DiskGuard
{
    /// <summary>A disk holding any of these, directly or through a partition, LVM, crypt, md or a pool, is the system disk.</summary>
    public static readonly IReadOnlyList<string> SystemPaths = ["/", "/boot", "/boot/efi", "/var/lib/pithyx"];

    public static IReadOnlyList<GuardVerdict> Evaluate(GuardFacts facts, IReadOnlyDictionary<string, string>? confirmations = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (MountTable.Containing(facts.Mounts, "/") is null)
        {
            // No root mount means the mount table is unusable, so any disk could be the system disk.
            return [.. facts.Disks.Select(disk => new GuardVerdict(disk.Disk.Id, GuardDecision.Refused, "system_disk", null, []))];
        }
        var systemDisks = SystemDisks(facts);
        var serialCounts = facts.Disks
            .Where(disk => disk.Disk.Serial is not null)
            .GroupBy(disk => disk.Disk.Serial!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return [.. facts.Disks.Select(disk => Judge(facts, disk, systemDisks, serialCounts, confirmations?.GetValueOrDefault(disk.Disk.Id)))];
    }

    private static GuardVerdict Judge(
        GuardFacts facts,
        GuardDisk disk,
        HashSet<string> systemDisks,
        Dictionary<string, int> serialCounts,
        string? confirmation)
    {
        var devices = disk.Device.SelfAndDescendants().ToList();
        var contents = devices.Skip(1).Select(device => device.Name)
            .Concat(devices.Select(device => device.FileSystem).OfType<string>())
            .Concat(disk.Signatures?.Types ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var pool = devices.SelectMany(device => PoolOf(facts, device)).FirstOrDefault();
        var serial = disk.Disk.Serial;

        GuardVerdict Verdict(GuardDecision decision, string reason) => new(disk.Disk.Id, decision, reason, pool, contents);

        if (systemDisks.Contains(disk.Disk.Id))
        {
            return Verdict(GuardDecision.Refused, "system_disk");
        }
        if (disk.Disk.ReadOnly)
        {
            return Verdict(GuardDecision.Refused, "read_only");
        }
        if (devices.Any(device => device.MountPoints.Count > 0))
        {
            return Verdict(GuardDecision.Refused, "mounted");
        }
        if (pool is not null)
        {
            return Verdict(GuardDecision.Refused, "pool_member");
        }
        if (facts.Environment.Kind == DeploymentKind.Dev && (serial is null || !facts.Environment.TestDiskSerials.Contains(serial)))
        {
            return Verdict(GuardDecision.Refused, "not_test_disk");
        }
        if (serial is not null && serialCounts[serial] > 1)
        {
            return Verdict(GuardDecision.Refused, "duplicate_serial");
        }
        var empty = disk.Signatures is { Types.Count: 0 } && contents.Count == 0;
        if (empty)
        {
            return Verdict(GuardDecision.Allowed, "empty");
        }
        if (serial is null)
        {
            return Verdict(GuardDecision.Refused, "no_serial");
        }
        if (confirmation is not null && string.Equals(confirmation.Trim(), serial, StringComparison.Ordinal))
        {
            return Verdict(GuardDecision.Allowed, "confirmed");
        }
        return Verdict(GuardDecision.NeedsSerialConfirmation, disk.Signatures is null ? "not_checked" : "has_data");
    }

    /// <summary>
    /// The rule for importing a pool and for every command of an import plan. A pool's disks carry ZFS labels, so data on
    /// them is expected and no serial is typed. Still refused: the system disk, read-only and mounted disks, disks of an
    /// imported pool other than <paramref name="importingPool"/> (the pool this plan exports and imports again), on a dev
    /// machine every disk that is not a declared test disk, and disks whose serial is not unique.
    /// </summary>
    /// <param name="facts">What the daemon knows.</param>
    /// <param name="importingPool">The imported pool whose disks the plan may touch, or null when none may be imported.</param>
    public static IReadOnlyList<GuardVerdict> EvaluateImport(GuardFacts facts, string? importingPool)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return [.. Evaluate(facts).Select(verdict =>
        {
            var disk = facts.Disks.First(candidate => candidate.Disk.Id == verdict.DiskId);
            GuardVerdict Verdict(GuardDecision decision, string reason) => verdict with { Decision = decision, Reason = reason };
            switch (verdict.Reason)
            {
                case "system_disk" or "read_only" or "mounted" or "not_test_disk" or "duplicate_serial":
                    return verdict;
                case "pool_member":
                    // A disk that belongs to the importing pool and to no other one; the dev rule still applies to it.
                    var pools = disk.Device.SelfAndDescendants().SelectMany(device => PoolOf(facts, device)).Distinct(StringComparer.Ordinal).ToList();
                    if (importingPool is null || pools.Any(pool => pool != importingPool))
                    {
                        return verdict;
                    }
                    return facts.Environment.Kind == DeploymentKind.Dev && (disk.Disk.Serial is not { } serial || !facts.Environment.TestDiskSerials.Contains(serial))
                        ? Verdict(GuardDecision.Refused, "not_test_disk")
                        : IsDuplicate(facts, disk) ? Verdict(GuardDecision.Refused, "duplicate_serial") : Verdict(GuardDecision.Allowed, "importable");
                default:
                    // empty, has_data, not_checked, no_serial or confirmed: nothing that makes an import unsafe.
                    return Verdict(GuardDecision.Allowed, "importable");
            }
        })];
    }

    private static bool IsDuplicate(GuardFacts facts, GuardDisk disk) =>
        disk.Disk.Serial is { } serial && facts.Disks.Count(other => other.Disk.Serial == serial) > 1;

    /// <summary>Disks that hold a system path or swap, including the disks of a pool with a system dataset.</summary>
    private static HashSet<string> SystemDisks(GuardFacts facts)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var systemMounts = SystemPaths.Select(path => MountTable.Containing(facts.Mounts, path)).OfType<MountEntry>().Distinct().ToList();
        var systemPools = systemMounts
            .Where(mount => mount.FileSystem == "zfs")
            .Select(mount => mount.Source.Split('/')[0])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var disk in facts.Disks)
        {
            foreach (var device in disk.Device.SelfAndDescendants())
            {
                var holdsMount = systemMounts.Any(mount =>
                    mount.FileSystem != "zfs" && (device.IsNamed(mount.Source) || (!mount.MajorMinor.StartsWith("0:", StringComparison.Ordinal) && mount.MajorMinor == device.MajorMinor)));
                var holdsSwap = device.MountPoints.Contains("[SWAP]") || facts.SwapDevices.Any(device.IsNamed);
                var holdsSystemPool = PoolOf(facts, device).Any(systemPools.Contains);
                if (holdsMount || holdsSwap || holdsSystemPool)
                {
                    result.Add(disk.Disk.Id);
                    break;
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> PoolOf(GuardFacts facts, BlockDevice device) =>
        facts.PoolMembers.Where(member => device.IsNamed(member.Key)).Select(member => member.Value);

    /// <summary>
    /// What a plan compares right before each command: if serial, size, partitions or signatures changed since the plan
    /// was shown, the disk is not the one the user saw.
    /// </summary>
    /// <param name="disk">The disk as it is now.</param>
    /// <param name="rule">
    /// For <see cref="GuardRule.Import"/> the probed signatures are left out: the disks of a pool are expected to carry
    /// ZFS labels, and they are probed only while their pool is not imported, so the export step of an import changes
    /// what a probe shows without the disk changing.
    /// </param>
    public static string Fingerprint(GuardDisk disk, GuardRule rule = GuardRule.NewData)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var devices = disk.Device.SelfAndDescendants().Select(device => $"{device.Name}:{device.Type}:{device.FileSystem}");
        var signatures = rule == GuardRule.Import ? [] : disk.Signatures?.Types ?? ["unprobed"];
        return string.Join('|', [disk.Disk.Serial ?? "", disk.Disk.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), .. devices, .. signatures]);
    }
}
