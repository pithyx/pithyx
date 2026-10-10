using System.Text.Json;

namespace Pithyx.Daemon.Apps;

/// <summary>Who may use an app after its first install (D304): everybody (a grant to the built-in group) or administrators only.</summary>
public enum AppAccess
{
    Everybody,
    Admins,
}

/// <summary>Whether an app's <c>debug</c> log entries reach the daemon (D305); <c>info</c>, <c>warn</c> and <c>error</c> always do.</summary>
public enum AppLogLevel
{
    Info,
    Debug,
}

/// <summary>How an app may use a folder mapped to one of its storage requests.</summary>
public enum AppStorageAccess
{
    Read,
    ReadWrite,
}

/// <summary>What installing a checked package does.</summary>
public enum AppPackageAction
{
    /// <summary>The app is not installed.</summary>
    Install,

    /// <summary>A newer version replaces the installed one.</summary>
    Update,

    /// <summary>The same version again.</summary>
    Reinstall,

    /// <summary>An older version replaces the installed one; its data may not fit.</summary>
    Downgrade,
}

/// <summary>A checked package, kept for 15 minutes for its installation by the administrator who uploaded it.</summary>
/// <param name="PackageId">Names it for the install.</param>
/// <param name="ExpiresAt">When it is discarded (UTC).</param>
/// <param name="Id">The app id.</param>
/// <param name="Version">The version in the package.</param>
/// <param name="Manifest">The whole manifest (<c>pithyx.json</c>).</param>
/// <param name="Action">What installing it does.</param>
/// <param name="InstalledVersion">The installed version, if the app is installed.</param>
/// <param name="AddedPermissions">Permissions the package asks for that the installed version did not (all of them for an install).</param>
/// <param name="RemovedPermissions">Permissions the installed version had that the package no longer asks for.</param>
/// <param name="Pool">The app pool, or null before the first app is installed (the install must name one then).</param>
/// <param name="FileCount">Regular files in the package.</param>
/// <param name="UnpackedBytes">Their sizes together.</param>
/// <param name="PackageBytes">The size of the package file.</param>
/// <param name="Sha256">SHA-256 of the package file, lowercase hex.</param>
/// <param name="SuggestedAccess">Who should use the app after an install (D304): administrators only when it asks for elevated rights, root in a container or <c>directory.read</c>, everybody otherwise. An install without access takes it.</param>
/// <param name="Catalog">The catalog the package comes from; null for an uploaded file (a sideload, unverified).</param>
public sealed record AppPackagePreview(
    string PackageId,
    DateTimeOffset ExpiresAt,
    string Id,
    string Version,
    JsonElement Manifest,
    AppPackageAction Action,
    string? InstalledVersion,
    IReadOnlyList<string> AddedPermissions,
    IReadOnlyList<string> RemovedPermissions,
    string? Pool,
    int FileCount,
    long UnpackedBytes,
    long PackageBytes,
    string Sha256,
    AppAccess SuggestedAccess,
    string? Catalog = null);

/// <param name="Pool">The pool for the app data; required for the first app, and then always the app pool.</param>
/// <param name="Access">Who may use the app after an install; an update keeps who may use it. Defaults to the preview's suggested access.</param>
public sealed record InstallAppRequest(string? Pool = null, AppAccess? Access = null);

/// <param name="Enabled">False hides the app from everybody and stops serving its frontend; its data and grants stay.</param>
public sealed record UpdateAppRequest(bool Enabled);

/// <summary>An installed app as a user who may use it sees it (topic <c>apps</c>).</summary>
/// <param name="Id">The app id.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Name">Names by language (<c>en</c> always).</param>
/// <param name="Description">Descriptions by language, if the manifest has one.</param>
/// <param name="Entry">The URL of the page the desktop loads in the sandboxed window, for example <c>/apps/com.example.chat/index.html</c>.</param>
/// <param name="Icon">The URL of the icon, changing with every version.</param>
/// <param name="Window">The window settings of the manifest.</param>
/// <param name="Permissions">The permissions the app holds: those of its manifest that no administrator revoked.</param>
/// <param name="LogLevel">Whether its debug log entries reach the daemon.</param>
/// <param name="FileHandlers">The files the app opens, for "Open with".</param>
public sealed record AppSummary(
    string Id,
    string Version,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string>? Description,
    string Entry,
    string Icon,
    JsonElement Window,
    IReadOnlyList<string> Permissions,
    AppLogLevel LogLevel,
    IReadOnlyList<AppFileHandler> FileHandlers);

/// <summary>The kinds of files an app opens (<c>fileHandlers</c> of its manifest).</summary>
/// <param name="Extensions">Lowercase extensions without the dot, for example <c>png</c> or <c>tar.gz</c>.</param>
/// <param name="IsDefault">Whether the app is the default for these files when it is the only handler.</param>
public sealed record AppFileHandler(IReadOnlyList<string> Extensions, bool IsDefault);

/// <summary>The apps the subscriber may use, sorted by id: the snapshot and every event of <c>apps</c>.</summary>
/// <param name="Apps">Enabled apps the user has a grant for; every enabled app for administrators.</param>
public sealed record AppList(IReadOnlyList<AppSummary> Apps);

/// <summary>An installed app for administrators (topic <c>apps/admin</c> and <c>GET /api/apps</c>).</summary>
/// <param name="Id">The app id.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Source"><c>sideload</c> (a package file) or <c>catalog</c>.</param>
/// <param name="Enabled">Whether users can open it.</param>
/// <param name="Manifest">The installed manifest.</param>
/// <param name="Permissions">Each permission of the manifest with whether it is granted.</param>
/// <param name="Uid">The app's own uid.</param>
/// <param name="Sha256">SHA-256 of the installed package file.</param>
/// <param name="InstalledAt">The first install (UTC).</param>
/// <param name="UpdatedAt">The last install or update (UTC).</param>
/// <param name="Busy">An install, update or removal runs.</param>
/// <param name="Available">Its data is reachable: the app pool is imported, unlocked and mounted.</param>
/// <param name="LogLevel">Whether its debug log entries reach the daemon.</param>
/// <param name="Storage">The folders mapped to the storage requests of its manifest; a request without one is not mapped.</param>
/// <param name="Catalog">The catalog it was installed or last updated from; null for a sideload.</param>
/// <param name="UpdatePolicy">Its own update policy; null follows the box's.</param>
/// <param name="PreviousVersion">The version before its last update, which a rollback restores with its data; null when there is none.</param>
public sealed record AppDetails(
    string Id,
    string Version,
    string Source,
    bool Enabled,
    JsonElement Manifest,
    IReadOnlyList<AppPermissionState> Permissions,
    long Uid,
    string Sha256,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    bool Busy,
    bool Available,
    AppLogLevel LogLevel,
    IReadOnlyList<AppStorageMapping> Storage,
    string? Catalog,
    Catalog.UpdatePolicy? UpdatePolicy,
    string? PreviousVersion);

/// <summary>A folder in a shared folder that an administrator mapped to one storage request of an app.</summary>
/// <param name="StorageId">The id of the request in the manifest.</param>
/// <param name="ShareId">The shared folder.</param>
/// <param name="Path">The folder relative to the shared folder's root, empty for the root itself.</param>
/// <param name="Access">Read-only, or read and write when the request asks for it.</param>
public sealed record AppStorageMapping(string StorageId, long ShareId, string Path, AppStorageAccess Access);

/// <param name="Permissions">Permissions of the app's manifest with whether the app keeps them; the others stay as they are.</param>
public sealed record AppPermissionsRequest(IReadOnlyList<AppPermissionState> Permissions);

/// <param name="ShareId">The shared folder.</param>
/// <param name="Access">Read-only, or read and write when the storage request asks for it.</param>
/// <param name="Path">An existing folder in it, relative to its root; empty or missing for the root.</param>
public sealed record AppStorageMappingRequest(long ShareId, AppStorageAccess Access, string? Path = null);

/// <param name="Level">Whether the app's debug log entries reach the daemon.</param>
public sealed record AppLogLevelRequest(AppLogLevel Level);

/// <summary>Who may use an app. Administrators only.</summary>
/// <param name="Grants">The grants of users and groups on it (level <c>allow</c>); the editor changes these. The built-in group <c>users</c> stands for everybody.</param>
/// <param name="Users">Every user with whether it may use the app in the end, administrators and deactivated users included.</param>
public sealed record AppGrants(IReadOnlyList<Permissions.SubjectGrant> Grants, IReadOnlyList<AppUserAccess> Users);

/// <param name="Grants">Every grant on the app the users and groups should have (level <c>allow</c>); the others go.</param>
public sealed record AppGrantsRequest(IReadOnlyList<Permissions.SubjectGrant> Grants);

/// <summary>One user's effective access to an app.</summary>
/// <param name="UserId">The user's id.</param>
/// <param name="Username">Login name.</param>
/// <param name="DisplayName">Name shown, if set.</param>
/// <param name="Administrator">Administrators use every app.</param>
/// <param name="Disabled">Deactivated users use none.</param>
/// <param name="Allowed">Whether the user may use the app: by its own grant, one of its groups', or as an administrator.</param>
public sealed record AppUserAccess(long UserId, string Username, string? DisplayName, bool Administrator, bool Disabled, bool Allowed);

/// <param name="Permission">For example <c>notifications</c>.</param>
/// <param name="Granted">False after an administrator revoked it.</param>
public sealed record AppPermissionState(string Permission, bool Granted);

/// <summary>Every installed app for administrators: the snapshot and every event of <c>apps/admin</c>.</summary>
/// <param name="Pool">The app pool, or null before the first install.</param>
/// <param name="Apps">Sorted by id.</param>
public sealed record AdminAppList(string? Pool, IReadOnlyList<AppDetails> Apps);
