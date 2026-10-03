namespace Pithyx.Daemon.Backup;

/// <summary>Where the backup reads and writes outside the database. Simulation leaves the host's accounts and SSH alone.</summary>
/// <param name="Apply">Real mode: restore Linux and Samba passwords and SSH host keys, run the commands.</param>
/// <param name="TlsDirectory">The daemon's certificates.</param>
/// <param name="SshDirectory">Where the SSH host keys are; null in simulation.</param>
/// <param name="ShadowPath">The Linux password file; null in simulation.</param>
/// <param name="WorkDirectory">
/// The decrypted database of a backup between checking and restoring it; <c>/run/pithyx/backup</c> in real mode, so it
/// stays in memory and never reaches a disk.
/// </param>
public sealed record BackupOptions(bool Apply, string TlsDirectory, string? SshDirectory, string? ShadowPath, string WorkDirectory);

/// <param name="Password">The backup password: needed to restore, never stored. The same rules as for an account password.</param>
public sealed record CreateBackupRequest(string Password);

/// <param name="Archive">The backup file, base64.</param>
/// <param name="Password">The password it was created with.</param>
public sealed record InspectBackupRequest(string Archive, string Password);

/// <summary>A backup the daemon decrypted and checked, kept for a restore for 15 minutes.</summary>
/// <param name="RestoreId">Names the checked backup in the restore request.</param>
/// <param name="CreatedAt">When the backup was created, UTC.</param>
/// <param name="HostName">The host name of the box that created it.</param>
/// <param name="Version">The Pithyx version that created it.</param>
/// <param name="Categories">What it holds and can restore.</param>
/// <param name="Users">Users in the backup.</param>
/// <param name="Groups">Groups in the backup, without the built-in one.</param>
/// <param name="SharedFolders">Shared folders in the backup.</param>
public sealed record BackupSummary(
    string RestoreId,
    DateTimeOffset CreatedAt,
    string HostName,
    string Version,
    IReadOnlyList<BackupCategory> Categories,
    int Users,
    int Groups,
    int SharedFolders);

/// <param name="RestoreId">From the summary of the checked backup.</param>
/// <param name="Categories">The parts to restore; each replaces the current one completely.</param>
public sealed record RestoreBackupRequest(string RestoreId, IReadOnlyList<BackupCategory> Categories);

/// <summary>A step of a restore that did not work; the rest was restored.</summary>
/// <param name="Category">The part it belongs to.</param>
/// <param name="Step">
/// <c>accounts</c> (Linux accounts and groups), <c>linuxPasswords</c>, <c>sambaPasswords</c>, <c>time</c>,
/// <c>tls</c> or <c>sshKeys</c>.
/// </param>
public sealed record RestoreProblem(BackupCategory Category, string Step);

/// <param name="Restored">The parts that were restored.</param>
/// <param name="Problems">Steps that failed; details are in the Log Center.</param>
/// <param name="Restarting">The daemon restarts in a few seconds to load the restored configuration.</param>
public sealed record RestoreResult(IReadOnlyList<BackupCategory> Restored, IReadOnlyList<RestoreProblem> Problems, bool Restarting);

/// <summary>The <c>manifest.json</c> inside a backup.</summary>
/// <param name="Format">The content format, 1.</param>
/// <param name="CreatedAt">When it was created, UTC.</param>
/// <param name="HostName">The host name of the box.</param>
/// <param name="Version">The Pithyx version.</param>
/// <param name="SchemaVersion">The database schema version.</param>
/// <param name="Categories">What it holds.</param>
public sealed record BackupManifest(int Format, DateTimeOffset CreatedAt, string HostName, string Version, int SchemaVersion, IReadOnlyList<BackupCategory> Categories);
