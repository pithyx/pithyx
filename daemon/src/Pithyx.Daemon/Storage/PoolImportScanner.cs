using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage;

/// <summary>
/// Finds pools that could be imported and says for each whether the disk guard lets it be imported. Imports nothing:
/// that is always a plan (<c>pool.import.readonly</c>, then <c>pool.import</c>).
/// </summary>
public sealed class PoolImportScanner(ICommandRunner commands, SystemStateService state, StorageFacts facts, PoolInventory pools)
{
    /// <summary>Device states of a device that is missing; such a device is on no disk the daemon could check.</summary>
    private static readonly HashSet<string> _missingStates = ["UNAVAIL", "REMOVED"];

    /// <summary>A missing device: ZFS names one it cannot find by its GUID, whatever state it gives it.</summary>
    private static bool IsMissing(string name, string? state) =>
        (state is not null && _missingStates.Contains(state)) || (name.Length > 0 && name.All(char.IsAsciiDigit));

    public async Task<ImportScan> ScanAsync(CancellationToken cancellationToken)
    {
        // Disks and pools are read first, so a disk that just appeared is known and a pool that was just imported is not listed as a member of none.
        await state.RefreshPoolsAsync(cancellationToken);
        await state.RefreshDisksAsync([], cancellationToken);
        var result = await commands.RunAsync(ImportPlans.Scan, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.StorageCommandFailed, new Dictionary<string, object?> { ["command"] = "zpool", ["exitCode"] = result.ExitCode });
        }
        return Evaluate(ZpoolImportParser.Parse(result.StandardOutput), facts.Current(), pools.Current);
    }

    /// <summary>Maps every device to a disk and asks the guard's import rule about those disks. A pure function.</summary>
    public static ImportScan Evaluate(IReadOnlyList<ImportCandidate> candidates, GuardFacts facts, PoolList imported)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(imported);
        var verdicts = DiskGuard.EvaluateImport(facts, null).ToDictionary(verdict => verdict.DiskId, StringComparer.Ordinal);
        return new ImportScan([.. candidates.Select(candidate =>
        {
            var vdevs = candidate.Vdevs.Select(vdev => Map(vdev, facts)).ToList();
            var leaves = vdevs.SelectMany(vdev => vdev.Leaves()).ToList();
            var diskIds = leaves.Select(leaf => leaf.DiskId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var unknown = leaves.Where(leaf => leaf.DiskId is null && !IsMissing(leaf.Name, leaf.State)).Select(leaf => leaf.Name).ToList();
            var disks = diskIds.Select(id => verdicts[id]).ToList();
            return new ImportablePool(
                candidate.Name,
                candidate.PoolGuid,
                candidate.State,
                candidate.Status,
                candidate.Action,
                candidate.MessageId,
                candidate.CanImport,
                candidate.NeedsForce,
                imported.Pools.Any(pool => pool.Name == candidate.Name),
                vdevs,
                disks,
                unknown,
                candidate.CanImport && unknown.Count == 0 && disks.Count > 0 && disks.All(verdict => verdict.Decision == GuardDecision.Allowed));
        })]);
    }

    /// <summary>
    /// The disk a device name or path is on: the disk itself or the disk of one of its partitions (or of anything stacked
    /// on it). Names without a slash are looked up in <see cref="ImportPlans.SearchDirectory"/> and in <c>/dev</c>.
    /// </summary>
    public static string? DiskOf(GuardFacts facts, string device)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(device);
        string[] paths = device.StartsWith('/') ? [device] : [$"{ImportPlans.SearchDirectory}/{device}", $"/dev/{device}"];
        return facts.Disks
            .FirstOrDefault(disk => disk.Device.SelfAndDescendants().Any(candidate => paths.Any(candidate.IsNamed)))
            ?.Disk.Id;
    }

    /// <summary>The disks of an imported pool, and the paths of its present devices that are on no known disk.</summary>
    public static (IReadOnlyList<string> DiskIds, IReadOnlyList<string> Unknown) DisksOf(GuardFacts facts, Pool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var leaves = pool.Vdevs.SelectMany(vdev => vdev.Leaves()).Where(leaf => leaf.Children.Count == 0).ToList();
        var mapped = leaves.Select(leaf => (Leaf: leaf, DiskId: leaf.Path is { } path ? DiskOf(facts, path) : null)).ToList();
        return (
            [.. mapped.Select(entry => entry.DiskId).OfType<string>().Distinct(StringComparer.Ordinal)],
            [.. mapped.Where(entry => entry.DiskId is null && !IsMissing(entry.Leaf.Name, entry.Leaf.State)).Select(entry => entry.Leaf.Path ?? entry.Leaf.Name)]);
    }

    private static ImportVdev Map(ImportVdev vdev, GuardFacts facts) =>
        vdev with
        {
            DiskId = vdev.Children.Count == 0 ? DiskOf(facts, vdev.Name) : null,
            Children = [.. vdev.Children.Select(child => Map(child, facts))],
        };
}
