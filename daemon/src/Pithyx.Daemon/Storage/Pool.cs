namespace Pithyx.Daemon.Storage;

/// <param name="Name">Pool name.</param>
/// <param name="PoolGuid">Pool GUID as decimal text (it does not fit a JSON number).</param>
/// <param name="State">ZFS state: <c>ONLINE</c>, <c>DEGRADED</c>, <c>FAULTED</c>, <c>OFFLINE</c>, <c>UNAVAIL</c>, <c>REMOVED</c> or <c>SUSPENDED</c>; degraded further when disks are gone that ZFS has not noticed yet.</param>
/// <param name="SizeBytes">Raw size of all vdevs, parity included.</param>
/// <param name="AllocatedBytes">Raw space in use.</param>
/// <param name="FreeBytes">Raw space free.</param>
/// <param name="CapacityPercent">Share of the raw space in use.</param>
/// <param name="FragmentationPercent">Free space fragmentation, if known.</param>
/// <param name="ReadOnly">True when the pool was imported read-only.</param>
/// <param name="DataErrors">Number of files with permanent errors.</param>
/// <param name="Scan">The last or running scrub or resilver, if any.</param>
/// <param name="Vdevs">Top-level vdevs, including log, cache, special and spare devices.</param>
/// <param name="Expansion">The last or running RAID-Z expansion, if the pool had one.</param>
/// <param name="Encryption">For a pool Pithyx encrypted (its <c>&lt;pool&gt;/pithyx</c> is an encryption root, D384): whether it is locked and where its key is kept; null for an unencrypted pool.</param>
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
    IReadOnlyList<Vdev> Vdevs,
    PoolExpansion? Expansion = null,
    PoolEncryption? Encryption = null);

/// <param name="Locked">True while its key is not loaded: its datasets are not mounted, and its shares, homes and apps are unavailable.</param>
/// <param name="KeyInVault">True when the key vault on the system disk holds its key, so the box unlocks it at start.</param>
/// <param name="VaultFailed">True when the vault's key was tried and did not unlock the pool (wrong, unreadable or damaged): the pool waits for its key to be typed.</param>
/// <param name="UsbKey">The USB stick its key was last written to on this box (D385), or null; the box also unlocks it from any other stick that holds its key.</param>
/// <param name="UnlockedFromUsb">True when the key on a USB stick unlocked it since the last start: the stick may be pulled, the pool stays unlocked until the next start.</param>
/// <param name="UsbFailed">True when a key file on a USB stick was tried and did not unlock the pool (an older key or damaged): the pool waits for another stick or the typed key.</param>
public sealed record PoolEncryption(bool Locked, bool KeyInVault, bool VaultFailed = false, PoolUsbKey? UsbKey = null, bool UnlockedFromUsb = false, bool UsbFailed = false);

/// <summary>Where a pool's key was written to a USB stick; no secret, kept beside the key vault and never in a backup.</summary>
/// <param name="Stick">How the stick was named then: its label, else vendor and model.</param>
/// <param name="Serial">The stick's serial number, if it has one.</param>
/// <param name="WrittenAt">When the key was written.</param>
public sealed record PoolUsbKey(string Stick, string? Serial, DateTimeOffset WrittenAt);

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
/// <param name="Missing">True for a leaf whose disk is not connected, also while ZFS has not noticed yet (see <see cref="PoolHealth"/>).</param>
/// <param name="Serial">Serial of the disk a leaf is on, as last seen connected, if known.</param>
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
    IReadOnlyList<Vdev> Children,
    bool Missing = false,
    string? Serial = null)
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

/// <summary>A RAID-Z expansion (OpenZFS 2.3): a disk attached to a raidz vdev, onto which ZFS spreads the existing data.</summary>
/// <param name="State"><c>scanning</c> while it runs, then <c>finished</c> or <c>canceled</c>.</param>
/// <param name="VdevGuid">GUID of the raidz vdev that grows, if it is still in the pool.</param>
/// <param name="StartedAt">When it started (UTC).</param>
/// <param name="EndedAt">When it ended (UTC), if it did.</param>
/// <param name="ToReflowBytes">Bytes to move onto the new layout.</param>
/// <param name="ReflowedBytes">Bytes moved so far.</param>
/// <param name="WaitingForResilver">True while it waits for a resilver to finish.</param>
public sealed record PoolExpansion(
    string State,
    string? VdevGuid,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long ToReflowBytes,
    long ReflowedBytes,
    bool WaitingForResilver);

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
/// <param name="QuotaBytes">Limit for the dataset with its children and snapshots, or null for none.</param>
/// <param name="ReservationBytes">Space guaranteed to the dataset with its children and snapshots, or null for none.</param>
/// <param name="Compression">Compression algorithm, for example <c>lz4</c>, <c>zstd</c> or <c>off</c>.</param>
/// <param name="RecordSizeBytes">Largest block of a file system; null for a volume.</param>
/// <param name="VolumeSizeBytes">Size of a volume; null for a file system.</param>
/// <param name="VolumeBlockSizeBytes">Block size of a volume; null for a file system.</param>
/// <param name="SnapshotsUsedBytes">Space only its snapshots hold.</param>
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
    string? EncryptionRoot,
    long? QuotaBytes,
    long? ReservationBytes,
    string? Compression,
    long? RecordSizeBytes,
    long? VolumeSizeBytes,
    long? VolumeBlockSizeBytes,
    long SnapshotsUsedBytes);

/// <summary>Snapshot and event of <c>storage/pools</c>, and response of <c>GET /api/storage/pools</c>.</summary>
public sealed record PoolList(IReadOnlyList<Pool> Pools, IReadOnlyList<Dataset> Datasets);
