using System.Text.Json;

namespace Pithyx.Daemon.Backups;

/// <summary>How the box reaches a backup target. Only SFTP for now (#393); more kinds come later (#398).</summary>
public enum BackupTargetKind
{
    /// <summary>A server reached over SSH: SFTP for versioned backups, rsync over SSH for mirrors.</summary>
    Sftp,
}

/// <summary>A host key of a server.</summary>
/// <param name="Type">The key type, for example <c>ssh-ed25519</c>.</param>
/// <param name="Key">The key as <c>&lt;type&gt; &lt;base64&gt;</c>, the form a target stores and a request sends back.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint as OpenSSH prints it, <c>SHA256:...</c>.</param>
public sealed record BackupHostKey(string Type, string Key, string Fingerprint);

/// <summary>The outcome of a target's last connection test.</summary>
/// <param name="TestedAt">When it ran, UTC.</param>
/// <param name="Succeeded">The box signed in, created the folder if needed, and wrote and removed a test file.</param>
/// <param name="ErrorCode">Why it failed: <c>backup.target.unreachable</c>, <c>backup.target.host_key_changed</c>, <c>backup.target.auth_failed</c> or <c>backup.target.not_writable</c>.</param>
/// <param name="ErrorParams">The error's params.</param>
public sealed record BackupTargetTest(DateTimeOffset TestedAt, bool Succeeded, string? ErrorCode, JsonElement? ErrorParams);

/// <summary>The space of the file system the target's folder is on, as the server reported it over SFTP.</summary>
/// <param name="TotalBytes">Its size.</param>
/// <param name="FreeBytes">What the target's user can still write.</param>
/// <param name="MeasuredAt">When the server reported it: the last connection test or backup run, UTC.</param>
public sealed record BackupTargetSpace(long TotalBytes, long FreeBytes, DateTimeOffset MeasuredAt);

/// <summary>A place backups go to.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Name">The name the administrator gave it, unique on the box.</param>
/// <param name="Kind">How the box reaches it.</param>
/// <param name="Host">The server's name or address.</param>
/// <param name="Port">The SSH port.</param>
/// <param name="User">The user the box signs in as.</param>
/// <param name="Path">The folder on the server, relative to the user's login folder or absolute; empty for the login folder.</param>
/// <param name="HostKey">The host key the administrator confirmed; the box accepts no other.</param>
/// <param name="PublicKey">The public half of the target's own SSH key, the line for the server's <c>authorized_keys</c>.</param>
/// <param name="CreatedAt">When it was added, UTC.</param>
/// <param name="LastTest">The last connection test, or none yet.</param>
/// <param name="Space">The space on the server; null until a test or run measured it, or when the server does not report it.</param>
public sealed record BackupTarget(
    long Id,
    string Name,
    BackupTargetKind Kind,
    string Host,
    int Port,
    string User,
    string Path,
    BackupHostKey HostKey,
    string PublicKey,
    DateTimeOffset CreatedAt,
    BackupTargetTest? LastTest,
    BackupTargetSpace? Space);

/// <summary>Every backup target, by name.</summary>
public sealed record BackupTargetList(IReadOnlyList<BackupTarget> Targets);

/// <summary>A new target, or the changes to one.</summary>
/// <param name="Name">1 to 64 characters, unique on the box.</param>
/// <param name="Host">A host name, IPv4 or IPv6 address.</param>
/// <param name="User">The user on the server.</param>
/// <param name="Port">The SSH port; 22 when left out.</param>
/// <param name="Path">The folder on the server; empty or left out for the login folder.</param>
/// <param name="HostKey">
/// The host key the administrator confirmed, as <c>&lt;type&gt; &lt;base64&gt;</c> or a known_hosts line. Required for a
/// new target and when the host or port changes; left out, a change keeps the key.
/// </param>
public sealed record BackupTargetRequest(string Name, string Host, string User, int? Port = null, string? Path = null, string? HostKey = null);

/// <summary>A server whose host keys the box reads before a target is saved.</summary>
/// <param name="Host">A host name, IPv4 or IPv6 address.</param>
/// <param name="Port">The SSH port; 22 when left out.</param>
public sealed record BackupHostKeyScanRequest(string Host, int? Port = null);

/// <summary>The host keys the server offered, Ed25519 first.</summary>
public sealed record BackupHostKeyScan(IReadOnlyList<BackupHostKey> Keys);

/// <summary>The new host key of a target, after the administrator checked its fingerprint.</summary>
/// <param name="HostKey">As <c>&lt;type&gt; &lt;base64&gt;</c> or a known_hosts line.</param>
public sealed record BackupHostKeyRequest(string HostKey);
