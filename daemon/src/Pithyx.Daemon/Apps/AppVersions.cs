using Pithyx.Daemon.Apps.Packages;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// Puts a checked package into an app's dataset as <c>versions/&lt;version&gt;</c> and points <c>current</c> at it, with
/// the registry change (<c>commit</c>) as the last step. An update first takes a held snapshot of the dataset; when any
/// step fails, the dataset returns to it (or, without a snapshot, the new files go and the old ones come back), the
/// registry stays as it was, and the snapshot is released and destroyed. After a successful update the snapshot and the
/// previous version's folder stay for a rollback (D329) until the next update; <c>commit</c> gets the snapshot's name to
/// record it. The frontend is always served from the version the registry names, so a half-done switch is never
/// visible; <c>published</c> runs after the commit and before older versions go, so the served registry never names a
/// deleted folder.
/// </summary>
public static class AppVersions
{
    public static async Task InstallAsync(
        AppStorage storage,
        string pool,
        string appId,
        UnpackedPackage package,
        string version,
        bool update,
        Func<string?, CancellationToken, Task> commit,
        CancellationToken cancellationToken,
        Func<Task>? published = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(commit);
        var root = await storage.EnsureAsync(pool, appId, cancellationToken);
        var snapshot = update ? await storage.SnapshotAsync(pool, appId, cancellationToken) : null;
        var versions = Path.Combine(root, AppStorage.VersionsFolder);
        var current = Path.Combine(root, AppStorage.CurrentLink);
        var previousLink = new FileInfo(current).LinkTarget;
        var target = Path.Combine(versions, version);
        // The version that ran before stays next to the new one for a rollback, unless it is the same version again.
        var kept = previousLink is null ? null : Path.GetFullPath(Path.Combine(root, previousLink));
        string? replaced = null;
        var succeeded = false;
        try
        {
            var incoming = Path.Combine(versions, ".incoming-" + Guid.NewGuid().ToString("N"));
            Copy(package, incoming, cancellationToken);
            if (Directory.Exists(target))
            {
                replaced = Path.Combine(versions, ".replaced-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, replaced);
            }
            Directory.Move(incoming, target);
            PointCurrent(current, Path.Combine(AppStorage.VersionsFolder, version));
            await commit(snapshot, cancellationToken);
            succeeded = true;
        }
        catch
        {
            if (snapshot is not null)
            {
                await storage.RollbackAsync(pool, appId, snapshot, CancellationToken.None);
            }
            else
            {
                Undo(versions, target, replaced, current, previousLink);
            }
            throw;
        }
        finally
        {
            if (snapshot is not null && !succeeded)
            {
                await storage.ForgetSnapshotAsync(pool, appId, snapshot, CancellationToken.None);
            }
        }
        if (published is not null)
        {
            await published();
        }
        // The installed version and, after an update, the one before it stay; the snapshot covers its data.
        foreach (var entry in Directory.EnumerateFileSystemEntries(versions).Where(entry => entry != target && !(update && Path.GetFullPath(entry) == kept)))
        {
            Delete(entry);
        }
    }

    /// <summary>
    /// Goes back to the version before the last update without a snapshot (simulation): <c>current</c> points at its
    /// kept folder again and the newer version's folder goes. With ZFS the rollback of the snapshot does this.
    /// </summary>
    public static void PointBack(string root, string previousVersion, string currentVersion)
    {
        PointCurrent(Path.Combine(root, AppStorage.CurrentLink), Path.Combine(AppStorage.VersionsFolder, previousVersion));
        if (previousVersion != currentVersion)
        {
            Delete(Path.Combine(root, AppStorage.VersionsFolder, currentVersion));
        }
    }

    /// <summary>Removes an app's frontends but keeps its volumes, for an uninstall that keeps the data.</summary>
    public static void RemoveFrontends(string root)
    {
        Delete(Path.Combine(root, AppStorage.CurrentLink));
        Delete(Path.Combine(root, AppStorage.VersionsFolder));
    }

    private static void Copy(UnpackedPackage package, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in package.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(package.Directory, file.Path), target);
        }
    }

    private static void PointCurrent(string current, string target)
    {
        // The frontend is served from the version the registry names, never through current, so replacing the link
        // needs no atomic rename.
        Delete(current);
        File.CreateSymbolicLink(current, target);
    }

    private static void Undo(string versions, string target, string? replaced, string current, string? previousLink)
    {
        Delete(target);
        if (replaced is not null && Directory.Exists(replaced))
        {
            Directory.Move(replaced, target);
        }
        foreach (var leftover in Directory.EnumerateDirectories(versions, ".incoming-*"))
        {
            Delete(leftover);
        }
        if (previousLink is null)
        {
            Delete(current);
        }
        else
        {
            PointCurrent(current, previousLink);
        }
    }

    private static void Delete(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
