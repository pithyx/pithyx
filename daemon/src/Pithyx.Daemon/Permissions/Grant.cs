using System.Globalization;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Permissions;

/// <summary>Who a grant is for. Apps join in M4; until then nothing grants to them.</summary>
public enum SubjectKind
{
    User,
    Group,
    App,
}

/// <summary>One right of one subject (D290). Shares take <c>read</c> or <c>readWrite</c>, everything else <c>allow</c>.</summary>
/// <param name="SubjectKind">A user, a group or an app.</param>
/// <param name="SubjectId">The user's, group's or app's id.</param>
/// <param name="Resource">See <see cref="Resources"/>.</param>
/// <param name="Level">The access to a share, or <see cref="ShareAccess.ReadWrite"/> for everything else (allowed).</param>
public sealed record Grant(SubjectKind SubjectKind, long SubjectId, string Resource, ShareAccess Level);

/// <summary>
/// Whose rights to resolve: a user with all its groups (the built-in group <c>users</c> included), or later an app.
/// </summary>
/// <param name="Kind">A user or an app.</param>
/// <param name="Id">Its id.</param>
/// <param name="GroupIds">Every group it is a member of.</param>
/// <param name="Active">A deactivated user has no rights at all.</param>
public sealed record Principal(SubjectKind Kind, long Id, IReadOnlyList<long> GroupIds, bool Active = true);

/// <summary>The resources a grant can name.</summary>
public static class Resources
{
    public const string Admin = "right:admin";
    public const string DesktopLogin = "right:desktopLogin";
    public const string SshLogin = "right:sshLogin";
    public const string Power = "right:power";
    public const string Smb = "protocol:smb";
    public const string Sftp = "protocol:sftp";
    public const string WebDav = "protocol:webdav";
    public const string Ftp = "protocol:ftp";

    public const string SharePrefix = "share:";
    public const string AppPrefix = "app:";

    /// <summary>The special rights, in the order the editors show them.</summary>
    public static readonly IReadOnlyList<string> Rights = [Admin, DesktopLogin, SshLogin, Power];

    /// <summary>The file protocols, in the order the editors show them.</summary>
    public static readonly IReadOnlyList<string> Protocols = [Smb, Sftp, WebDav, Ftp];

    /// <summary>What a new user gets unless the administrator chooses otherwise: the desktop, SMB and the apps.</summary>
    public static readonly IReadOnlyList<string> NewUserDefaults = [DesktopLogin, Smb, .. CoreApps.Granted.Select(App)];

    public static string Share(long shareId) => SharePrefix + shareId.ToString(CultureInfo.InvariantCulture);

    public static string App(string appId) => AppPrefix + appId;

    /// <summary>The share id of a share resource, or null for any other resource.</summary>
    public static long? ShareId(string resource) =>
        resource.StartsWith(SharePrefix, StringComparison.Ordinal)
        && long.TryParse(resource.AsSpan(SharePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0
            ? id
            : null;

    /// <summary>Whether a resource other than a share is known.</summary>
    public static bool IsKnown(string resource) =>
        Rights.Contains(resource) || Protocols.Contains(resource) || (resource.StartsWith(AppPrefix, StringComparison.Ordinal) && CoreApps.Granted.Contains(resource[AppPrefix.Length..]));
}
