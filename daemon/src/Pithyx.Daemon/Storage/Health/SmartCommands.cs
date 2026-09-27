using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Health;

/// <summary>
/// Builds every smartctl command the daemon runs. Each one carries <c>-n standby</c>: smartctl then checks the power mode
/// first and does nothing to a disk that is asleep, so health checks never spin a disk up.
/// </summary>
public static class SmartCommands
{
    /// <summary>Self-test kinds: <c>short</c> takes minutes, <c>long</c> reads the whole surface and takes hours.</summary>
    public static readonly IReadOnlyList<string> SelfTestKinds = ["short", "long"];

    public static CommandRequest Read(string devicePath) => new("smartctl", ["-n", "standby", "-j", "-a", devicePath], TimeSpan.FromSeconds(30));

    /// <summary>Starts a self-test in the drive's background; smartctl returns at once.</summary>
    public static CommandRequest SelfTest(string devicePath, string kind)
    {
        if (!SelfTestKinds.Contains(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown self-test kind.");
        }
        return new("smartctl", ["-n", "standby", "-j", "-t", kind, devicePath], TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Disks smartctl cannot ask at all: virtio disks of a virtual machine have no SMART and no pass-through. Other
    /// virtual disks (QEMU SCSI) are asked and answer that they have no SMART.
    /// </summary>
    public static bool IsVirtio(Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return disk.Name.StartsWith("vd", StringComparison.Ordinal) || disk.Name.StartsWith("xvd", StringComparison.Ordinal);
    }
}
