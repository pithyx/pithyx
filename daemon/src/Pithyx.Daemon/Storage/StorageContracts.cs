namespace Pithyx.Daemon.Storage;

/// <summary>A disk with the guard's verdict, for the Storage settings page.</summary>
public sealed record StorageDisk(Disk Disk, GuardVerdict Guard);

/// <summary>Response of <c>GET /api/storage/disks</c>.</summary>
/// <param name="Disks">Every disk with its verdict.</param>
/// <param name="Environment"><c>dev</c> or <c>prod</c>; on dev only the declared test disks can be used.</param>
public sealed record StorageDiskList(IReadOnlyList<StorageDisk> Disks, string Environment);

/// <summary>
/// A destructive storage action to plan. Which fields an action needs: <c>pool.create</c> Pool, Layout, DiskIds,
/// Encryption (and Confirmations for disks that are not empty); <c>pool.destroy</c> Pool and ConfirmName;
/// <c>pool.replace</c> Pool, Vdev and one disk in DiskIds with its serial in Confirmations; <c>pool.spare.add</c> Pool
/// and one disk; <c>pool.spare.remove</c>, <c>pool.offline</c> and <c>pool.online</c> Pool and Vdev;
/// <c>pool.import.readonly</c> PoolGuid from the import scan, NewName when the name is taken, and Force with the pool
/// name in ConfirmName when the scan says <c>needsForce</c>; <c>pool.import</c> Pool (imported read-only), and Force with
/// ConfirmName to force it; <c>pool.export</c> Pool; <c>pool.vdev.add</c> Pool, Layout (<c>single</c>, <c>mirror</c>,
/// <c>raidz1</c> to <c>raidz3</c>), DiskIds, Confirmations, and ConfirmName when the preview says so;
/// <c>pool.raidz.expand</c> Pool, Vdev (the raidz vdev), one disk and ConfirmName;
/// <c>dataset.rename</c> Pool, Dataset and NewName; <c>dataset.set</c> Pool, Dataset and at least one of QuotaBytes,
/// ReservationBytes, Compression and RecordSizeBytes; <c>dataset.destroy</c> Pool, Dataset and ConfirmName;
/// <c>volume.create</c> Pool, Name, SizeBytes and Sparse; <c>snapshot.destroy</c> Pool, Dataset and the snapshot's
/// Name; <c>snapshot.rollback</c> Pool, Dataset, the snapshot's Name and the dataset's full name in ConfirmName.
/// </summary>
/// <param name="Action"><c>pool.create</c>, <c>pool.destroy</c>, <c>pool.replace</c>, <c>pool.spare.add</c>, <c>pool.spare.remove</c>, <c>pool.offline</c>, <c>pool.online</c>, <c>pool.import.readonly</c>, <c>pool.import</c>, <c>pool.export</c>, <c>pool.vdev.add</c>, <c>pool.raidz.expand</c>, <c>dataset.rename</c>, <c>dataset.set</c>, <c>dataset.destroy</c>, <c>volume.create</c>, <c>snapshot.destroy</c> or <c>snapshot.rollback</c>.</param>
/// <param name="Pool">The pool, or the new pool's name; for <c>pool.import.readonly</c> the scanned name (the GUID decides).</param>
/// <param name="Layout"><c>single</c>, <c>stripe</c>, <c>mirror</c>, <c>mirrors</c> (pairs), <c>raidz1</c>, <c>raidz2</c> or <c>raidz3</c>.</param>
/// <param name="DiskIds">The disks to use, by id.</param>
/// <param name="Name">Name of a new volume (one path component), or the snapshot after <c>@</c> for the snapshot actions. Shared folders are created with <c>POST /api/shares/plans</c>.</param>
/// <param name="Vdev">GUID of the device to replace, take offline, bring online or remove, or of the raidz vdev to expand.</param>
/// <param name="Encryption">The answer to the encryption question of a new pool: <c>none</c>, or <c>key</c> once the key vault exists.</param>
/// <param name="ConfirmName">The pool's name, or the dataset's full name, typed by the user where the preview says <c>requiresConfirmName</c> and to destroy.</param>
/// <param name="Confirmations">Serial numbers the user typed, by disk id.</param>
/// <param name="Dataset">Full name of the dataset or volume to rename, change or destroy, on <paramref name="Pool"/>.</param>
/// <param name="NewName">The new last component of a renamed dataset (it stays below the same parent), or for <c>pool.import.readonly</c> the name to import the pool under when its own is taken.</param>
/// <param name="QuotaBytes">New quota in bytes (dataset, children and snapshots), 0 for none; null leaves it.</param>
/// <param name="ReservationBytes">New reservation in bytes (dataset, children and snapshots), 0 for none; null leaves it.</param>
/// <param name="Compression">New compression: <c>off</c>, <c>lz4</c>, <c>zle</c>, <c>zstd</c>, <c>zstd-1</c> to <c>zstd-19</c>, <c>gzip</c>, <c>gzip-1</c> to <c>gzip-9</c>; null leaves it.</param>
/// <param name="RecordSizeBytes">New record size of a file system, a power of two from 4 KiB to 1 MiB; null leaves it.</param>
/// <param name="SizeBytes">Size of a new volume, at least 1 MiB; rounded up to whole 16 KiB blocks.</param>
/// <param name="Sparse">True for a volume that reserves no space up front.</param>
/// <param name="PoolGuid">For <c>pool.import.readonly</c>: the pool's numeric identifier from the import scan.</param>
/// <param name="Force">For imports: import a pool that was last used by another system; needs ConfirmName.</param>
public sealed record StoragePlanRequest(
    string Action,
    string Pool,
    string? Layout = null,
    IReadOnlyList<string>? DiskIds = null,
    string? Name = null,
    string? Vdev = null,
    string? Encryption = null,
    string? ConfirmName = null,
    IReadOnlyDictionary<string, string>? Confirmations = null,
    string? Dataset = null,
    string? NewName = null,
    long? QuotaBytes = null,
    long? ReservationBytes = null,
    string? Compression = null,
    long? RecordSizeBytes = null,
    long? SizeBytes = null,
    bool? Sparse = null,
    string? PoolGuid = null,
    bool? Force = null);

/// <param name="Program">Program name.</param>
/// <param name="Arguments">Arguments, passed without a shell.</param>
public sealed record PlannedCommand(string Program, IReadOnlyList<string> Arguments);

/// <summary>Response of <c>POST /api/storage/plans</c>. Nothing was run.</summary>
/// <param name="PlanId">
/// Runs the plan exactly as shown with <c>POST /api/storage/plans/{planId}/run</c>; null while a disk is not allowed, and
/// while <paramref name="RequiresConfirmName"/> is true and the name was not typed yet.
/// </param>
/// <param name="Disks">The guard's verdict for every chosen disk, after probing them again.</param>
/// <param name="Commands">The exact commands, or null while a disk is not allowed.</param>
/// <param name="Datasets">
/// For destroying or exporting a pool: every dataset that goes or becomes unavailable; for <c>pool.import</c>: the
/// datasets it mounts. For destroying a dataset: the dataset, its children and every snapshot of them
/// (<c>dataset@snapshot</c>), each dataset followed by its snapshots. For renaming a dataset: the dataset and its
/// children, whose names change. For destroying a snapshot: the snapshot (<c>dataset@snapshot</c>); for rolling back:
/// every newer snapshot, which goes too.
/// </param>
/// <param name="ExpiresAt">Until when the plan may run.</param>
/// <param name="Warnings">
/// Stable codes for what the user must know before running it: <c>lowers_redundancy</c>, <c>replication_mismatch</c>,
/// <c>irreversible</c> and <c>no_redundancy</c> (adding a vdev: it tolerates fewer failed disks than the pool, it has a
/// layout ZFS calls a replication mismatch, it can never be removed, it has no redundancy), <c>irreversible</c> (RAID-Z
/// expansion), <c>other_system</c> (an import forced over another system's claim), <c>degraded</c> (the pool is imported
/// with devices missing), <c>unmounted_datasets</c> (<paramref name="Unmounted"/> stay unmounted) and
/// <c>datasets_unavailable</c> (exporting a pool with datasets in use); for a rollback <c>changes_lost</c> (every change
/// since the snapshot is lost) and <c>newer_snapshots</c> (the listed newer snapshots are destroyed).
/// </param>
/// <param name="RequiresConfirmName">True when the plan runs only with the pool's name (for a rollback the dataset's full name) typed in <c>confirmName</c>.</param>
/// <param name="Unmounted">For <c>pool.import</c>: datasets that stay unmounted, and why; those that would hide part of the system also get <c>canmount=noauto</c>.</param>
public sealed record StoragePlanPreview(
    string? PlanId,
    IReadOnlyList<GuardVerdict> Disks,
    IReadOnlyList<PlannedCommand>? Commands,
    IReadOnlyList<string> Datasets,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Warnings,
    bool RequiresConfirmName,
    IReadOnlyList<Zfs.UnmountedDataset> Unmounted);

/// <summary>Response of <c>POST /api/storage/import/scan</c>: pools on the disks that are not imported. Nothing was imported.</summary>
public sealed record ImportScan(IReadOnlyList<ImportablePool> Pools);

/// <param name="Name">The pool's name as stored on its disks.</param>
/// <param name="PoolGuid">The numeric identifier as decimal text; <c>pool.import.readonly</c> takes it as <c>poolGuid</c>.</param>
/// <param name="State">ZFS state as it would be imported: <c>ONLINE</c>, <c>DEGRADED</c>, <c>UNAVAIL</c> or <c>FAULTED</c>.</param>
/// <param name="Status">What ZFS says is wrong, in English, if anything.</param>
/// <param name="Action">What ZFS says can be done, in English.</param>
/// <param name="MessageId">ZFS message id, for example <c>ZFS-8000-2Q</c> (devices missing) or <c>ZFS-8000-EY</c> (last used by another system).</param>
/// <param name="CanImport">True when ZFS can import the pool.</param>
/// <param name="NeedsForce">True when it was last used by another system: importing needs <c>force</c> and the typed name.</param>
/// <param name="NameTaken">True when an imported pool has this name: the import needs <c>newName</c>.</param>
/// <param name="Vdevs">The pool's layout with each device's state and disk.</param>
/// <param name="Disks">The guard's import verdict for each disk the pool is on.</param>
/// <param name="UnknownDevices">Present devices that are on no disk the daemon knows; such a pool is not offered.</param>
/// <param name="Allowed">True when the pool can be imported: ZFS can, and every device is on a disk the guard allows.</param>
public sealed record ImportablePool(
    string Name,
    string PoolGuid,
    string State,
    string? Status,
    string? Action,
    string? MessageId,
    bool CanImport,
    bool NeedsForce,
    bool NameTaken,
    IReadOnlyList<Zfs.ImportVdev> Vdevs,
    IReadOnlyList<GuardVerdict> Disks,
    IReadOnlyList<string> UnknownDevices,
    bool Allowed);
