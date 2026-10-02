using System.Globalization;
using System.Text;

namespace Pithyx.Daemon.FileServices;

/// <summary>A shared folder as SMB offers it.</summary>
/// <param name="Name">The share name clients see, the shared folder's name.</param>
/// <param name="Path">The dataset's mount point.</param>
/// <param name="Readers">User names with read access only.</param>
/// <param name="Writers">User names with read and write access, administrators included.</param>
/// <param name="Encrypt">Clients must encrypt.</param>
public sealed record SmbShare(string Name, string Path, IReadOnlyList<string> Readers, IReadOnlyList<string> Writers, bool Encrypt);

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

    /// <summary>Section names Samba gives a meaning of its own; a shared folder with such a name is not offered over SMB.</summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(["global", "homes", "printers", "print$", "ipc$"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The longest workgroup name (a NetBIOS name).</summary>
    public const int MaxWorkgroupLength = 15;

    public static bool IsValidWorkgroup(string? workgroup) =>
        workgroup is { Length: > 0 and <= MaxWorkgroupLength }
        && workgroup.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && char.IsAsciiLetterOrDigit(workgroup[0]);

    public static string Render(string workgroup, IReadOnlyList<SmbShare> shares)
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
        Line(text, "server string", "Pithyx");
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
        foreach (var share in shares.Where(share => !ReservedNames.Contains(share.Name)).OrderBy(share => share.Name, StringComparer.OrdinalIgnoreCase))
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
            // The default ACL of D286 decides the rights of new files; the group bits stay open so its mask allows them.
            Line(text, "inherit acls", "yes");
            Line(text, "create mask", "0770");
            Line(text, "directory mask", "0770");
            Line(text, "veto files", $"/{Files.FsPath.TrashFolder}/");
            Line(text, "delete veto files", "no");
            Line(text, "vfs objects", "full_audit catia fruit streams_xattr shadow_copy2 recycle");
            Line(text, "recycle:repository", $"{Files.FsPath.TrashFolder}/{RecycleFolder}/%U");
            Line(text, "recycle:keeptree", "yes");
            Line(text, "recycle:versions", "yes");
            Line(text, "recycle:touch", "no");
            Line(text, "recycle:directory_mode", "0700");
            Line(text, "recycle:subdir_mode", "0700");
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
