using System.Text.Json;

namespace Pithyx.Daemon.Apps;

/// <summary>Body of <c>POST /api/apps/{appId}/tokens</c>.</summary>
/// <param name="Kind"><c>host</c> for the daemon's API for apps (kept by the desktop), <c>backend</c> for the app's backend.</param>
/// <param name="Instance">The window instance: 1 to 64 letters, digits, dashes and underscores.</param>
public sealed record AppTokenRequest(AppTokenKind Kind, string Instance);

/// <summary>A token for one app, user, session and window.</summary>
/// <param name="Token">Send as <c>Authorization: Bearer</c>; never log or store it.</param>
/// <param name="ExpiresAt">When it stops working (UTC); ask for a new one before.</param>
public sealed record AppToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Body of <c>POST /api/app/tickets</c>.</summary>
/// <param name="Endpoint">A backend endpoint of the app's manifest, for example <c>api</c>.</param>
public sealed record AppTicketRequest(string Endpoint);

/// <summary>A one-time ticket for one WebSocket to the app's backend, as <c>?pithyx-ticket=</c>.</summary>
/// <param name="Ticket">Works once, within 30 seconds.</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
public sealed record AppTicket(string Ticket, DateTimeOffset ExpiresAt);

/// <summary>The keys an app stored for the user.</summary>
/// <param name="Keys">Sorted.</param>
public sealed record AppStorageKeys(IReadOnlyList<string> Keys);

/// <summary>A value an app stores for the user; JSON null when there is none.</summary>
/// <param name="Value">Any JSON, at most 64 KB.</param>
public sealed record AppStorageValue(JsonElement Value);

/// <summary>A user of the box, for an app with <c>directory.read</c>. Deactivated users are left out.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Name">The user name.</param>
/// <param name="DisplayName">The name to show, if set.</param>
/// <param name="Groups">Ids of the user's groups, without the built-in group every user is in.</param>
public sealed record DirectoryUser(long Id, string Name, string? DisplayName, IReadOnlyList<long> Groups);

/// <summary>A group of the box, for an app with <c>directory.read</c>. The built-in group is left out.</summary>
/// <param name="Id">Stable id (the group's Linux gid).</param>
/// <param name="Name">The group name.</param>
/// <param name="Description">Free text, may be empty.</param>
public sealed record DirectoryGroup(long Id, string Name, string Description);

/// <summary>A shared folder the user can use, as an app with <c>files.shares</c> sees it: never its place on the host.</summary>
/// <param name="Id">The location <c>{ share: id }</c>.</param>
/// <param name="Name">Name shown to users.</param>
/// <param name="Access">The user's access: <c>read</c> or <c>readWrite</c>.</param>
/// <param name="Unavailable">Why its files cannot be reached now (<c>poolMissing</c>, <c>poolUnavailable</c>, <c>notMounted</c>), or null.</param>
public sealed record AppShareInfo(string Id, string Name, Shares.ShareAccess Access, string? Unavailable);

/// <summary>One page of a folder, relative to its location.</summary>
/// <param name="Path">The folder ("" for the location's root).</param>
/// <param name="FolderId">Stable id of the folder, also after a rename.</param>
/// <param name="Total">Entries in the folder when the page was read.</param>
/// <param name="Entries">This page, in the requested order.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; null on the last page.</param>
public sealed record AppFolderPage(string Path, string FolderId, int Total, IReadOnlyList<Files.FileEntry> Entries, string? NextCursor);

/// <summary>A file or folder an app reaches, with whether the app may write it.</summary>
/// <param name="Name">Name within its folder.</param>
/// <param name="Kind">File, folder, symlink or another kind.</param>
/// <param name="Size">Bytes for files, 0 for folders.</param>
/// <param name="Modified">Last change of the content, UTC.</param>
/// <param name="Writable">The app holds <c>files.write</c> (or the storage is mapped read and write) and the user may write there.</param>
public sealed record AppFileEntry(string Name, Files.FileKind Kind, long Size, DateTimeOffset Modified, bool Writable);

/// <summary>The state of a storage request of the manifest for the user.</summary>
public enum AppStorageState
{
    /// <summary>Mapped to a folder the user can reach.</summary>
    Ready,

    /// <summary>No administrator assigned a folder yet.</summary>
    Unmapped,

    /// <summary>Its pool is encrypted and locked.</summary>
    Locked,

    /// <summary>Its pool or folder is missing, or the user has no access to the shared folder.</summary>
    Unavailable,
}

/// <summary>A storage request of the manifest and where it stands for the user.</summary>
/// <param name="Id">The request's id; reach it as <c>{ storage: id }</c>.</param>
/// <param name="State">Whether it can be used.</param>
/// <param name="Access">What the user may do there through the app: the least of the request, the mapping and the user's own access; null while it cannot be used.</param>
public sealed record AppStorageInfo(string Id, AppStorageState State, Shares.ShareAccess? Access);

/// <summary>The app's backend: the state of its containers and the names of its endpoints.</summary>
/// <param name="Status"><c>stopped</c>, <c>starting</c>, <c>running</c>, <c>updating</c> or <c>unavailable</c> (a start failed, or a container is unhealthy or ended); the proxy reaches the backend while it is <c>running</c> or <c>unavailable</c> after a health check.</param>
/// <param name="Endpoints">The endpoint names of the manifest, sorted.</param>
public sealed record AppBackendInfo(string Status, IReadOnlyList<string> Endpoints);

/// <summary>
/// The per-user topic <c>apps/&lt;id&gt;/context</c>: what the daemon decides about an app for the subscriber, who may
/// use the app (only such a user can subscribe). Its subscription is also the window lease (A15): while a desktop of the
/// user is subscribed, the app is in use.
/// </summary>
/// <param name="Version">The installed version.</param>
/// <param name="Permissions">The permissions of the manifest that no administrator revoked; the app sees them as <c>permissionsChange</c>.</param>
/// <param name="LogLevel">Whether debug log entries reach the daemon (<c>logLevelChange</c>).</param>
/// <param name="Backend">The backend, or null for an app without one.</param>
/// <param name="Storage">The storage requests of the manifest with their state for the user (<c>storageChange</c>).</param>
public sealed record AppContextState(
    string Version,
    IReadOnlyList<string> Permissions,
    AppLogLevel LogLevel,
    AppBackendInfo? Backend,
    IReadOnlyList<AppStorageInfo> Storage);

