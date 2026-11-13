namespace Pithyx.Daemon.Shares;

public enum ShareAccess
{
    None,
    Read,
    ReadWrite,
}

/// <summary>What a share is (D348).</summary>
public enum ShareKind
{
    /// <summary>A shared folder.</summary>
    Share,

    /// <summary>The caller's own home, named <c>home</c>.</summary>
    Home,

    /// <summary>Another user's home, or one whose user was deleted, named after its folder. Administrators only.</summary>
    UserHome,

    /// <summary>
    /// The special folder Apps (D409): one folder per app on the app pool showing the app's volumes. Administrators by
    /// default; it has no recycle bin and no previous versions, and its top level holds only the app folders.
    /// </summary>
    Apps,
}

/// <summary>A share the user can use.</summary>
/// <param name="Id">Stable id; file paths are relative to the share.</param>
/// <param name="Name">Name shown to users: the shared folder's name, <c>home</c> for the caller's home, the folder name for another user's home.</param>
/// <param name="Access">The user's access: read or read and write.</param>
/// <param name="Path">The share root on the host. Only administrators get it.</param>
/// <param name="Unavailable">Why its files cannot be reached now (<see cref="ShareAvailability"/>), or null when they can.</param>
/// <param name="Kind">A shared folder, the caller's home or another user's home.</param>
/// <param name="PreviousVersions">Whether Files offers previous versions of its files (D411); false for Apps, which has none.</param>
public sealed record ShareInfo(long Id, string Name, ShareAccess Access, string? Path, string? Unavailable = null, ShareKind Kind = ShareKind.Share, bool PreviousVersions = true);

/// <param name="Name">Letters, digits, spaces, dots, dashes and underscores, 1 to 64 characters, not starting with a dot.</param>
/// <param name="Path">An existing folder on the host: absolute, without symlinks, outside the system directories and not overlapping another share.</param>
public sealed record NewShare(string Name, string Path);

/// <summary>A share resolved for one request, with the caller's access.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Name">Name shown to the caller.</param>
/// <param name="Path">The share root on the host.</param>
/// <param name="Access">The caller's access.</param>
/// <param name="Kind">A shared folder or a home, named as <see cref="ShareInfo"/> names it.</param>
/// <param name="PreviousVersions">Whether the share shows previous versions (D411).</param>
public sealed record ResolvedShare(long Id, string Name, string Path, ShareAccess Access, ShareKind Kind = ShareKind.Share, bool PreviousVersions = true);

/// <summary>A share with what the Shared folders page shows. Administrators only.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Name">Name shown to users.</param>
/// <param name="Description">Free text for administrators and users, may be empty.</param>
/// <param name="Path">The share root on the host.</param>
/// <param name="Pool">The pool of the share's dataset; null for a folder share.</param>
/// <param name="Dataset">Full name of the share's dataset, for example <c>tank/pithyx/shares/media</c>; null for a folder share (development only).</param>
/// <param name="UsedBytes">Space the dataset uses with its snapshots; null for a folder share or while the dataset is not there (pool missing).</param>
/// <param name="AvailableBytes">Space still available to the dataset; null like <paramref name="UsedBytes"/>.</param>
/// <param name="QuotaBytes">The dataset's quota, or null for none.</param>
/// <param name="ReadUsers">Active users other than administrators who may read, through their own grants or their groups.</param>
/// <param name="ReadWriteUsers">Active users other than administrators who may read and write, through their own grants or their groups.</param>
/// <param name="RestorePoints">Whether daily restore points (snapshots kept 7 days) are on; null for a folder share.</param>
/// <param name="Smb">Whether SMB offers the shared folder (dataset shares only).</param>
/// <param name="Sftp">Whether SFTP shows the shared folder (dataset shares only).</param>
/// <param name="SmbEncrypt">Whether SMB clients must encrypt.</param>
/// <param name="WebDav">Whether WebDAV offers the shared folder.</param>
/// <param name="Ftp">Whether FTP offers the shared folder.</param>
/// <param name="PreviousVersions">Whether SMB and Files show the daily restore points as previous versions (D411).</param>
/// <param name="TrashRetentionDays">Days the recycle bin keeps deleted items; 0 keeps them until it is emptied.</param>
/// <param name="System">Null for a shared folder; <c>apps</c> for the special folder Apps (D409), whose name, description and protocols other than SMB stay as they are.</param>
public sealed record ShareDetails(
    long Id,
    string Name,
    string Description,
    string Path,
    string? Pool,
    string? Dataset,
    long? UsedBytes,
    long? AvailableBytes,
    long? QuotaBytes,
    int ReadUsers,
    int ReadWriteUsers,
    bool? RestorePoints,
    bool Smb,
    bool Sftp,
    bool SmbEncrypt,
    bool WebDav,
    bool Ftp,
    bool PreviousVersions,
    int TrashRetentionDays,
    string? System = null);

/// <param name="Enabled">Daily restore points, kept 7 days, on or off.</param>
public sealed record RestorePointsRequest(bool Enabled);

/// <summary>Changes of a share; fields left out stay. The dataset is never renamed by this.</summary>
/// <param name="Name">New name shown to users, unique like every share name.</param>
/// <param name="Description">New description, at most 200 characters, empty to remove it.</param>
/// <param name="Smb">Offers the shared folder over SMB or stops it.</param>
/// <param name="Sftp">Shows the shared folder over SFTP or hides it.</param>
/// <param name="SmbEncrypt">Requires SMB clients to encrypt, or lets them choose.</param>
/// <param name="WebDav">Offers the shared folder over WebDAV or stops it.</param>
/// <param name="Ftp">Offers the shared folder over FTP or stops it.</param>
/// <param name="PreviousVersions">Shows the daily restore points as previous versions over SMB and in Files, or hides them.</param>
public sealed record ShareUpdate(string? Name = null, string? Description = null, bool? Smb = null, bool? Sftp = null, bool? SmbEncrypt = null, bool? WebDav = null, bool? Ftp = null, bool? PreviousVersions = null);

/// <summary>A user's space on a shared folder (D411).</summary>
/// <param name="UserId">The user.</param>
/// <param name="Username">Its name.</param>
/// <param name="DisplayName">Its display name, or null.</param>
/// <param name="UsedBytes">Space the files the user owns there take, as ZFS counts it; null until the box read it (or in simulation).</param>
/// <param name="QuotaBytes">The user's quota on the shared folder, or null for none.</param>
public sealed record ShareUserQuota(long UserId, string Username, string? DisplayName, long? UsedBytes, long? QuotaBytes);

/// <summary>The users of a shared folder with their space: everybody who may write, and everybody with a quota.</summary>
/// <param name="ShareId">The shared folder.</param>
/// <param name="MinQuotaBytes">The smallest quota.</param>
/// <param name="Users">By user name.</param>
public sealed record ShareQuotaList(long ShareId, long MinQuotaBytes, IReadOnlyList<ShareUserQuota> Users);

/// <param name="QuotaBytes">The user's quota in bytes, at least <see cref="ShareQuotaList.MinQuotaBytes"/>; null removes it.</param>
public sealed record ShareQuotaRequest(long? QuotaBytes);

/// <summary>Snapshot and event of <c>shares/quotas</c>.</summary>
/// <param name="Revision">Changes whenever a quota or the space of a user on a shared folder changed; compare it, do not count on it.</param>
public sealed record ShareQuotaRevision(long Revision);

public enum ShareDataChoice
{
    /// <summary>Unregister the share; the dataset or folder and its files stay.</summary>
    Keep,

    /// <summary>Destroy the share's dataset with its snapshots.</summary>
    Destroy,
}

/// <summary>
/// A shared folder action to plan. <c>share.create</c> needs Name and Pool (Description is optional);
/// <c>share.delete</c> needs ShareId, ConfirmName and Data.
/// </summary>
/// <param name="Action"><c>share.create</c> or <c>share.delete</c>.</param>
/// <param name="Name">Name of the new shared folder; its dataset name is derived from it and shown in the plan.</param>
/// <param name="Pool">Pool of the new shared folder; it needs the Pithyx layout (<c>&lt;pool&gt;/pithyx/shares</c>).</param>
/// <param name="Description">Description of the new shared folder.</param>
/// <param name="ShareId">The shared folder to delete.</param>
/// <param name="ConfirmName">The shared folder's name, typed by the user to delete it.</param>
/// <param name="Data"><c>keep</c> leaves the dataset or folder with its files, <c>destroy</c> destroys the dataset and its snapshots (dataset shares only).</param>
public sealed record SharePlanRequest(
    string Action,
    string? Name = null,
    string? Pool = null,
    string? Description = null,
    long? ShareId = null,
    string? ConfirmName = null,
    ShareDataChoice? Data = null);

/// <summary>A share row as stored, for the daemon's own checks.</summary>
public sealed record ShareRecord(long Id, string Name, string Path, string Description, string? Dataset);
