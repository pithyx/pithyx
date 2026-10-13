using System.Globalization;
using System.Text;

namespace Pithyx.Daemon.FileServices;

/// <summary>What an SMB share shows (D348).</summary>
public enum SmbShareKind
{
    /// <summary>A shared folder.</summary>
    Share,

    /// <summary><c>home</c>: every user's own home, the home directory of its account (<c>%H</c>).</summary>
    Home,

    /// <summary>
    /// <c>homes</c>: the folder of every home, for administrators. Samba's <c>[homes]</c> with a fixed path is an
    /// ordinary share; the share it would add per user name is hidden (<c>browseable = no</c> in <c>[global]</c>) and
    /// open to administrators only. Its homes are datasets of their own, so Samba could not move a deleted file into a
    /// recycle bin across them: it has none, and what is deleted there is gone.
    /// </summary>
    Homes,
}

/// <summary>A shared folder as SMB offers it.</summary>
/// <param name="Name">The share name clients see, the shared folder's name.</param>
/// <param name="Path">The dataset's mount point.</param>
/// <param name="Readers">User names with read access only.</param>
/// <param name="Writers">User names with read and write access, administrators included.</param>
/// <param name="Encrypt">Clients must encrypt.</param>
/// <param name="Kind">A shared folder, the users' homes or the folder of all homes.</param>
public sealed record SmbShare(string Name, string Path, IReadOnlyList<string> Readers, IReadOnlyList<string> Writers, bool Encrypt, SmbShareKind Kind = SmbShareKind.Share);

/// <summary>
/// Renders <c>/etc/samba/smb.conf</c> from SQLite (D287): SMB3 only, signing required, no guests, no NetBIOS, no
/// printers. ACL editing over SMB is off, so rights change only in Pithyx (D102); the POSIX ACLs of D286 decide what a
/// user may do. Each share hides the recycle bin (<c>veto files</c>), moves deletes into it with <c>vfs_recycle</c>
/// (<see cref="SmbRecycle"/> turns them into recycle bin items), shows the daily restore points as previous versions
/// (<c>shadow_copy2</c>) and reports writes and deletes to the journal (<c>full_audit</c>, read by
/// <see cref="FileServiceActivity"/>). Manual settings go into <see cref="LocalInclude"/>, which Pithyx never writes.
/// </summary>
public static class SmbConfig
{
    public const string ConfigPath = "/etc/samba/smb.conf";

    /// <summary>The owner's own settings, included last; created empty when missing, never written otherwise.</summary>
    public const string LocalInclude = "/etc/samba/pithyx.local.conf";

    public const string Header = "# Managed by pithyx. Pithyx writes this file again on every change; put your own settings into " + LocalInclude + ".";

    /// <summary>The folder below the recycle bin where Samba moves deleted files, one folder per user.</summary>
    public const string RecycleFolder = ".smb";

    /// <summary>The syslog identifier of <c>full_audit</c>.</summary>
    public const string AuditIdentifier = "smbd_audit";

    /// <summary>
    /// Section names Samba gives a meaning of its own, and the names of the homes (D348); a shared folder can never be
    /// named like this, and one from before is not offered over SMB.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(["global", "home", "homes", "printers", "print$", "ipc$"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The longest workgroup name (a NetBIOS name).</summary>
    public const int MaxWorkgroupLength = 15;

    public static bool IsValidWorkgroup(string? workgroup) =>
        workgroup is { Length: > 0 and <= MaxWorkgroupLength }
        && workgroup.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && char.IsAsciiLetterOrDigit(workgroup[0]);

    /// <summary>The NetBIOS name Samba announces: the host name in upper case, at most 15 characters (D337).</summary>
    public static string NetbiosName(string hostName)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        var name = hostName.Split('.')[0].ToUpperInvariant();
        return name.Length > MaxWorkgroupLength ? name[..MaxWorkgroupLength].TrimEnd('-') : name;
    }

    public static string Render(string workgroup, IReadOnlyList<SmbShare> shares, string? netbiosName = null)
    {
        ArgumentNullException.ThrowIfNull(shares);
        if (!IsValidWorkgroup(workgroup))
        {
            throw new ArgumentException("Invalid workgroup.", nameof(workgroup));
        }
        var text = new StringBuilder();
        text.AppendLine(Header);
        text.AppendLine();
        text.AppendLine("[global]");
        Line(text, "server role", "standalone server");
        Line(text, "workgroup", workgroup);
        if (!string.IsNullOrEmpty(netbiosName))
        {
            Line(text, "netbios name", netbiosName);
        }
        Line(text, "server string", "Pithyx");
        // Every share says browseable itself; the share Samba adds per user name from [homes] stays hidden.
        Line(text, "browseable", "no");
        Line(text, "server min protocol", "SMB3");
        Line(text, "server signing", "mandatory");
        Line(text, "map to guest", "never");
        Line(text, "restrict anonymous", "2");
        Line(text, "usershare max shares", "0");
        Line(text, "load printers", "no");
        Line(text, "printing", "bsd");
        Line(text, "printcap name", "/dev/null");
        Line(text, "disable spoolss", "yes");
        Line(text, "disable netbios", "yes");
        Line(text, "smb ports", "445");
        Line(text, "passdb backend", "tdbsam");
        // Deactivated users have an expired Linux account; PAM's account check refuses them.
        Line(text, "obey pam restrictions", "yes");
        Line(text, "unix password sync", "no");
        Line(text, "logging", "systemd");
        Line(text, "log level", "1");
        Line(text, "nt acl support", "no");
        Line(text, "store dos attributes", "yes");
        Line(text, "map archive", "no");
        Line(text, "map hidden", "no");
        Line(text, "map system", "no");
        Line(text, "map readonly", "no");
        Line(text, "follow symlinks", "yes");
        Line(text, "wide links", "no");
        Line(text, "vfs objects", "catia fruit streams_xattr");
        Line(text, "fruit:metadata", "stream");
        Line(text, "fruit:model", "MacSamba");
        Line(text, "fruit:posix_rename", "yes");
        Line(text, "fruit:nfs_aces", "no");
        Line(text, "fruit:wipe_intentionally_left_blank_rfork", "yes");
        Line(text, "fruit:delete_empty_adfiles", "yes");
        // Last in [global], so the owner's settings win; it may add shares of its own too.
        Line(text, "include", LocalInclude);
        var sections = shares.Where(share => share.Kind != SmbShareKind.Share).OrderBy(share => share.Kind)
            .Concat(shares.Where(share => share.Kind == SmbShareKind.Share && !ReservedNames.Contains(share.Name)).OrderBy(share => share.Name, StringComparer.OrdinalIgnoreCase));
        foreach (var share in sections)
        {
            text.AppendLine();
            text.Append('[').Append(share.Name).AppendLine("]");
            Line(text, "path", share.Path);
            // Nobody but the users with access sees or opens the share; their rights on the files come from the ACLs.
            Line(text, "browseable", "yes");
            Line(text, "access based share enum", "yes");
            Line(text, "valid users", Users(share.Readers.Concat(share.Writers)));
            Line(text, "read only", "yes");
            Line(text, "write list", Users(share.Writers));
            Line(text, "server smb encrypt", share.Encrypt ? "required" : "if_required");
            // The default ACL of D286 gives new files their rights; the group bits stay open so its mask allows the named
            // users. Files are never executable: "inherit acls" would create them with 0777.
            Line(text, "inherit acls", "no");
            Line(text, "create mask", "0660");
            Line(text, "directory mask", "0770");
            Line(text, "veto files", $"/{Files.FsPath.TrashFolder}/");
            Line(text, "delete veto files", "no");
            if (share.Kind == SmbShareKind.Homes)
            {
                Line(text, "vfs objects", "full_audit catia fruit streams_xattr shadow_copy2");
            }
            else
            {
                Line(text, "vfs objects", "full_audit catia fruit streams_xattr shadow_copy2 recycle");
                Line(text, "recycle:repository", $"{Files.FsPath.TrashFolder}/{RecycleFolder}/%U");
                Line(text, "recycle:keeptree", "yes");
                Line(text, "recycle:versions", "yes");
                Line(text, "recycle:touch", "no");
                Line(text, "recycle:directory_mode", "0700");
                Line(text, "recycle:subdir_mode", "0700");
            }
            Line(text, "shadow:snapdir", ".zfs/snapshot");
            Line(text, "shadow:format", $"{Storage.Snapshots.SnapshotNames.Prefix}{Storage.Snapshots.SnapshotKinds.Daily}-%Y%m%dT%H%M%SZ");
            Line(text, "shadow:localtime", "no");
            Line(text, "shadow:sort", "desc");
            Line(text, "full_audit:prefix", "%U|%S");
            Line(text, "full_audit:success", "pwrite_send unlinkat renameat mkdirat");
            Line(text, "full_audit:failure", "none");
            Line(text, "full_audit:facility", "LOCAL5");
            Line(text, "full_audit:priority", "DEBUG");
        }
        return text.ToString();
    }

    /// <summary>A user list for <c>valid users</c>: names quoted, so nothing in them is read as a group or a separator.</summary>
    private static string Users(IEnumerable<string> names)
    {
        var list = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(name => $"\"{name}\"").ToList();
        // An empty list would let every user in; a name that cannot exist lets nobody in.
        return list.Count == 0 ? "\"pithyx-nobody\"" : string.Join(' ', list);
    }

    private static void Line(StringBuilder text, string key, string value) =>
        text.Append(CultureInfo.InvariantCulture, $"   {key} = {value}").AppendLine();
}
