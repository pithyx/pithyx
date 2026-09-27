namespace Pithyx.Daemon.Storage;

/// <summary>
/// A block device in the lsblk tree: a disk with everything stacked on it (partitions, LVM, crypt, md). Internal to the
/// daemon; the disk guard uses it to find what a disk holds. A device on several disks (md, a volume group) appears
/// under each of them.
/// </summary>
/// <param name="Name">Kernel name, for example <c>sda1</c> or <c>md127</c>.</param>
/// <param name="Path">Device node, for example <c>/dev/sda1</c> or <c>/dev/mapper/vg-root</c>.</param>
/// <param name="IdLink">Name of the device's link in <c>/dev/disk/by-id</c>, if udev created one.</param>
/// <param name="MajorMinor">Device number as <c>major:minor</c>, which the mount table uses.</param>
/// <param name="Type">lsblk type: <c>disk</c>, <c>part</c>, <c>lvm</c>, <c>crypt</c>, <c>raid1</c> and so on.</param>
/// <param name="FileSystem">Content type, for example <c>ext4</c>, <c>swap</c> or <c>zfs_member</c>.</param>
/// <param name="MountPoints">Mount points, <c>[SWAP]</c> for active swap.</param>
/// <param name="Children">Devices stacked on this one.</param>
public sealed record BlockDevice(
    string Name,
    string Path,
    string? IdLink,
    string MajorMinor,
    string Type,
    string? FileSystem,
    IReadOnlyList<string> MountPoints,
    IReadOnlyList<BlockDevice> Children)
{
    /// <summary>This device and every device stacked on it, depth first.</summary>
    public IEnumerable<BlockDevice> SelfAndDescendants() => Children.SelectMany(child => child.SelfAndDescendants()).Prepend(this);

    /// <summary>The paths under which the device can be named: its node and its by-id link.</summary>
    public bool IsNamed(string path) =>
        string.Equals(path, Path, StringComparison.Ordinal)
        || (IdLink is not null && string.Equals(path, $"/dev/disk/by-id/{IdLink}", StringComparison.Ordinal));
}
