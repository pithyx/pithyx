using System.Text.Json;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>What the box does when a check finds a newer version of an app (D329).</summary>
public enum UpdatePolicy
{
    /// <summary>Installs it as a job of the box, unless it asks for new permissions or elevated rights or the app has a critical right (D388).</summary>
    Automatic,

    /// <summary>Tells the administrators on their desktops.</summary>
    Notify,

    /// <summary>Only lists it in the Store's tab Updates.</summary>
    None,
}

/// <summary>Which branch of every catalog the box reads: <c>catalog</c> (stable) or <c>catalog-next</c> (beta).</summary>
public enum StoreChannel
{
    Stable,
    Beta,
}

/// <summary>Whose catalog it is: the project's two (signed, keys built in) or one an administrator added (unverified).</summary>
public enum CatalogKind
{
    Official,
    Community,
    Custom,
}

/// <summary>A catalog's state for the Store's settings.</summary>
public enum CatalogStatus
{
    /// <summary>Switched off.</summary>
    Off,

    /// <summary>Never fetched yet.</summary>
    New,

    /// <summary>The last fetch verified the copy.</summary>
    Ok,

    /// <summary>The last fetch failed; the last verified copy, if any, is still used.</summary>
    Failed,
}

/// <summary>The Store's settings (D329).</summary>
/// <param name="Policy">What a check does with newer versions; each app may override it.</param>
/// <param name="Time">The daily check time as HH:mm in the box's time zone; the box adds its own delay of up to an hour.</param>
/// <param name="Channel">The branch every catalog is read from.</param>
/// <param name="NextCheckAt">When the next check runs (UTC), the box's delay included.</param>
/// <param name="LastCheckAt">When the last check ran (UTC), if one did.</param>
public sealed record StoreSettings(UpdatePolicy Policy, string Time, StoreChannel Channel, DateTimeOffset NextCheckAt, DateTimeOffset? LastCheckAt);

/// <param name="Policy">What a check does with newer versions.</param>
/// <param name="Time">HH:mm in the box's time zone.</param>
/// <param name="Channel">stable or beta.</param>
public sealed record StoreSettingsRequest(UpdatePolicy Policy, string Time, StoreChannel Channel);

/// <summary>A catalog of the box.</summary>
/// <param name="Key">The box's number for it, which the catalog endpoints take.</param>
/// <param name="Id">The id from its <c>catalog.json</c>, or null before a custom catalog's first fetch.</param>
/// <param name="Kind">The project's official or community catalog, or a custom one.</param>
/// <param name="Url">The repository.</param>
/// <param name="Enabled">Switched on.</param>
/// <param name="Verified">Its index must carry a signature by a key built into the box (the project's catalogs).</param>
/// <param name="Name">Its name by language from <c>catalog.json</c>, once fetched.</param>
/// <param name="Description">Its description by language, if it has one.</param>
/// <param name="Homepage">Its homepage, if it names one.</param>
/// <param name="SshPublicKey">The deploy key the box made for an SSH URL; add it to the repository with read access.</param>
/// <param name="HostKey">The server's host key the box pinned (for hosts other than GitHub, whose keys are built in).</param>
/// <param name="HasRegistryToken">A read token for its private images is set (never sent back).</param>
/// <param name="Status">Off, new, ok or failed.</param>
/// <param name="CheckedAt">When the last fetch ended.</param>
/// <param name="VerifiedAt">When a fetch last verified the copy.</param>
/// <param name="Commit">The commit of the verified copy.</param>
/// <param name="Sequence">The verified index's sequence on the box's channel.</param>
/// <param name="AppCount">How many apps the verified copy lists.</param>
/// <param name="ErrorCode">Why the last fetch failed.</param>
/// <param name="ErrorParams">The error's params.</param>
/// <param name="Busy">A fetch runs.</param>
public sealed record CatalogInfo(
    long Key,
    string? Id,
    CatalogKind Kind,
    string Url,
    bool Enabled,
    bool Verified,
    IReadOnlyDictionary<string, string>? Name,
    IReadOnlyDictionary<string, string>? Description,
    string? Homepage,
    string? SshPublicKey,
    string? HostKey,
    bool HasRegistryToken,
    CatalogStatus Status,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? VerifiedAt,
    string? Commit,
    long? Sequence,
    int AppCount,
    string? ErrorCode,
    JsonElement? ErrorParams,
    bool Busy);

/// <param name="Url">https://host/owner/repository.git, or for a private catalog git@host:owner/repository.git.</param>
public sealed record AddCatalogRequest(string Url);

/// <param name="Enabled">Switches the catalog on or off; null leaves it.</param>
/// <param name="RegistryToken">user:token to pull the catalog's private images; empty removes it, null leaves it.</param>
public sealed record UpdateCatalogRequest(bool? Enabled = null, string? RegistryToken = null);

/// <summary>An app of a catalog, at the newest version this box can run.</summary>
/// <param name="Catalog">The catalog's id.</param>
/// <param name="Kind">The catalog's kind; custom ones are unverified.</param>
/// <param name="Id">The app id.</param>
/// <param name="Version">The newest version for this box.</param>
/// <param name="Name">Names by language.</param>
/// <param name="Description">Descriptions by language, if any.</param>
/// <param name="Author">The author's name, if the manifest has one.</param>
/// <param name="License">The SPDX license.</param>
/// <param name="Icon">The icon's URL.</param>
/// <param name="InstalledVersion">The installed version, if the app is installed.</param>
/// <param name="Update">The installed app is older than <paramref name="Version"/>.</param>
/// <param name="Categories">Its categories from the manifest (D389), empty without.</param>
/// <param name="Keywords">Its search words from the manifest, empty without.</param>
/// <param name="Maturity">Its maturity: <c>stable</c> (also without the field), <c>beta</c> or <c>experimental</c>.</param>
public sealed record StoreApp(
    string Catalog,
    CatalogKind Kind,
    string Id,
    string Version,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string>? Description,
    string? Author,
    string License,
    string Icon,
    string? InstalledVersion,
    bool Update,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Keywords,
    string Maturity);

/// <summary>Apps of the enabled catalogs, sorted by name.</summary>
public sealed record StoreAppList(IReadOnlyList<StoreApp> Apps);

/// <summary>A published version of a catalog app.</summary>
/// <param name="Version">The version.</param>
/// <param name="PublishedAt">When the catalog published it.</param>
/// <param name="Pithyx">The range of Pithyx versions it runs on.</param>
/// <param name="Fits">This box can install it (its version and architecture).</param>
/// <param name="DataVersion">Its data version, if it declares one.</param>
public sealed record StoreAppVersion(string Version, DateTimeOffset PublishedAt, string Pithyx, bool Fits, int? DataVersion);

/// <summary>The detail page of a catalog app.</summary>
/// <param name="App">The app at its newest fitting version.</param>
/// <param name="Manifest">That version's manifest.</param>
/// <param name="Screenshots">URLs of its screenshots, in order.</param>
/// <param name="Homepage">The manifest's homepage, if any.</param>
/// <param name="Source">Where its source is: the catalog repository at the commit CI built, in the app's folder.</param>
/// <param name="Versions">Every published version, newest first.</param>
public sealed record StoreAppDetails(StoreApp App, JsonElement Manifest, IReadOnlyList<string> Screenshots, string? Homepage, string Source, IReadOnlyList<StoreAppVersion> Versions);

/// <summary>A newer version of an installed app that a catalog offers.</summary>
/// <param name="Id">The app.</param>
/// <param name="InstalledVersion">The installed version.</param>
/// <param name="Version">The newest version for this box.</param>
/// <param name="Catalog">The catalog that offers it.</param>
/// <param name="AddedPermissions">Permissions the new version asks for that the installed one did not.</param>
/// <param name="Elevated">It asks for root in a container or elevated rights it did not have.</param>
/// <param name="Policy">The app's own policy, or the box's.</param>
/// <param name="Automatic">The next check installs it on its own: the policy is automatic, nothing new is asked for and the app has no critical right.</param>
/// <param name="Critical">The app has a critical right (root on the box) or the new version asks for one, so it never updates by itself whatever the policy (D388).</param>
/// <param name="ReleaseNotes">What is new in the new version by language, from its manifest's <c>releaseNotes</c> (D389), if it has them.</param>
public sealed record AppUpdate(string Id, string InstalledVersion, string Version, string Catalog, IReadOnlyList<string> AddedPermissions, bool Elevated, UpdatePolicy Policy, bool Automatic, bool Critical, IReadOnlyDictionary<string, string>? ReleaseNotes);

/// <summary>The Store for administrators: the snapshot and every event of the topic <c>store/admin</c>.</summary>
/// <param name="Settings">The settings with the next check.</param>
/// <param name="Catalogs">Every catalog, the project's first.</param>
/// <param name="Updates">Newer versions of installed apps, by app id.</param>
public sealed record StoreState(StoreSettings Settings, IReadOnlyList<CatalogInfo> Catalogs, IReadOnlyList<AppUpdate> Updates);

/// <param name="Policy">The app's own update policy; null follows the box's.</param>
public sealed record AppUpdatePolicyRequest(UpdatePolicy? Policy);
