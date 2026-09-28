using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Zfs;

/// <param name="Name">The dataset.</param>
/// <param name="MountPoint">Its mount point.</param>
/// <param name="Reason">
/// Why it stays unmounted: <c>system_path</c> (<c>/</c> or at or below a system directory), <c>shadows_mount</c> (it
/// would hide something mounted there or below), <c>duplicate_mountpoint</c> (another dataset of the pool mounts there
/// first) or <c>key_unavailable</c> (encrypted and locked).
/// </param>
public sealed record UnmountedDataset(string Name, string MountPoint, string Reason);

/// <param name="Mount">Datasets that are mounted, parents first.</param>
/// <param name="Unmounted">Datasets that would mount but stay unmounted, with the reason.</param>
public sealed record ImportMounts(IReadOnlyList<string> Mount, IReadOnlyList<UnmountedDataset> Unmounted);

/// <summary>
/// Proof that the user typed a name that had to be typed. Only <see cref="Confirm"/> makes one, so a plan builder that
/// needs it cannot add an override flag such as <c>-f</c> without the typed confirmation.
/// </summary>
public sealed class TypedName
{
    private TypedName(string name) => Name = name;

    public string Name { get; }

    /// <summary>The proof, or null when <paramref name="typed"/> is not exactly <paramref name="expected"/>.</summary>
    public static TypedName? Confirm(string expected, string? typed) =>
        typed is not null && string.Equals(typed.Trim(), expected, StringComparison.Ordinal) ? new TypedName(expected) : null;
}

/// <summary>
/// Scanning, importing and exporting pools. Only this class names <c>zpool import</c>. It never passes <c>-a</c>
/// (every pool) or <c>-D</c> (destroyed pools), and <c>-f</c> only with a <see cref="TypedName"/>.
/// </summary>
public static class ImportPlans
{
    /// <summary>Where imports look for devices: stable names that survive a change of kernel names.</summary>
    public const string SearchDirectory = "/dev/disk/by-id";

    /// <summary>Lists the pools that could be imported; imports nothing. Reads the labels of every disk, so it may wake one.</summary>
    public static readonly CommandRequest Scan = new("zpool", ["import", "-d", SearchDirectory], TimeSpan.FromMinutes(2));

    /// <summary>
    /// A dataset mounted at or below one of these would hide part of the system. Broader than the share roots in
    /// <see cref="Shares.ShareService"/>: a share may live below <c>/home</c> or <c>/srv</c>, but a mount there would
    /// hide what is in it.
    /// </summary>
    public static readonly IReadOnlyList<string> SystemDirectories =
        ["/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib32", "/lib64", "/opt", "/proc", "/root", "/run", "/sbin", "/srv", "/sys", "/tmp", "/usr", "/var"];

    /// <summary>What decides whether a dataset of the pool can be mounted safely.</summary>
    public static CommandRequest MountProperties(string pool) =>
        new("zfs", ["get", "-j", "--json-int", "-r", "-t", "filesystem", "canmount,mountpoint,encryption,keystatus", pool]);

    /// <summary>
    /// Imports a pool read-only without mounting anything, so its datasets can be looked at first. <c>cachefile=none</c>
    /// keeps it out of the pool cache, so it is not imported again (read-write) at the next boot.
    /// </summary>
    public static IReadOnlyList<PlanStep> ImportReadOnly(string poolGuid, string? newName, TypedName? force, IReadOnlyList<string> diskIds) =>
    [
        new PlanStep(
            new CommandRequest("zpool", ["import", "-o", "readonly=on", "-o", "cachefile=none", "-N", "-d", SearchDirectory, .. Force(force), poolGuid, .. newName is null ? [] : new[] { newName }], TimeSpan.FromMinutes(5)),
            diskIds,
            GuardRule.Import),
    ];

    /// <summary>
    /// Imports a pool that is imported read-only again, read-write: export, import by GUID under its current name, then
    /// mount the datasets that are safe to mount. Datasets that would hide part of the system get
    /// <c>canmount=noauto</c> first, so <c>zfs mount -a</c> at the next boot leaves them unmounted too.
    /// </summary>
    public static IReadOnlyList<PlanStep> ImportReadWrite(string name, string poolGuid, TypedName? force, IReadOnlyList<string> diskIds, ImportMounts mounts)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        var steps = new List<PlanStep>
        {
            new(new CommandRequest("zpool", ["export", name], TimeSpan.FromMinutes(5)), diskIds, GuardRule.Import, name),
            new(new CommandRequest("zpool", ["import", "-N", "-d", SearchDirectory, .. Force(force), poolGuid, name], TimeSpan.FromMinutes(5)), diskIds, GuardRule.Import, name),
        };
        steps.AddRange(mounts.Unmounted
            .Where(dataset => dataset.Reason != "key_unavailable")
            .Select(dataset => new PlanStep(new CommandRequest("zfs", ["set", "canmount=noauto", dataset.Name]), diskIds, GuardRule.Import, name)));
        steps.AddRange(mounts.Mount.Select(dataset => new PlanStep(new CommandRequest("zfs", ["mount", dataset]), diskIds, GuardRule.Import, name)));
        return steps;
    }

    /// <summary>Exports a pool; never forced, so ZFS refuses while a dataset is busy.</summary>
    public static IReadOnlyList<PlanStep> Export(string pool) =>
        [new PlanStep(new CommandRequest("zpool", ["export", pool], TimeSpan.FromMinutes(5)), [])];

    /// <summary>True when an import failed because ZFS wants <c>-f</c>: the pool is in use by, or was last used on, another system.</summary>
    public static bool AsksForForce(CommandRequest command, CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);
        return command.FileName == "zpool" && command.Arguments is ["import", ..] && result.StandardError.Contains("import -f", StringComparison.Ordinal);
    }

    /// <summary>
    /// Which datasets an import mounts, from <c>zfs get</c> of <see cref="MountProperties"/>. Only datasets with
    /// <c>canmount=on</c> and a mount point are considered; the rest never mount on their own anyway.
    /// </summary>
    /// <param name="properties">Property values by dataset, from <see cref="ZfsDatasetParser.ParseProperties"/>.</param>
    /// <param name="mounts">The mount table.</param>
    public static ImportMounts DecideMounts(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> properties, IReadOnlyList<MountEntry> mounts)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(mounts);
        var mount = new List<(string Name, string MountPoint)>();
        var unmounted = new List<UnmountedDataset>();
        var candidates = properties
            .Select(entry => (Name: entry.Key, Values: entry.Value, MountPoint: entry.Value.GetValueOrDefault("mountpoint")))
            .Where(entry => entry.Values.GetValueOrDefault("canmount", "on") == "on" && entry.MountPoint is not (null or "none" or "legacy"))
            .OrderBy(entry => entry.MountPoint, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal);
        foreach (var (name, values, mountPoint) in candidates)
        {
            var reason = !IsSafePath(mountPoint!) ? "system_path"
                : mounts.Any(existing => existing.Target != "/" && Files.FsPath.IsSameOrInside(existing.Target, mountPoint!)) ? "shadows_mount"
                : mount.Any(chosen => chosen.MountPoint == mountPoint) ? "duplicate_mountpoint"
                : values.GetValueOrDefault("encryption", "off") != "off" && values.GetValueOrDefault("keystatus") != "available" ? "key_unavailable"
                : null;
            if (reason is null)
            {
                mount.Add((name, mountPoint!));
            }
            else
            {
                unmounted.Add(new UnmountedDataset(name, mountPoint!, reason));
            }
        }
        return new ImportMounts([.. mount.Select(chosen => chosen.Name)], unmounted);
    }

    /// <summary>An absolute path without <c>.</c> or <c>..</c> that is neither <c>/</c> nor at or below a system directory.</summary>
    private static bool IsSafePath(string path)
    {
        var segments = path.Split('/');
        return path.StartsWith('/')
            && path != "/"
            && !segments.Any(segment => segment is "." or "..")
            && !SystemDirectories.Any(directory => Files.FsPath.IsSameOrInside(path.TrimEnd('/'), directory));
    }

    private static string[] Force(TypedName? force) => force is null ? [] : ["-f"];
}
