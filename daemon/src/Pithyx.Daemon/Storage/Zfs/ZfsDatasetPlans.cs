using System.Globalization;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>
/// Builds the commands that rename, change, destroy and create datasets and volumes. Like <see cref="ZfsPlans"/> it
/// decides nothing about which dataset may be changed (that is <see cref="DatasetPlanner"/>); it only makes sure every
/// command names exactly the dataset it was given, never forces and never reaches clones elsewhere.
/// </summary>
public static class ZfsDatasetPlans
{
    /// <summary>Block size of every new volume; OpenZFS 2.2 made 16 KiB its default as well.</summary>
    public const long VolumeBlockSize = 16 * 1024;

    public const long MinimumVolumeSize = 1024 * 1024;

    /// <summary>Far above any real disk, so rounding up can never overflow.</summary>
    public const long MaximumVolumeSize = 1L << 60;

    public const long MinimumRecordSize = 4 * 1024;

    public const long MaximumRecordSize = 1024 * 1024;

    /// <summary>ZFS refuses longer full dataset names.</summary>
    public const int MaximumFullNameLength = 255;

    /// <summary>
    /// Compression algorithms Pithyx sets. Not <c>on</c> (it hides which algorithm ZFS picks), not <c>lzjb</c> (legacy)
    /// and not <c>zstd-fast-*</c> (trades too much ratio for a speed lz4 already has).
    /// </summary>
    public static readonly IReadOnlyList<string> Compressions =
    [
        "off", "lz4", "zle",
        "zstd", .. Enumerable.Range(1, 19).Select(level => $"zstd-{level}"),
        "gzip", .. Enumerable.Range(1, 9).Select(level => $"gzip-{level}"),
    ];

    public static string VolumesDataset(string pool) => $"{pool}/pithyx/volumes";

    /// <summary>The full name of a dataset <paramref name="name"/> below <paramref name="parent"/>, or <c>storage.dataset.name_invalid</c>.</summary>
    public static string Child(string parent, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(parent);
        var full = $"{parent}/{name}";
        if (name is null || !ZfsPlans.IsDatasetName(name) || full.Length > MaximumFullNameLength)
        {
            throw new PithyxException(ErrorCodes.StorageDatasetNameInvalid, new Dictionary<string, object?> { ["name"] = name });
        }
        return full;
    }

    /// <summary>Renames a dataset below the same parent. Never <c>-f</c>: ZFS refuses while it is busy instead of unmounting it.</summary>
    public static IReadOnlyList<PlanStep> Rename(string dataset, string newName)
    {
        var parent = Parent(Single(dataset));
        return [new PlanStep(new CommandRequest("zfs", ["rename", dataset, Child(parent, newName)], TimeSpan.FromMinutes(5)), [])];
    }

    /// <summary>One <c>zfs set</c> with every changed property, in the order given.</summary>
    public static IReadOnlyList<PlanStep> Set(string dataset, IReadOnlyList<KeyValuePair<string, string>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Count == 0)
        {
            throw new ArgumentException("Nothing to set.", nameof(properties));
        }
        return [new PlanStep(new CommandRequest("zfs", ["set", .. properties.Select(property => $"{property.Key}={property.Value}"), Single(dataset)]), [])];
    }

    /// <summary>
    /// Destroys a dataset; <c>-r</c> takes its children and snapshots with it and is passed only when the caller listed
    /// them. Never <c>-R</c> (clones of its snapshots elsewhere) and never <c>-f</c> (forced unmount).
    /// </summary>
    public static IReadOnlyList<PlanStep> Destroy(string dataset, bool recursive)
    {
        var target = Single(dataset);
        if (!target.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"{target} is a pool root.", nameof(dataset));
        }
        string[] arguments = recursive ? ["destroy", "-r", target] : ["destroy", target];
        return [new PlanStep(new CommandRequest("zfs", arguments, TimeSpan.FromMinutes(10)), [])];
    }

    /// <summary>The size ZFS gets for a volume: a whole number of blocks.</summary>
    public static long RoundVolumeSize(long sizeBytes)
    {
        if (sizeBytes is < MinimumVolumeSize or > MaximumVolumeSize)
        {
            throw new PithyxException(
                ErrorCodes.StorageVolumeSizeInvalid,
                new Dictionary<string, object?> { ["minBytes"] = MinimumVolumeSize, ["maxBytes"] = MaximumVolumeSize });
        }
        return (sizeBytes + VolumeBlockSize - 1) / VolumeBlockSize * VolumeBlockSize;
    }

    /// <summary>
    /// A volume <c>&lt;pool&gt;/pithyx/volumes/&lt;name&gt;</c>, after creating the unmounted <c>volumes</c> dataset when
    /// it is missing. A sparse volume (<c>-s</c>) reserves nothing; otherwise ZFS reserves its whole size.
    /// </summary>
    public static IReadOnlyList<PlanStep> CreateVolume(string pool, string name, long sizeBytes, bool sparse, bool createParent)
    {
        var full = Child(VolumesDataset(Single(pool)), name);
        var size = RoundVolumeSize(sizeBytes).ToString(CultureInfo.InvariantCulture);
        var steps = new List<PlanStep>();
        if (createParent)
        {
            steps.Add(new PlanStep(new CommandRequest("zfs", ["create", "-o", "canmount=off", "-o", "mountpoint=none", VolumesDataset(pool)]), []));
        }
        string[] create = sparse
            ? ["create", "-V", size, "-s", "-o", "volblocksize=16K", full]
            : ["create", "-V", size, "-o", "volblocksize=16K", full];
        steps.Add(new PlanStep(new CommandRequest("zfs", create), []));
        return steps;
    }

    public static string Parent(string dataset)
    {
        var slash = dataset.LastIndexOf('/');
        return slash > 0 ? dataset[..slash] : throw new ArgumentException($"{dataset} is a pool root.", nameof(dataset));
    }

    /// <summary>A name as it came from the dataset list: one dataset, never an option, a snapshot or a bookmark.</summary>
    private static string Single(string dataset)
    {
        if (string.IsNullOrEmpty(dataset) || dataset.StartsWith('-') || dataset.IndexOfAny(['@', '#', ',', '%']) >= 0
            || dataset.Split('/').Any(part => part.Length == 0))
        {
            throw new ArgumentException($"Not a single dataset: {dataset}.", nameof(dataset));
        }
        return dataset;
    }
}
