namespace Pithyx.Daemon.Storage;

/// <param name="Id">Stable across reboots and device renames: <c>wwn-...</c>, else <c>serial-...</c>, else <c>path-...</c>.</param>
/// <param name="Name">Kernel name, for example <c>sda</c> or <c>nvme0n1</c>. Can change between boots.</param>
/// <param name="Path">Device node, for example <c>/dev/sda</c>.</param>
/// <param name="Serial">Serial number as reported by the drive, if any.</param>
/// <param name="Wwn">World wide name, if any.</param>
/// <param name="Model">Model name, if any.</param>
/// <param name="Vendor">Vendor, if any.</param>
/// <param name="SizeBytes">Capacity in bytes.</param>
/// <param name="Rotational">True for spinning disks.</param>
/// <param name="Transport">Bus, for example <c>sata</c>, <c>nvme</c> or <c>usb</c>; null for virtual disks.</param>
/// <param name="Removable">True for removable media.</param>
/// <param name="ReadOnly">True when the kernel exposes the disk read-only.</param>
/// <param name="Partitions">Partitions in on-disk order.</param>
public sealed record Disk(
    string Id,
    string Name,
    string Path,
    string? Serial,
    string? Wwn,
    string? Model,
    string? Vendor,
    long SizeBytes,
    bool Rotational,
    string? Transport,
    bool Removable,
    bool ReadOnly,
    IReadOnlyList<Partition> Partitions);

/// <param name="Name">Kernel name, for example <c>sda1</c>.</param>
/// <param name="Path">Device node.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="FileSystem">File system or content type, for example <c>ext4</c> or <c>zfs_member</c>.</param>
/// <param name="Label">File system label, if any.</param>
/// <param name="MountPoints">Where the partition is mounted; empty when it is not.</param>
public sealed record Partition(string Name, string Path, long SizeBytes, string? FileSystem, string? Label, IReadOnlyList<string> MountPoints);

/// <summary>Snapshot of <c>storage/disks</c> and response of <c>GET /api/disks</c>.</summary>
public sealed record DiskList(IReadOnlyList<Disk> Disks);

/// <summary>Event of <c>storage/disks</c>: disks that appeared or changed, and ids of disks that disappeared.</summary>
public sealed record DisksChanged(IReadOnlyList<Disk> Upserts, IReadOnlyList<string> Removals);
