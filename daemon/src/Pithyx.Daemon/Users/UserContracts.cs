namespace Pithyx.Daemon.Users;

/// <summary>A user as the Users page shows it. Administrators only.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Username">The Linux user name, lower case.</param>
/// <param name="DisplayName">Shown instead of the user name, if set.</param>
/// <param name="IsAdmin">Whether the user has the administrator right, its own or through a group; administrators have every right.</param>
/// <param name="Uid">The Linux uid from the Pithyx range (20000 to 29999), never reused.</param>
/// <param name="TotpEnabled">Whether the user signs in with a second factor.</param>
/// <param name="CreatedAt">When the user was created, UTC.</param>
/// <param name="Home">The user's home folder on a pool, or null when there is no pool yet.</param>
/// <param name="Disabled">Deactivated: cannot sign in, and the Linux account is expired.</param>
/// <param name="Rights">The special rights the user has (<c>right:admin</c>, <c>right:desktopLogin</c>, <c>right:sshLogin</c>, <c>right:power</c>), its own and through its groups; a deactivated user gets them back when activated.</param>
/// <param name="Protocols">The file protocols the user may use (<c>protocol:smb</c>, <c>protocol:sftp</c>, <c>protocol:webdav</c>, <c>protocol:ftp</c>), the same way.</param>
/// <param name="FileServicePassword">Whether SMB and SFTP know the user's password. They learn it when the user signs in on the web or an administrator sets it; until then the user cannot use them.</param>
/// <param name="GroupIds">The groups the user is a member of; the built-in group <c>users</c>, which has everybody, is left out.</param>
public sealed record UserSummary(
    long Id,
    string Username,
    string? DisplayName,
    bool IsAdmin,
    long? Uid,
    bool TotpEnabled,
    DateTimeOffset CreatedAt,
    string? Home,
    bool Disabled,
    IReadOnlyList<string> Rights,
    IReadOnlyList<string> Protocols,
    bool FileServicePassword,
    IReadOnlyList<long> GroupIds);

/// <param name="Username">Lowercase letters, digits, underscores and hyphens, starting with a letter or underscore, at most 32 characters; not a system name.</param>
/// <param name="Password">At least 8 characters and not one of the most common passwords.</param>
/// <param name="DisplayName">Optional, at most 64 characters.</param>
/// <param name="GroupIds">The groups the user joins, besides the built-in group <c>users</c>.</param>
/// <param name="Grants">The user's own grants; when left out, the desktop login, SMB and the apps files, viewer and demo.</param>
public sealed record NewUser(string Username, string Password, string? DisplayName = null, IReadOnlyList<long>? GroupIds = null, IReadOnlyList<Permissions.GrantEntry>? Grants = null);

/// <summary>Fields left out stay as they are.</summary>
/// <param name="DisplayName">An empty text removes the display name.</param>
/// <param name="Disabled">Deactivates or activates the user; deactivating ends the user's sessions. Nobody deactivates themselves or the last active administrator.</param>
/// <param name="GroupIds">Every group the user should be a member of, besides the built-in group <c>users</c>; the user leaves the others.</param>
public sealed record UserUpdate(string? DisplayName = null, bool? Disabled = null, IReadOnlyList<long>? GroupIds = null);

/// <param name="Password">The new password, with the same rules as everywhere.</param>
public sealed record PasswordReset(string Password);
