using System.Globalization;
using System.Text;
using System.Text.Json;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Updates;

/// <summary>A read-only snapshot of the system (<c>@</c> with <c>/var/lib/pithyx</c>), taken before an update.</summary>
/// <param name="Name">Its folder in <c>/@snapshots</c>, the UTC time it was taken (<c>20261009T214000Z</c>).</param>
/// <param name="Version">The Pithyx version it holds.</param>
/// <param name="CreatedAt">When it was taken.</param>
/// <param name="Kernel">The kernel release it booted with; its GRUB entry boots that kernel.</param>
/// <param name="Reason">Why it was taken, for example <c>update to 0.2.0</c>.</param>
public sealed record SnapshotInfo(string Name, string Version, DateTimeOffset CreatedAt, string Kernel, string Reason);

/// <summary>Whether this machine is laid out as the installer lays out a box (D295, D299), and its system file system.</summary>
/// <param name="Reason">Why updates cannot apply here: <c>not_btrfs</c>, <c>not_subvolume</c>, <c>not_array</c>; null on a box.</param>
/// <param name="FileSystemUuid">The UUID of the btrfs on the system array, which GRUB searches for.</param>
public sealed record SystemLayout(string? Reason, string? FileSystemUuid)
{
    public bool Supported => Reason is null;
}

/// <summary>
/// The pure parts of the system snapshots (D338): names, which to keep, the GRUB menu with an entry per snapshot, and
/// the check that the root is the installer's layout. Snapshots live beside <c>@</c> at the top of the btrfs, so they
/// are never inside each other and survive a rollback; the newest <see cref="Keep"/> stay.
/// </summary>
public static class SystemSnapshots
{
    public const string SystemArray = "/dev/md/pithyx-system";
    public const string Directory = "@snapshots";
    public const string Root = "@";
    public const string MenuFile = "menu.cfg";
    public const int Keep = 3;

    /// <summary>The GRUB entry of the automatic rollback that a trial boot falls back to (see <c>image/rootfs/etc/grub.d/01_pithyx</c>).</summary>
    public const string AutoRollbackEntry = "pithyx-rollback-auto";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Name(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static bool IsName(string name) =>
        name.Length == 16 && DateTime.TryParseExact(name, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>The snapshots to delete so the newest <paramref name="keep"/> stay; <paramref name="protect"/> never goes.</summary>
    public static IReadOnlyList<string> Prune(IEnumerable<SnapshotInfo> snapshots, int keep, string? protect)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return [.. snapshots
            .OrderByDescending(snapshot => snapshot.Name, StringComparer.Ordinal)
            .Skip(keep)
            .Where(snapshot => snapshot.Name != protect)
            .Select(snapshot => snapshot.Name)
            .Order(StringComparer.Ordinal)];
    }

    public static string Serialize(SnapshotInfo info) => JsonSerializer.Serialize(info, _json) + "\n";

    public static SnapshotInfo? Deserialize(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<SnapshotInfo>(text, _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>/@snapshots/menu.cfg</c>, which GRUB reads through <c>42_pithyx</c>: per snapshot an entry that boots its
    /// kernel read-only into the recovery menu with the rollback to it chosen, and, while an update waits for its
    /// health check, the automatic rollback to <paramref name="trialSnapshot"/> that a failed trial boot falls back to.
    /// </summary>
    public static string RenderMenu(IReadOnlyList<SnapshotInfo> snapshots, string fileSystemUuid, string? trialSnapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var builder = new StringBuilder("# Managed by pithyx: the system snapshots (D338), written after every snapshot change.\n");
        // The newest snapshot is key 1 on the console, the next 2, and so on.
        var number = 0;
        foreach (var snapshot in snapshots.OrderByDescending(snapshot => snapshot.Name, StringComparer.Ordinal))
        {
            number++;
            var title = $"Pithyx {snapshot.Version} from {snapshot.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC (roll back)";
            Entry(builder, title, $"pithyx-snapshot-{snapshot.Name} --hotkey={number}", snapshot, fileSystemUuid, $"pithyx.rollback={snapshot.Name}");
        }
        if (trialSnapshot is not null && snapshots.FirstOrDefault(snapshot => snapshot.Name == trialSnapshot) is { } trial)
        {
            Entry(builder, $"Pithyx {trial.Version} (automatic rollback after a failed update)", AutoRollbackEntry, trial, fileSystemUuid, $"pithyx.rollback=auto:{trial.Name}");
        }
        return builder.ToString();
    }

    private static void Entry(StringBuilder builder, string title, string id, SnapshotInfo snapshot, string uuid, string rollback)
    {
        var path = $"/{Directory}/{snapshot.Name}";
        builder.Append(CultureInfo.InvariantCulture, $"menuentry '{title}' --id {id} {{\n");
        builder.Append(CultureInfo.InvariantCulture, $"  search --no-floppy --fs-uuid --set=root {uuid}\n");
        builder.Append(CultureInfo.InvariantCulture,
            $"  linux {path}/boot/vmlinuz-{snapshot.Kernel} root=UUID={uuid} ro rootflags=subvol={Directory}/{snapshot.Name} systemd.unit=pithyx-recovery.target systemd.mask=systemd-remount-fs.service fstab=no {rollback}\n");
        builder.Append(CultureInfo.InvariantCulture, $"  initrd {path}/boot/initrd.img-{snapshot.Kernel}\n}}\n");
    }

    /// <summary>
    /// Checks <c>findmnt --json --output SOURCE,FSTYPE,OPTIONS,UUID --target /</c> against the installer's layout: btrfs,
    /// the subvolume <c>@</c>, on the system array (<paramref name="arrayDevice"/>, the kernel device that
    /// <c>/dev/md/pithyx-system</c> points to, or null when it does not exist).
    /// </summary>
    public static SystemLayout CheckLayout(string findmnt, string? arrayDevice)
    {
        ArgumentNullException.ThrowIfNull(findmnt);
        using var document = JsonDocument.Parse(findmnt);
        var root = document.RootElement.GetProperty("filesystems")[0];
        var source = root.GetProperty("source").GetString() ?? "";
        var options = (root.GetProperty("options").GetString() ?? "").Split(',');
        var uuid = root.TryGetProperty("uuid", out var value) ? value.GetString() : null;
        if (root.GetProperty("fstype").GetString() != "btrfs")
        {
            return new SystemLayout("not_btrfs", null);
        }
        if (!options.Contains("subvol=/@"))
        {
            return new SystemLayout("not_subvolume", uuid);
        }
        var device = source.Split('[')[0];
        return arrayDevice is not null && device == arrayDevice ? new SystemLayout(null, uuid) : new SystemLayout("not_array", uuid);
    }

    /// <summary>The commands that take, delete and roll back snapshots, with the top of the btrfs mounted at <c>top</c>.</summary>
    public static class Plans
    {
        private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);

        public static CommandRequest MountTop(string top) =>
            new("mount", ["-o", "subvolid=5,noatime", SystemArray, top], _timeout);

        public static CommandRequest UnmountTop(string top) => new("umount", [top], _timeout);

        public static CommandRequest Take(string top, string name) =>
            new("btrfs", ["subvolume", "snapshot", "-r", Path.Combine(top, Root), Path.Combine(top, Directory, name)], _timeout);

        public static CommandRequest Delete(string top, string subvolume) =>
            new("btrfs", ["subvolume", "delete", Path.Combine(top, subvolume)], _timeout);

        /// <summary>
        /// The rollback: <c>@</c> moves aside as <c>@rollback-&lt;time&gt;</c> (kept until the next update, for a look at
        /// what failed), and a writable snapshot of the chosen one becomes <c>@</c>, which fstab and GRUB mount by name.
        /// </summary>
        public static IReadOnlyList<CommandRequest> Rollback(string top, string snapshot, DateTimeOffset now) =>
        [
            new("mv", ["--no-target-directory", Path.Combine(top, Root), Path.Combine(top, $"@rollback-{Name(now)}")], _timeout),
            new("btrfs", ["subvolume", "snapshot", Path.Combine(top, Directory, snapshot), Path.Combine(top, Root)], _timeout),
        ];
    }
}
