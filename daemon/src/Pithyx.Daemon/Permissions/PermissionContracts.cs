namespace Pithyx.Daemon.Permissions;

/// <summary>How far a grant goes: <c>read</c> or <c>readWrite</c> for a shared folder, <c>allow</c> for everything else.</summary>
public enum GrantLevel
{
    Read,
    ReadWrite,
    Allow,
}

/// <summary>One grant of a user or a group.</summary>
/// <param name="Resource"><c>share:&lt;id&gt;</c>, <c>protocol:smb|sftp|webdav|ftp</c>, <c>app:&lt;id&gt;</c> or <c>right:admin|desktopLogin|sshLogin|power</c>.</param>
/// <param name="Level"><c>read</c> or <c>readWrite</c> for a shared folder, <c>allow</c> for everything else.</param>
public sealed record GrantEntry(string Resource, GrantLevel Level);

/// <summary>A grant that reaches a user through one of its groups.</summary>
/// <param name="Resource">The resource, as in <see cref="GrantEntry"/>.</param>
/// <param name="Level">The level, as in <see cref="GrantEntry"/>.</param>
/// <param name="GroupId">The group that has the grant.</param>
/// <param name="GroupName">Its name.</param>
public sealed record InheritedGrant(string Resource, GrantLevel Level, long GroupId, string GroupName);

/// <summary>The rights of a user for the editor. Administrators only.</summary>
/// <param name="Grants">The user's own grants; the editor changes these.</param>
/// <param name="Inherited">What its groups give it; changed on the group.</param>
/// <param name="Effective">What the user may do: the union of both, or everything for an administrator.</param>
public sealed record UserGrants(IReadOnlyList<GrantEntry> Grants, IReadOnlyList<InheritedGrant> Inherited, SessionPermissions Effective);

/// <summary>The grants of a group. Administrators only.</summary>
/// <param name="Grants">Every grant of the group.</param>
public sealed record GroupGrants(IReadOnlyList<GrantEntry> Grants);

/// <param name="Grants">Every grant the subject should have; the others go. A subject has at most one grant per resource.</param>
public sealed record GrantsRequest(IReadOnlyList<GrantEntry> Grants);

/// <summary>A user's or a group's access to one shared folder.</summary>
/// <param name="SubjectKind"><c>user</c> or <c>group</c>.</param>
/// <param name="SubjectId">The user's or the group's id.</param>
/// <param name="Level"><c>read</c> or <c>readWrite</c>.</param>
public sealed record SubjectGrant(SubjectKind SubjectKind, long SubjectId, GrantLevel Level);

/// <summary>Who may use a shared folder. Administrators only.</summary>
/// <param name="Grants">The grants of users and groups on it; the editor changes these.</param>
/// <param name="Users">Every user with the access it ends up with, administrators and deactivated users included.</param>
public sealed record ShareGrants(IReadOnlyList<SubjectGrant> Grants, IReadOnlyList<ShareUserAccess> Users);

/// <param name="Grants">Every grant the shared folder should have; the others go.</param>
public sealed record ShareGrantsRequest(IReadOnlyList<SubjectGrant> Grants);

/// <summary>One user's effective access to a shared folder.</summary>
/// <param name="UserId">The user's id.</param>
/// <param name="Username">Login name.</param>
/// <param name="DisplayName">Name shown, if set.</param>
/// <param name="Administrator">Administrators use every shared folder with read and write.</param>
/// <param name="Disabled">Deactivated users use none.</param>
/// <param name="Access">What the user ends up with: <c>none</c>, <c>read</c> or <c>readWrite</c>.</param>
public sealed record ShareUserAccess(long UserId, string Username, string? DisplayName, bool Administrator, bool Disabled, Shares.ShareAccess Access);

/// <summary>What the signed-in user may do outside shared folders, for the desktop: which apps it shows and whether the power menu is there.</summary>
/// <param name="Admin">Administrators may do everything.</param>
/// <param name="Rights">The special rights: <c>right:admin</c>, <c>right:desktopLogin</c>, <c>right:sshLogin</c>, <c>right:power</c>.</param>
/// <param name="Protocols">The file protocols: <c>protocol:smb</c>, <c>protocol:sftp</c>, <c>protocol:webdav</c>, <c>protocol:ftp</c>.</param>
/// <param name="Apps">The ids of the apps of <see cref="PermissionCatalog.Apps"/> the user may open.</param>
public sealed record SessionPermissions(bool Admin, IReadOnlyList<string> Rights, IReadOnlyList<string> Protocols, IReadOnlyList<string> Apps);

/// <summary>Every resource besides shared folders a grant can name, in the order the editors show them.</summary>
/// <param name="Rights">The special rights.</param>
/// <param name="Protocols">The file protocols.</param>
/// <param name="Apps">The app ids given to users; the administration apps come with the administrator right.</param>
public sealed record PermissionCatalog(IReadOnlyList<string> Rights, IReadOnlyList<string> Protocols, IReadOnlyList<string> Apps);
