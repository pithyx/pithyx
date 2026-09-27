namespace Pithyx.Daemon.Storage;

/// <param name="Name">Pool name.</param>
/// <param name="PoolGuid">Pool GUID as decimal text (it does not fit a JSON number).</param>
/// <param name="State">ZFS state: <c>ONLINE</c>, <c>DEGRADED</c>, <c>FAULTED</c>, <c>OFFLINE</c>, <c>UNAVAIL</c>, <c>REMOVED</c> or <c>SUSPENDED</c>.</param>
/// <param name="SizeBytes">Raw size of all vdevs, parity included.</param>
/// <param name="AllocatedBytes">Raw space in use.</param>
/// <param name="FreeBytes">Raw space free.</param>
/// <param name="CapacityPercent">Share of the raw space in use.</param>
/// <param name="FragmentationPercent">Free space fragmentation, if known.</param>
/// <param name="ReadOnly">True when the pool was imported read-only.</param>
/// <param name="DataErrors">Number of files with permanent errors.</param>
/// <param name="Scan">The last or running scrub or resilver, if any.</param>
/// <param name="Vdevs">Top-level vdevs, including log, cache, special and spare devices.</param>
public sealed record Pool(
    string Name,
    string PoolGuid,
    string State,
    long SizeBytes,
    long AllocatedBytes,
    long FreeBytes,
    int CapacityPercent,
    int? FragmentationPercent,
    bool ReadOnly,
    long DataErrors,
    PoolScan? Scan,
    IReadOnlyList<Vdev> Vdevs);

/// <param name="Name">Vdev name as ZFS shows it: <c>raidz1-0</c>, <c>mirror-1</c> or the device's by-id name.</param>
/// <param name="VdevGuid">Vdev GUID as decimal text; names a device in commands even when it is missing.</param>
/// <param name="Type">ZFS vdev type: <c>disk</c>, <c>file</c>, <c>mirror</c>, <c>raidz</c>, <c>draid</c>, <c>spare</c>, <c>replacing</c> and so on.</param>
/// <param name="Role">What the vdev is for: <c>data</c>, <c>log</c>, <c>cache</c>, <c>special</c>, <c>dedup</c> or <c>spare</c>.</param>
/// <param name="State">ZFS state of the vdev.</param>
/// <param name="Path">Device path of a leaf vdev, usually below <c>/dev/disk/by-id</c>.</param>
/// <param name="ParityDisks">Parity level of a raidz vdev (1 to 3), otherwise null.</param>
/// <param name="ReadErrors">Read errors since import or the last clear.</param>
/// <param name="WriteErrors">Write errors since import or the last clear.</param>
/// <param name="ChecksumErrors">Checksum errors since import or the last clear.</param>
/// <param name="Children">Child vdevs.</param>
public sealed record Vdev(
    string Name,
    string VdevGuid,
    string Type,
    string Role,
    string State,
    string? Path,
    int? ParityDisks,
    long ReadErrors,
    long WriteErrors,
    long ChecksumErrors,
    IReadOnlyList<Vdev> Children)
{
    public IEnumerable<Vdev> Leaves() => Children.Count == 0 ? [this] : Children.SelectMany(child => child.Leaves());
}

/// <param name="Kind">Scan kind: <c>scrub</c> or <c>resilver</c>.</param>
/// <param name="State"><c>scanning</c>, <c>finished</c> or <c>canceled</c>.</param>
/// <param name="Paused">True for a paused scrub.</param>
/// <param name="StartedAt">When the scan started (UTC).</param>
/// <param name="EndedAt">When it ended (UTC), if it did.</param>
/// <param name="ToExamineBytes">Bytes the scan covers.</param>
/// <param name="ExaminedBytes">Bytes examined so far.</param>
/// <param name="Errors">Errors the scan found.</param>
public sealed record PoolScan(
    string Kind,
    string State,
    bool Paused,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long ToExamineBytes,
    long ExaminedBytes,
    long Errors);

/// <param name="Name">Full dataset name, for example <c>tank/pithyx/shares/media</c>.</param>
/// <param name="Pool">The pool it belongs to.</param>
/// <param name="Type"><c>filesystem</c> or <c>volume</c>.</param>
/// <param name="UsedBytes">Space used by the dataset and its children.</param>
/// <param name="AvailableBytes">Space available to it.</param>
/// <param name="ReferencedBytes">Data the dataset references itself.</param>
/// <param name="MountPoint">Mount point of a file system, if it has one.</param>
/// <param name="Mounted">True while the file system is mounted.</param>
/// <param name="Encryption">Encryption algorithm, or <c>off</c>.</param>
/// <param name="KeyStatus"><c>available</c> or <c>unavailable</c> for encrypted datasets, otherwise null.</param>
/// <param name="EncryptionRoot">The dataset whose key unlocks this one, if encrypted.</param>
public sealed record Dataset(
    string Name,
    string Pool,
    string Type,
    long UsedBytes,
    long AvailableBytes,
    long ReferencedBytes,
    string? MountPoint,
    bool Mounted,
    string Encryption,
    string? KeyStatus,
    string? EncryptionRoot);

/// <summary>Snapshot and event of <c>storage/pools</c>, and response of <c>GET /api/storage/pools</c>.</summary>
public sealed record PoolList(IReadOnlyList<Pool> Pools, IReadOnlyList<Dataset> Datasets);
