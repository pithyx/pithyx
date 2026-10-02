namespace Pithyx.Daemon.FileServices;

/// <param name="Apply">Whether commands run and files are written; false in simulation.</param>
/// <param name="SftpRoot">The folder of the SFTP views, one chroot per user.</param>
/// <param name="SmbConfigPath">The generated Samba configuration.</param>
/// <param name="SmbLocalInclude">The owner's own Samba settings, never written but created empty.</param>
/// <param name="SshdDropIn">The generated sshd drop-in.</param>
/// <param name="GroupPath">Where the groups are read.</param>
public sealed record FileServiceOptions(
    bool Apply,
    string SftpRoot = "/run/pithyx/sftp",
    string SmbConfigPath = SmbConfig.ConfigPath,
    string SmbLocalInclude = SmbConfig.LocalInclude,
    string SshdDropIn = SshdConfig.DropInPath,
    string GroupPath = "/etc/group");

/// <summary>How a file service is doing.</summary>
public enum FileServiceState
{
    /// <summary>Switched off.</summary>
    Off,

    /// <summary>Switched on and running with the current configuration.</summary>
    Running,

    /// <summary>Switched on, but its configuration was refused or it did not start; the Log Center has the details.</summary>
    Failed,

    /// <summary>Its program is not installed on this system.</summary>
    Unavailable,
}

/// <summary>The file services of the box. Administrators only.</summary>
/// <param name="SmbEnabled">Whether SMB is switched on.</param>
/// <param name="Workgroup">The SMB workgroup, 1 to 15 letters, digits, dots, dashes and underscores.</param>
/// <param name="SftpEnabled">Whether SFTP is switched on.</param>
/// <param name="Smb">How SMB is doing.</param>
/// <param name="Sftp">How SFTP is doing.</param>
public sealed record FileServiceSettings(bool SmbEnabled, string Workgroup, bool SftpEnabled, FileServiceState Smb, FileServiceState Sftp);

/// <summary>Changes of the file services; fields left out stay.</summary>
/// <param name="SmbEnabled">Switches SMB on or off.</param>
/// <param name="Workgroup">A new SMB workgroup.</param>
/// <param name="SftpEnabled">Switches SFTP on or off.</param>
public sealed record FileServiceSettingsRequest(bool? SmbEnabled = null, string? Workgroup = null, bool? SftpEnabled = null);
