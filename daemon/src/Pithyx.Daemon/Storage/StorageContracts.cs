namespace Pithyx.Daemon.Storage;

/// <summary>A disk with the guard's verdict, for the Storage settings page.</summary>
public sealed record StorageDisk(Disk Disk, GuardVerdict Guard);

/// <summary>Response of <c>GET /api/storage/disks</c>.</summary>
/// <param name="Disks">Every disk with its verdict.</param>
/// <param name="Environment"><c>dev</c> or <c>prod</c>; on dev only the declared test disks can be used.</param>
public sealed record StorageDiskList(IReadOnlyList<StorageDisk> Disks, string Environment);

/// <param name="Name">Pool name.</param>
/// <param name="Layout"><c>single</c>, <c>stripe</c>, <c>mirror</c>, <c>raidz1</c>, <c>raidz2</c> or <c>raidz3</c>.</param>
/// <param name="DiskIds">The disks to use, by id.</param>
/// <param name="Confirmations">For disks that are not empty: the serial number the user typed, by disk id.</param>
public sealed record PoolCreatePreviewRequest(string Name, string Layout, IReadOnlyList<string> DiskIds, IReadOnlyDictionary<string, string>? Confirmations = null);

/// <param name="Program">Program name.</param>
/// <param name="Arguments">Arguments, passed without a shell.</param>
public sealed record PlannedCommand(string Program, IReadOnlyList<string> Arguments);

/// <summary>Response of <c>POST /api/storage/pools/preview</c>. Nothing was run.</summary>
/// <param name="Disks">The guard's verdict for every chosen disk, after probing them again.</param>
/// <param name="Commands">The exact commands the plan would run, or null while a disk is not allowed.</param>
/// <param name="ExpiresAt">Until when the plan may run once creating pools is available.</param>
public sealed record PoolCreatePreview(IReadOnlyList<GuardVerdict> Disks, IReadOnlyList<PlannedCommand>? Commands, DateTimeOffset ExpiresAt);
