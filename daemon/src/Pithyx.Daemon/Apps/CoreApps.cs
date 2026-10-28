using System.Collections.Frozen;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// The apps that ship with the desktop, under the reserved namespace <c>org.pithyx.*</c> (D302). Until A3 (migration
/// 0022) they had short ids such as <c>files</c>; <see cref="LegacyIds"/> keeps those for what was stored before. The
/// desktop has the same list in <c>desktop/src/apps/ids.ts</c>.
/// </summary>
public static class CoreApps
{
    public const string Files = "org.pithyx.files";
    public const string Viewer = "org.pithyx.viewer";
    public const string Storage = "org.pithyx.storage";
    public const string Settings = "org.pithyx.settings";
    public const string Monitor = "org.pithyx.monitor";
    public const string Logs = "org.pithyx.logs";
    public const string About = "org.pithyx.about";
    public const string Preferences = "org.pithyx.preferences";
    public const string Store = "org.pithyx.store";

    /// <summary>
    /// The core apps that are given to users by a grant. The administration apps (Storage, Settings, Resource Monitor,
    /// Log Center, Store) belong to the administrator right, and About and Personal settings are for everybody.
    /// </summary>
    public static readonly IReadOnlyList<string> Granted = [Files, Viewer];

    /// <summary>The core apps that come with the administrator right.</summary>
    /// <remarks>The Store manages apps for everybody and is for administrators only (D320); users find their apps in the menu.</remarks>
    public static readonly IReadOnlyList<string> Admin = [Storage, Settings, Monitor, Logs, Store];

    /// <summary>Every core app.</summary>
    public static readonly IReadOnlyList<string> All = [Files, Viewer, Storage, Settings, Monitor, Logs, About, Store, Preferences];

    // The core apps that existed before migration 0022; later ones never had a short id.
    private static readonly IReadOnlyList<string> _beforeSystemIds = [Files, Viewer, Storage, Settings, Monitor, Logs, About, Store];

    /// <summary>
    /// The short id each core app had before migration 0022, by that id. Log entries written before keep their source
    /// (<c>app/files</c>), so the Log Center treats both names as one app.
    /// </summary>
    public static readonly FrozenDictionary<string, string> LegacyIds =
        _beforeSystemIds.ToFrozenDictionary(id => id[Manifest.AppId.ReservedPrefix.Length..], StringComparer.Ordinal);

    /// <summary>The short id <paramref name="appId"/> had before migration 0022, or null for any other app.</summary>
    public static string? LegacyIdOf(string appId) =>
        _beforeSystemIds.Contains(appId) ? appId[Manifest.AppId.ReservedPrefix.Length..] : null;

    /// <summary>
    /// Whether the rights allow an app: given core apps and every third-party app by their grant, administration apps
    /// to administrators, the other core apps (About, Personal settings) to everybody. Whether a third-party app is installed and enabled
    /// is the registry's part (<see cref="AppRegistry.Allows"/>).
    /// </summary>
    public static bool Allows(EffectivePermissions rights, string appId)
    {
        ArgumentNullException.ThrowIfNull(rights);
        if (Admin.Contains(appId))
        {
            return rights.IsAdmin;
        }
        return (All.Contains(appId) && !Granted.Contains(appId)) || rights.Has(Resources.App(appId));
    }
}
