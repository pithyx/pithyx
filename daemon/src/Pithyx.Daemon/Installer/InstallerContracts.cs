namespace Pithyx.Daemon.Installer;

public enum InstallerPhase
{
    Ready,
    Installing,
    Installed,
    Failed,
    Rebooting,
}

/// <summary>A requirement the box must meet before the installation starts.</summary>
/// <param name="Id"><c>cpu</c> (x86-64-v2), <c>uefi</c>, <c>memory</c>, <c>secure_boot</c> (off) or <c>disk</c> (a suitable disk exists).</param>
/// <param name="Passed">Whether the box meets it.</param>
/// <param name="Params">Values for the text, for example <c>minGb</c> and <c>foundGb</c> of <c>memory</c>.</param>
public sealed record InstallerCheck(string Id, bool Passed, IReadOnlyDictionary<string, object?> Params);

/// <summary>Response of <c>GET /api/installer</c>.</summary>
/// <param name="Claimed">Whether a browser owns the installation.</param>
/// <param name="Owner">Whether this browser owns it.</param>
/// <param name="Phase">Where the installation is.</param>
/// <param name="Step">The step that runs or failed: <c>partition</c>, <c>raid</c>, <c>filesystems</c>, <c>copy</c>, <c>configure</c>, <c>bootloader</c> or <c>finish</c>.</param>
/// <param name="StepIndex">Zero-based position of <paramref name="Step"/>.</param>
/// <param name="StepCount">Number of steps.</param>
/// <param name="ErrorCode">Why the installation failed.</param>
/// <param name="ErrorParams">Values for its text; for <c>installer.install.failed</c> the <c>step</c>, the <c>command</c> and its <c>exitCode</c>.</param>
/// <param name="InstallId">Set once installed: the installed system answers it on <c>http://&lt;address&gt;/api/install-id</c>.</param>
/// <param name="RecoveryKey">Set once installed, for the owner only: the administrator's recovery key, shown before the reboot.</param>
/// <param name="Version">The Pithyx version that gets installed.</param>
/// <param name="Checks">The requirements and whether the box meets them.</param>
public sealed record InstallerStatus(
    bool Claimed,
    bool Owner,
    InstallerPhase Phase,
    string? Step,
    int StepIndex,
    int StepCount,
    string? ErrorCode,
    IReadOnlyDictionary<string, object?> ErrorParams,
    string? InstallId,
    string? RecoveryKey,
    string Version,
    IReadOnlyList<InstallerCheck> Checks);

/// <summary>A disk as the installer offers it.</summary>
/// <param name="Disk">The disk.</param>
/// <param name="Suitable">Whether the system may be installed on it.</param>
/// <param name="Reason">Why not: <c>usb</c>, <c>removable</c>, <c>boot_medium</c>, <c>read_only</c>, <c>in_use</c> (mounted or swap) or <c>too_small</c>.</param>
/// <param name="HasData">Whether it holds partitions or file systems that the installation erases.</param>
public sealed record InstallerDisk(Storage.Disk Disk, bool Suitable, string? Reason, bool HasData);

/// <summary>Response of <c>GET /api/installer/options</c>: what the steps offer.</summary>
/// <param name="Disks">Every disk, suitable or not.</param>
/// <param name="Network">The network cards with their state and addresses.</param>
/// <param name="NetworkConfig">The network settings of the live system (set on the console, for example), the start of step 2.</param>
/// <param name="TimeZones">IANA time zone names.</param>
/// <param name="KeyboardLayouts">XKB layout codes for the console keyboard.</param>
/// <param name="MinDiskBytes">The smallest disk the system fits on.</param>
public sealed record InstallerOptions(
    IReadOnlyList<InstallerDisk> Disks,
    IReadOnlyList<Network.NetworkLink> Network,
    Network.NetworkConfig NetworkConfig,
    IReadOnlyList<string> TimeZones,
    IReadOnlyList<string> KeyboardLayouts,
    long MinDiskBytes);

/// <summary>Body of <c>POST /api/installer/install</c>.</summary>
/// <param name="Hostname">Name of the box, a DNS label.</param>
/// <param name="Language">System language.</param>
/// <param name="Region">Country of the formats, or null to follow each user's language.</param>
/// <param name="TimeZone">IANA time zone.</param>
/// <param name="NtpServers">Time servers; empty for the defaults.</param>
/// <param name="KeyboardLayout">XKB layout of the console keyboard.</param>
/// <param name="DiskIds">One disk, or two for a mirror.</param>
/// <param name="Username">The administrator.</param>
/// <param name="Password">The administrator's password.</param>
/// <param name="DisplayName">The administrator's display name.</param>
/// <param name="EraseConfirmed">The user confirmed that every listed disk is erased.</param>
/// <param name="Network">The network of the installed system (step 2), checked like Settings > Network; its host name is <paramref name="Hostname"/>. Null keeps DHCP on every card.</param>
public sealed record InstallRequest(
    string Hostname,
    string Language,
    string? Region,
    string TimeZone,
    IReadOnlyList<string> NtpServers,
    string KeyboardLayout,
    IReadOnlyList<string> DiskIds,
    string Username,
    string Password,
    string? DisplayName,
    bool EraseConfirmed,
    Network.NetworkConfig? Network = null);

/// <summary>Response of <c>GET /api/install-id</c> on an installed box.</summary>
public sealed record InstallIdResponse(string InstallId);
