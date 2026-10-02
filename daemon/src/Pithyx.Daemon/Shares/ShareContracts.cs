namespace Pithyx.Daemon.Shares;

public enum ShareAccess
{
    None,
    Read,
    ReadWrite,
}

/// <summary>A share the user can use.</summary>
/// <param name="Id">Stable id; file paths are relative to the share.</param>
/// <param name="Name">Name shown to users.</param>
/// <param name="Access">The user's access: read or read and write.</param>
/// <param name="Path">The share root on the host. Only administrators get it.</param>
/// <param name="Unavailable">Why its files cannot be reached now (<see cref="ShareAvailability"/>), or null when they can.</param>
public sealed record ShareInfo(long Id, string Name, ShareAccess Access, string? Path, string? Unavailable = null);

/// <param name="Name">Letters, digits, spaces, dots, dashes and underscores, 1 to 64 characters, not starting with a dot.</param>
/// <param name="Path">An existing folder on the host: absolute, without symlinks, outside the system directories and not overlapping another share.</param>
public sealed record NewShare(string Name, string Path);

/// <param name="Access"><c>none</c> removes the user's access.</param>
public sealed record ShareAccessRequest(ShareAccess Access);

/// <summary>A share resolved for one request, with the caller's access.</summary>
public sealed record ResolvedShare(long Id, string Name, string Path, ShareAccess Access);

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
/// <param name="ReadUsers">Users other than administrators who may read.</param>
/// <param name="ReadWriteUsers">Users other than administrators who may read and write.</param>
/// <param name="RestorePoints">Whether daily restore points (snapshots kept 7 days) are on; null for a folder share.</param>
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
    bool? RestorePoints);

/// <param name="Enabled">Daily restore points, kept 7 days, on or off.</param>
public sealed record RestorePointsRequest(bool Enabled);

/// <summary>One user's access to a share, for editing rights.</summary>
/// <param name="UserId">The user's id, for <c>PUT /api/shares/{shareId}/access/{userId}</c>.</param>
/// <param name="Username">Login name.</param>
/// <param name="DisplayName">Name shown, if set.</param>
/// <param name="Administrator">Administrators use every share with read and write access, whatever <paramref name="Access"/> says.</param>
/// <param name="Access">The access granted: <c>none</c>, <c>read</c> or <c>readWrite</c>.</param>
public sealed record ShareUserAccess(long UserId, string Username, string? DisplayName, bool Administrator, ShareAccess Access);

/// <summary>Changes of a share; fields left out stay. The dataset is never renamed by this.</summary>
/// <param name="Name">New name shown to users, unique like every share name.</param>
/// <param name="Description">New description, at most 200 characters, empty to remove it.</param>
public sealed record ShareUpdate(string? Name = null, string? Description = null);

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
