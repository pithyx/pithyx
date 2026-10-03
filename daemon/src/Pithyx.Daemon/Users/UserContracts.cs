namespace Pithyx.Daemon.Users;

/// <summary>A user as the Users page shows it. Administrators only.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Username">The Linux user name, lower case.</param>
/// <param name="DisplayName">Shown instead of the user name, if set.</param>
/// <param name="IsAdmin">Administrators have every right.</param>
/// <param name="Uid">The Linux uid from the Pithyx range (20000 to 29999), never reused.</param>
/// <param name="TotpEnabled">Whether the user signs in with a second factor.</param>
/// <param name="CreatedAt">When the user was created, UTC.</param>
/// <param name="Home">The user's home folder on a pool, or null when there is no pool yet.</param>
/// <param name="Disabled">Deactivated: cannot sign in, and the Linux account is expired.</param>
/// <param name="Smb">Whether the user may use SMB; administrators always may.</param>
/// <param name="Sftp">Whether the user may use SFTP; administrators always may.</param>
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
    bool Smb,
    bool Sftp,
    bool FileServicePassword,
    IReadOnlyList<long> GroupIds);

/// <param name="Username">Lowercase letters, digits, underscores and hyphens, starting with a letter or underscore, at most 32 characters; not a system name.</param>
/// <param name="Password">At least 8 characters and not one of the most common passwords.</param>
/// <param name="DisplayName">Optional, at most 64 characters.</param>
/// <param name="IsAdmin">Whether the user is an administrator.</param>
/// <param name="Smb">Whether the user may use SMB; on when left out.</param>
/// <param name="Sftp">Whether the user may use SFTP; off when left out.</param>
/// <param name="GroupIds">The groups the user joins, besides the built-in group <c>users</c>.</param>
public sealed record NewUser(string Username, string Password, string? DisplayName = null, bool IsAdmin = false, bool Smb = true, bool Sftp = false, IReadOnlyList<long>? GroupIds = null);

/// <summary>Fields left out stay as they are.</summary>
/// <param name="DisplayName">An empty text removes the display name.</param>
/// <param name="IsAdmin">The last administrator cannot lose the right.</param>
/// <param name="Disabled">Deactivates or activates the user; deactivating ends the user's sessions. Nobody deactivates themselves or the last active administrator.</param>
/// <param name="Smb">Allows or forbids SMB.</param>
/// <param name="Sftp">Allows or forbids SFTP.</param>
/// <param name="GroupIds">Every group the user should be a member of, besides the built-in group <c>users</c>; the user leaves the others.</param>
public sealed record UserUpdate(string? DisplayName = null, bool? IsAdmin = null, bool? Disabled = null, bool? Smb = null, bool? Sftp = null, IReadOnlyList<long>? GroupIds = null);

/// <param name="Password">The new password, with the same rules as everywhere.</param>
public sealed record PasswordReset(string Password);
