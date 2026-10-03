namespace Pithyx.Daemon.FileServices;

/// <param name="Apply">Whether commands run and files are written; false in simulation.</param>
/// <param name="SftpRoot">The folder of the SFTP views, one chroot per user.</param>
/// <param name="SmbConfigPath">The generated Samba configuration.</param>
/// <param name="SmbLocalInclude">The owner's own Samba settings, never written but created empty.</param>
/// <param name="SshdDropIn">The generated sshd drop-in.</param>
/// <param name="GroupPath">Where the groups are read.</param>
/// <param name="TlsDirectory">Where the FTPS certificate is kept, next to the database (null: no FTP).</param>
/// <param name="FtpPort">The FTP control port; 0 picks a free one (tests).</param>
/// <param name="FtpPassivePorts">The first and last port for FTP data connections.</param>
public sealed record FileServiceOptions(
    bool Apply,
    string SftpRoot = "/run/pithyx/sftp",
    string SmbConfigPath = SmbConfig.ConfigPath,
    string SmbLocalInclude = SmbConfig.LocalInclude,
    string SshdDropIn = SshdConfig.DropInPath,
    string GroupPath = "/etc/group",
    string? TlsDirectory = null,
    int FtpPort = 21,
    (int First, int Last)? FtpPassivePorts = null);

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
/// <param name="SshEnabled">Whether users with the SSH login right may sign in over SSH with a shell. Off by default.</param>
/// <param name="WebDavEnabled">Whether WebDAV is switched on, at <c>/webdav/</c> of the daemon's address. Off by default.</param>
/// <param name="FtpEnabled">Whether FTP is switched on, with TLS (FTPS). Off by default.</param>
/// <param name="FtpPlainAllowed">Whether FTP also works without TLS, at the users' own risk. Off by default.</param>
/// <param name="Ftp">How FTP is doing.</param>
/// <param name="FtpPort">The FTP control port.</param>
/// <param name="FtpFingerprint">The SHA-256 fingerprint of the FTPS certificate, for clients that ask whether to trust it; null until FTP ran once.</param>
public sealed record FileServiceSettings(
    bool SmbEnabled,
    string Workgroup,
    bool SftpEnabled,
    FileServiceState Smb,
    FileServiceState Sftp,
    bool SshEnabled,
    bool WebDavEnabled,
    bool FtpEnabled,
    bool FtpPlainAllowed,
    FileServiceState Ftp,
    int FtpPort,
    string? FtpFingerprint);

/// <summary>Changes of the file services; fields left out stay.</summary>
/// <param name="SmbEnabled">Switches SMB on or off.</param>
/// <param name="Workgroup">A new SMB workgroup.</param>
/// <param name="SftpEnabled">Switches SFTP on or off.</param>
/// <param name="SshEnabled">Switches SSH logins with a shell on or off for users with the SSH login right.</param>
/// <param name="WebDavEnabled">Switches WebDAV on or off.</param>
/// <param name="FtpEnabled">Switches FTP (FTPS) on or off.</param>
/// <param name="FtpPlainAllowed">Allows or refuses plain FTP without TLS; refusing it ends every plain session.</param>
public sealed record FileServiceSettingsRequest(
    bool? SmbEnabled = null,
    string? Workgroup = null,
    bool? SftpEnabled = null,
    bool? SshEnabled = null,
    bool? WebDavEnabled = null,
    bool? FtpEnabled = null,
    bool? FtpPlainAllowed = null);
