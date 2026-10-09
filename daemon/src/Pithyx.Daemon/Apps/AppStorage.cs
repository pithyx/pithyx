using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Apps;

/// <summary>Where app data lives.</summary>
/// <param name="UseZfs">
/// A dataset per app on the app pool (D301). Simulation has no ZFS: there each app is a folder below
/// <paramref name="FolderRoot"/><c>/&lt;pool&gt;/.pithyx/apps/&lt;id&gt;</c>, and updates take no snapshot.
/// </param>
/// <param name="FolderRoot">The stand-in for the pools' mount points in simulation.</param>
public sealed record AppStorageOptions(bool UseZfs, string FolderRoot);

/// <summary>
/// The app pool and the app datasets on it (D301): <c>&lt;pool&gt;/pithyx/apps/&lt;id&gt;</c>, mounted at
/// <c>/&lt;pool&gt;/.pithyx/apps/&lt;id&gt;/</c> with <c>versions/&lt;version&gt;</c>, <c>current</c>, <c>volumes/</c> and
/// <c>run/</c>. Every ZFS command comes from the plan builders and runs through <see cref="ICommandRunner"/>. Nothing of
/// an app is written to the system disk.
/// </summary>
public sealed class AppStorage(ICommandRunner commands, PoolInventory pools, SystemStateService state, AppStorageOptions options, TimeProvider time)
{
    public const string VersionsFolder = "versions";
    public const string CurrentLink = "current";
    public const string VolumesFolder = "volumes";
    public const string RunFolder = "run";

    /// <summary>Throws <see cref="ErrorCodes.AppPoolUnavailable"/> unless <paramref name="pool"/> can take app data now.</summary>
    public void RequirePool(string pool)
    {
        if (Unavailable(pools.Current, pool) is { } reason)
        {
            throw new PithyxException(ErrorCodes.AppPoolUnavailable, new Dictionary<string, object?> { ["pool"] = pool, ["reason"] = reason });
        }
    }

    /// <summary>Why <paramref name="pool"/> cannot take app data now, or null when it can.</summary>
    public string? Unavailable(PoolList current, string pool)
    {
        ArgumentNullException.ThrowIfNull(current);
        var found = current.Pools.FirstOrDefault(candidate => candidate.Name == pool);
        if (found is null)
        {
            return "missing";
        }
        if (found.State is not ("ONLINE" or "DEGRADED"))
        {
            return "offline";
        }
        if (found.ReadOnly)
        {
            return "read_only";
        }
        if (!options.UseZfs)
        {
            return null;
        }
        var apps = current.Datasets.FirstOrDefault(dataset => dataset.Name == $"{pool}/pithyx/apps");
        if (apps is null)
        {
            return "not_prepared";
        }
        return apps.KeyStatus is null or "available" ? null : "locked";
    }

    /// <summary>The app's folder while its dataset is mounted, or null (pool missing, locked, dataset gone).</summary>
    public string? RootOf(string pool, string appId) => RootOf(pools.Current, options, pool, appId);

    /// <summary>The app's folder in <paramref name="current"/>, or null while its dataset is not mounted.</summary>
    public static string? RootOf(PoolList current, AppStorageOptions options, string pool, string appId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.UseZfs)
        {
            var folder = FolderOf(options, pool, appId);
            return Directory.Exists(folder) ? folder : null;
        }
        var dataset = current.Datasets.FirstOrDefault(candidate => candidate.Name == ZfsPlans.AppDataset(pool, appId));
        return dataset is { Mounted: true, MountPoint: { } mountPoint } ? mountPoint : null;
    }

    /// <summary>Creates the app's dataset unless it exists (an uninstall that kept the data leaves it), and its folders.</summary>
    public async Task<string> EnsureAsync(string pool, string appId, CancellationToken cancellationToken)
    {
        RequirePool(pool);
        string root;
        if (options.UseZfs)
        {
            if (!pools.Current.Datasets.Any(candidate => candidate.Name == ZfsPlans.AppDataset(pool, appId)))
            {
                await RunAsync(ZfsPlans.CreateAppDataset(pool, appId), cancellationToken);
                await state.RefreshPoolsAsync(cancellationToken);
            }
            root = RootOf(pool, appId) ?? throw new PithyxException(
                ErrorCodes.AppPoolUnavailable, new Dictionary<string, object?> { ["pool"] = pool, ["reason"] = "locked" });
        }
        else
        {
            root = FolderOf(pool, appId);
        }
        foreach (var folder in new[] { VersionsFolder, VolumesFolder, RunFolder })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
        }
        return root;
    }

    /// <summary>
    /// Takes a held snapshot of the app's dataset before an update, so a failed update returns to exactly this state.
    /// Returns its name, or null in simulation.
    /// </summary>
    public async Task<string?> SnapshotAsync(string pool, string appId, CancellationToken cancellationToken)
    {
        if (!options.UseZfs)
        {
            return null;
        }
        var dataset = ZfsPlans.AppDataset(pool, appId);
        var name = SnapshotNames.For(SnapshotKinds.Update, time.GetUtcNow());
        await RunAsync(SnapshotPlans.Take(dataset, name), cancellationToken);
        await RunAsync(SnapshotPlans.Hold(dataset, name), cancellationToken);
        return name;
    }

    /// <summary>
    /// Returns the dataset to an update's snapshot: after a failed update it is the newest, so nothing else goes; a
    /// rollback days later (<paramref name="discardNewer"/>) also destroys the snapshots taken since.
    /// </summary>
    public async Task RollbackAsync(string pool, string appId, string snapshot, CancellationToken cancellationToken, bool discardNewer = false) =>
        await RunAsync(SnapshotPlans.Rollback(ZfsPlans.AppDataset(pool, appId), snapshot, discardNewer), cancellationToken);

    /// <summary>Releases the hold of an update's snapshot and destroys it.</summary>
    public async Task ForgetSnapshotAsync(string pool, string appId, string snapshot, CancellationToken cancellationToken)
    {
        var dataset = ZfsPlans.AppDataset(pool, appId);
        await RunAsync(SnapshotPlans.Release(dataset, snapshot), cancellationToken);
        await RunAsync(SnapshotPlans.Destroy(dataset, snapshot), cancellationToken);
    }

    /// <summary>Removes the app's data: its dataset with every snapshot, or its folder in simulation.</summary>
    public async Task DestroyAsync(string pool, string appId, CancellationToken cancellationToken)
    {
        if (!options.UseZfs)
        {
            var folder = FolderOf(pool, appId);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            return;
        }
        if (pools.Current.Datasets.Any(candidate => candidate.Name == ZfsPlans.AppDataset(pool, appId)))
        {
            await RunAsync(ZfsPlans.DestroyAppDataset(pool, appId), cancellationToken);
            await state.RefreshPoolsAsync(cancellationToken);
        }
    }

    private string FolderOf(string pool, string appId) => FolderOf(options, pool, appId);

    private static string FolderOf(AppStorageOptions options, string pool, string appId)
    {
        ZfsPlans.AppDataset(pool, appId);
        return Path.Combine(options.FolderRoot, pool, ".pithyx", "apps", appId);
    }

    private async Task RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(request, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.StorageCommandFailed, new Dictionary<string, object?> { ["command"] = request.FileName, ["exitCode"] = result.ExitCode });
        }
    }
}
