using System.Runtime.InteropServices;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Installer;

/// <summary>Pure decisions of the installer: whether the box meets the requirements and which disks may hold the system.</summary>
public static class InstallerFacts
{
    public const long MinMemoryBytes = 4L * 1000 * 1000 * 1000;

    /// <summary>ESP, system and swap partition (System layout v0.1) with room to spare.</summary>
    public const long MinDiskBytes = 40L * 1000 * 1000 * 1000;

    // The kernel reserves some memory, so a 4 GB box reports a little less.
    private const long MemoryTolerance = 400L * 1000 * 1000;

    private static readonly string[] _x8664V2Flags = ["cx16", "lahf_lm", "popcnt", "sse4_1", "sse4_2", "ssse3"];

    /// <summary>Whether <c>/proc/cpuinfo</c> shows every flag of x86-64-v2.</summary>
    public static bool IsX8664V2(string cpuInfo)
    {
        ArgumentNullException.ThrowIfNull(cpuInfo);
        var flags = cpuInfo.Split('\n')
            .FirstOrDefault(line => line.StartsWith("flags", StringComparison.Ordinal))?
            .Split(':', 2).ElementAtOrDefault(1)?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        return _x8664V2Flags.All(flags.Contains);
    }

    /// <summary>The <c>SecureBoot</c> EFI variable: four attribute bytes, then 1 when Secure Boot is on.</summary>
    public static bool IsSecureBootOn(ReadOnlySpan<byte> variable) => variable.Length >= 5 && variable[4] == 1;

    /// <param name="cpuInfo">Content of <c>/proc/cpuinfo</c>.</param>
    /// <param name="memoryBytes">Total memory.</param>
    /// <param name="uefi">Whether the box booted with UEFI.</param>
    /// <param name="secureBootVariable">The <c>SecureBoot</c> EFI variable, or null when it does not exist.</param>
    /// <param name="disks">The disks as the installer offers them.</param>
    /// <param name="architecture">The processor architecture; the x86-64-v2 check only applies to x64.</param>
    public static IReadOnlyList<InstallerCheck> Checks(
        string cpuInfo,
        long memoryBytes,
        bool uefi,
        byte[]? secureBootVariable,
        IReadOnlyList<InstallerDisk> disks,
        Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(disks);
        return
        [
            new("cpu", architecture != Architecture.X64 || IsX8664V2(cpuInfo), new Dictionary<string, object?>()),
            new("uefi", uefi, new Dictionary<string, object?>()),
            new("memory", memoryBytes + MemoryTolerance >= MinMemoryBytes, new Dictionary<string, object?>
            {
                ["minGb"] = MinMemoryBytes / 1_000_000_000,
                ["foundGb"] = Math.Round(memoryBytes / 1e9, 1),
            }),
            new("secure_boot", secureBootVariable is null || !IsSecureBootOn(secureBootVariable), new Dictionary<string, object?>()),
            new("disk", disks.Any(disk => disk.Suitable), new Dictionary<string, object?> { ["minGb"] = MinDiskBytes / 1_000_000_000 }),
        ];
    }

    /// <summary>
    /// Which disks may hold the system: internal disks of at least <see cref="MinDiskBytes"/>, never a USB or removable
    /// disk, never the medium the live system runs from and never a disk with something mounted or active swap. SSDs and
    /// HDDs both pass; the page hints at HDDs.
    /// </summary>
    /// <param name="disks">Every disk.</param>
    /// <param name="devices">Device trees by disk id, to find the boot medium and data on a disk.</param>
    public static IReadOnlyList<InstallerDisk> EvaluateDisks(IReadOnlyList<Disk> disks, IReadOnlyDictionary<string, BlockDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(devices);
        return [.. disks
            .OrderBy(disk => disk.Name, StringComparer.Ordinal)
            .Select(disk =>
            {
                var tree = devices.GetValueOrDefault(disk.Id)?.SelfAndDescendants().ToList() ?? [];
                var reason =
                    tree.Any(device => device.MountPoints.Any(IsLiveMount)) || disk.Partitions.Any(part => part.MountPoints.Any(IsLiveMount)) ? "boot_medium"
                    : string.Equals(disk.Transport, "usb", StringComparison.Ordinal) ? "usb"
                    : disk.Removable || string.Equals(disk.Transport, "mmc", StringComparison.Ordinal) ? "removable"
                    : disk.ReadOnly ? "read_only"
                    : tree.Any(device => device.MountPoints.Count > 0) ? "in_use"
                    : disk.SizeBytes < MinDiskBytes ? "too_small"
                    : null;
                var hasData = disk.Partitions.Count > 0 || tree.Any(device => device.FileSystem is not null);
                return new InstallerDisk(disk, reason is null, reason, hasData);
            })];
    }

    /// <summary>Whether a host name is a DNS label: lowercase letters, digits and inner hyphens, at most 63 characters.</summary>
    public static bool IsHostname(string? value) =>
        value is { Length: > 0 and <= 63 }
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && value[0] != '-'
        && value[^1] != '-';

    /// <summary>
    /// The device node of partition <paramref name="number"/>: <c>/dev/sda1</c>, but <c>/dev/nvme0n1p1</c> for disks
    /// whose name ends in a digit.
    /// </summary>
    public static string PartitionPath(string diskPath, int number)
    {
        ArgumentNullException.ThrowIfNull(diskPath);
        return char.IsAsciiDigit(diskPath[^1]) ? $"{diskPath}p{number}" : $"{diskPath}{number}";
    }

    // live-boot mounts the stick under /run/live (medium, rootfs, overlay).
    private static bool IsLiveMount(string mountPoint) => mountPoint.StartsWith("/run/live", StringComparison.Ordinal);
}
