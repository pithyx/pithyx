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
/// and one disk; <c>pool.spare.remove</c>, <c>pool.offline</c> and <c>pool.online</c> Pool and Vdev.
/// </summary>
/// <param name="Action"><c>pool.create</c>, <c>pool.destroy</c>, <c>pool.replace</c>, <c>pool.spare.add</c>, <c>pool.spare.remove</c>, <c>pool.offline</c>, <c>pool.online</c> or <c>dataset.create</c>.</param>
/// <param name="Pool">The pool, or the new pool's name.</param>
/// <param name="Layout"><c>single</c>, <c>stripe</c>, <c>mirror</c>, <c>mirrors</c> (pairs), <c>raidz1</c>, <c>raidz2</c> or <c>raidz3</c>.</param>
/// <param name="DiskIds">The disks to use, by id.</param>
/// <param name="Name">Name of a new share dataset.</param>
/// <param name="Vdev">GUID of the device to replace, take offline, bring online or remove.</param>
/// <param name="Encryption">The answer to the encryption question of a new pool: <c>none</c>, or <c>key</c> once the key vault exists.</param>
/// <param name="ConfirmName">The pool's name, typed by the user, to destroy it.</param>
/// <param name="Confirmations">Serial numbers the user typed, by disk id.</param>
public sealed record StoragePlanRequest(
    string Action,
    string Pool,
    string? Layout = null,
    IReadOnlyList<string>? DiskIds = null,
    string? Name = null,
    string? Vdev = null,
    string? Encryption = null,
    string? ConfirmName = null,
    IReadOnlyDictionary<string, string>? Confirmations = null);

/// <param name="Program">Program name.</param>
/// <param name="Arguments">Arguments, passed without a shell.</param>
public sealed record PlannedCommand(string Program, IReadOnlyList<string> Arguments);

/// <summary>Response of <c>POST /api/storage/plans</c>. Nothing was run.</summary>
/// <param name="PlanId">Runs the plan exactly as shown with <c>POST /api/storage/plans/{planId}/run</c>; null while a disk is not allowed.</param>
/// <param name="Disks">The guard's verdict for every chosen disk, after probing them again.</param>
/// <param name="Commands">The exact commands, or null while a disk is not allowed.</param>
/// <param name="Datasets">For destroying a pool: every dataset that goes with it.</param>
/// <param name="ExpiresAt">Until when the plan may run.</param>
public sealed record StoragePlanPreview(
    string? PlanId,
    IReadOnlyList<GuardVerdict> Disks,
    IReadOnlyList<PlannedCommand>? Commands,
    IReadOnlyList<string> Datasets,
    DateTimeOffset ExpiresAt);
