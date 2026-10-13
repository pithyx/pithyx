using System.Globalization;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>
/// The ZFS commands for homes (D105, D348): one dataset per home below <c>&lt;pool&gt;/pithyx/homes</c>, mounted at
/// <c>/&lt;pool&gt;/homes/&lt;folder&gt;</c> through it. Every command names exactly one home dataset (or one of its
/// snapshots), never forces and never reaches another dataset.
/// </summary>
public static partial class HomePlans
{
    /// <summary>Moving a home to another pool copies a whole home at once; a large one takes hours.</summary>
    public static readonly TimeSpan MoveTimeout = TimeSpan.FromHours(24);

    public static string HomesDataset(string pool) => $"{Pool(pool)}/pithyx/homes";

    /// <summary>The dataset of a home; <paramref name="folder"/> is the user name or the folder a deleted user's home kept.</summary>
    public static string Dataset(string pool, string folder) =>
        IsFolder(folder) ? $"{HomesDataset(pool)}/{folder}" : throw new ArgumentException($"Not a home folder: {folder}.", nameof(folder));

    /// <summary>Whether a name can be a home's folder and dataset: a user name, or what a deleted user's home was renamed to.</summary>
    public static bool IsFolder(string? folder) => folder is not null && Folder().IsMatch(folder);

    public static CommandRequest Create(string pool, string folder) => new("zfs", ["create", Dataset(pool, folder)]);

    /// <summary>Renames a home below the same <c>homes</c>; ZFS mounts it again under its new name.</summary>
    public static CommandRequest Rename(string pool, string folder, string newFolder) =>
        new("zfs", ["rename", Dataset(pool, folder), Dataset(pool, newFolder)], TimeSpan.FromMinutes(5));

    /// <summary>Sets the quota of a home in bytes, or removes it with null.</summary>
    public static CommandRequest SetQuota(string dataset, long? bytes) =>
        new("zfs", ["set", bytes is { } value ? $"quota={value.ToString(CultureInfo.InvariantCulture)}" : "quota=none", Home(dataset)]);

    /// <summary>Destroys a home with its snapshots, after the administrator typed its name.</summary>
    public static CommandRequest Destroy(string dataset) => new("zfs", ["destroy", "-r", Home(dataset)], TimeSpan.FromMinutes(10));

    /// <summary>Makes a home read-only while it is copied to another pool, so nothing written after its snapshot is lost.</summary>
    public static CommandRequest SetReadOnly(string dataset, bool readOnly) =>
        readOnly ? new("zfs", ["set", "readonly=on", Home(dataset)]) : new("zfs", ["inherit", "readonly", Home(dataset)]);

    public static CommandRequest Snapshot(string dataset, string name) => new("zfs", ["snapshot", Full(dataset, name)]);

    public static CommandRequest DestroySnapshot(string dataset, string name) => new("zfs", ["destroy", Full(dataset, name)]);

    /// <summary>
    /// Copies a home's snapshot to its new dataset on another pool, without its properties: the new home inherits its
    /// mount point from the target's <c>homes</c>, and the quota is set again afterwards. <c>-u</c> leaves it unmounted
    /// until its row points there.
    /// </summary>
    public static CommandRequest SendReceive(string dataset, string snapshot, string target) =>
        new("zfs", ["send", Full(dataset, snapshot)], MoveTimeout, PipeTo: new CommandRequest("zfs", ["receive", "-u", Home(target)], MoveTimeout));

    public static CommandRequest Mount(string dataset) => new("zfs", ["mount", Home(dataset)]);

    private static string Full(string dataset, string name) =>
        Storage.Snapshots.SnapshotNames.KindOf(name) is not null ? $"{Home(dataset)}@{name}" : throw new ArgumentException($"Not a Pithyx snapshot: {name}.", nameof(name));

    private static string Home(string dataset)
    {
        var parts = dataset?.Split('/') ?? [];
        return parts is [var pool, "pithyx", "homes", var folder] && PoolName().IsMatch(pool) && IsFolder(folder)
            ? dataset!
            : throw new ArgumentException($"Not a home dataset: {dataset}.", nameof(dataset));
    }

    private static string Pool(string pool) =>
        PoolName().IsMatch(pool ?? "") ? pool! : throw new ArgumentException($"Not a pool name: {pool}.", nameof(pool));

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex PoolName();

    // User names (^[a-z_][a-z0-9_-]{0,31}$), the folders deleted users' homes keep (<name>.deleted-<uid>) and what ZFS
    // allows below homes otherwise, so a dataset found there can be listed.
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.:-]{0,63}$")]
    private static partial Regex Folder();
}
