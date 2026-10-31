using System.Text.RegularExpressions;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Encryption;

/// <summary>A file system on a USB stick or another removable disk that can carry pool keys (D385).</summary>
/// <param name="Device">The partition's device node, or the disk's when the file system covers the whole disk.</param>
/// <param name="DiskId">The disk's stable id, see <see cref="Disk.Id"/>.</param>
/// <param name="FileSystem"><c>vfat</c>, <c>exfat</c>, <c>ext2</c>, <c>ext3</c> or <c>ext4</c>.</param>
/// <param name="Label">The file system's label, if any.</param>
/// <param name="Vendor">The disk's vendor, if any.</param>
/// <param name="Model">The disk's model, if any.</param>
/// <param name="Serial">The disk's serial number, if any.</param>
/// <param name="SizeBytes">Size of the file system's partition or disk.</param>
/// <param name="ReadOnly">True when the disk is write-protected: keys can be read from it, not written.</param>
public sealed record UsbStick(
    string Device,
    string DiskId,
    string FileSystem,
    string? Label,
    string? Vendor,
    string? Model,
    string? Serial,
    long SizeBytes,
    bool ReadOnly);

/// <summary>Response of <c>GET /api/storage/usb-sticks</c>: the file systems a pool key can be written to.</summary>
public sealed record UsbStickList(IReadOnlyList<UsbStick> Sticks);

/// <summary>
/// Which removable disks carry pool keys and how they are mounted (D385). A key file is
/// <c>pithyx-keys/&lt;pool GUID&gt;.key</c> holding the key as 64 hex characters, as the key vault keeps it, so
/// <c>zfs load-key -L file://...</c> takes it on any Linux. Pure: the inventory in, the sticks and commands out.
/// </summary>
public static partial class UsbKeys
{
    public const string Folder = "pithyx-keys";

    public static readonly IReadOnlySet<string> FileSystems = new HashSet<string>(StringComparer.Ordinal) { "vfat", "exfat", "ext2", "ext3", "ext4" };

    // A disk with any of these is in use by the system, a pool or a RAID and is never mounted for a key.
    private static readonly HashSet<string> _inUse = new(StringComparer.Ordinal) { "zfs_member", "linux_raid_member", "swap", "crypto_LUKS", "LVM2_member" };

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    public static string FileName(string poolGuid) => $"{poolGuid}.key";

    /// <summary>
    /// The file systems on USB or removable disks that nothing has mounted. A disk with a mounted file system, swap or a
    /// member of a pool or RAID is left out whole, so the system disk (also one installed on a stick) never shows; so is
    /// any drive that is not a disk, such as an optical drive.
    /// </summary>
    public static IReadOnlyList<UsbStick> Sticks(IReadOnlyList<Disk> disks, IReadOnlyDictionary<string, BlockDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(devices);
        var sticks = new List<UsbStick>();
        foreach (var disk in disks.Where(disk => disk.Transport == "usb" || disk.Removable))
        {
            if (!devices.TryGetValue(disk.Id, out var device) || device.Type != "disk")
            {
                continue;
            }
            var nodes = device.SelfAndDescendants().ToList();
            if (nodes.Any(node => node.MountPoints.Count > 0 || (node.FileSystem is { } type && _inUse.Contains(type))))
            {
                continue;
            }
            foreach (var node in nodes.Where(node => node.Children.Count == 0 && node.FileSystem is { } type && FileSystems.Contains(type)))
            {
                var partition = disk.Partitions.FirstOrDefault(candidate => candidate.Path == node.Path);
                sticks.Add(new UsbStick(
                    node.Path,
                    disk.Id,
                    node.FileSystem!,
                    partition?.Label,
                    disk.Vendor,
                    disk.Model,
                    disk.Serial,
                    partition?.SizeBytes ?? disk.SizeBytes,
                    disk.ReadOnly));
            }
        }
        return sticks;
    }

    /// <summary>The key in a key file: 64 hex characters, in any case and with surrounding space; null for anything else.</summary>
    public static string? ParseKey(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = content.Trim().ToLowerInvariant();
        return HexKey().IsMatch(key) ? key : null;
    }

    /// <summary>
    /// Mounts the stick so that nothing on it changes: read-only, and for ext3 and ext4 without replaying the journal, which
    /// a read-only mount otherwise does.
    /// </summary>
    public static CommandRequest MountReadOnly(UsbStick stick, string target)
    {
        ArgumentNullException.ThrowIfNull(stick);
        var options = stick.FileSystem is "ext3" or "ext4" ? "ro,nosuid,nodev,noexec,noload" : "ro,nosuid,nodev,noexec";
        return new CommandRequest("mount", ["-t", stick.FileSystem, "-o", options, stick.Device, target], _timeout);
    }

    public static CommandRequest MountWritable(UsbStick stick, string target)
    {
        ArgumentNullException.ThrowIfNull(stick);
        return new CommandRequest("mount", ["-t", stick.FileSystem, "-o", "nosuid,nodev,noexec", stick.Device, target], _timeout);
    }

    public static CommandRequest Unmount(string target) => new("umount", [target], _timeout);

    /// <summary>How a stick is named to the user: its label, else vendor and model, else its device.</summary>
    public static string Describe(UsbStick stick)
    {
        ArgumentNullException.ThrowIfNull(stick);
        if (!string.IsNullOrWhiteSpace(stick.Label))
        {
            return stick.Label.Trim();
        }
        var name = string.Join(' ', new[] { stick.Vendor, stick.Model }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return name.Length > 0 ? name : stick.Device;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexKey();
}
