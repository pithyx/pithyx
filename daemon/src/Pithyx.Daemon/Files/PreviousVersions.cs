using System.Globalization;
using System.Security.Claims;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;

namespace Pithyx.Daemon.Files;

/// <param name="Snapshot">The snapshot the version is in; pass it to restore.</param>
/// <param name="CreatedAt">When the snapshot was taken (UTC).</param>
/// <param name="Kind">File or folder.</param>
/// <param name="Size">Bytes for files, 0 for folders.</param>
/// <param name="Modified">Last change of the content in that version, UTC.</param>
public sealed record FileVersion(string Snapshot, DateTimeOffset CreatedAt, FileKind Kind, long Size, DateTimeOffset Modified);

/// <summary>Response of <c>GET /api/files/{shareId}/versions</c>.</summary>
/// <param name="Path">The file or folder, relative to the share.</param>
/// <param name="Versions">Newest first; empty when the share is not on a ZFS dataset.</param>
public sealed record FileVersionList(string Path, IReadOnlyList<FileVersion> Versions);

/// <param name="Path">The file or folder, relative to the share; it may have been deleted since.</param>
/// <param name="Snapshot">The snapshot to restore from, as listed.</param>
/// <param name="Overwrite">Replace the current file (folders are merged) instead of restoring next to it.</param>
public sealed record FileVersionRestoreRequest(string Path, string Snapshot, bool Overwrite = false);

/// <summary>
/// Earlier versions of files and folders from the snapshots of the share's dataset, read through
/// <c>&lt;mountpoint&gt;/.zfs/snapshot/&lt;snapshot&gt;</c> with the file system (no command runs). Inside a snapshot, paths
/// resolve below the share's folder, so a symlink in a snapshot cannot reach another share.
/// </summary>
public sealed class PreviousVersions(ShareService shares, PoolInventory pools, SnapshotInventory snapshots, FileOperations operations)
{
    public const string SnapshotFolder = ".zfs/snapshot";

    public async Task<FileVersionList> ListAsync(ClaimsPrincipal user, long shareId, string? path, CancellationToken cancellationToken)
    {
        var share = await shares.ResolveAsync(user, shareId, write: false, cancellationToken);
        var normalized = NonRoot(path);
        if (Locate(pools.Current.Datasets, share.Path, normalized) is not { } location)
        {
            return new FileVersionList(normalized, []);
        }
        FsStat? current;
        using (var root = FileService.OpenRoot(share))
        {
            var (parent, name) = FsPath.Split(normalized);
            try
            {
                using var folder = LinuxFs.OpenBeneath(root, FsPath.ForOpen(parent), LinuxFs.Path | LinuxFs.Directory);
                current = FileService.TryStat(folder, name);
            }
            catch (FsError)
            {
                current = null;
            }
        }
        return new FileVersionList(normalized, List(location, snapshots.Of(location.Dataset), current));
    }

    /// <summary>Copies a version back as a job: next to the current one as "name (version date)", or over it.</summary>
    public async Task<Job> StartRestoreAsync(ClaimsPrincipal user, long shareId, FileVersionRestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var share = await shares.ResolveAsync(user, shareId, write: true, cancellationToken);
        var normalized = NonRoot(request.Path);
        var location = Locate(pools.Current.Datasets, share.Path, normalized);
        var snapshot = location is null ? null : snapshots.Find(location.Dataset, request.Snapshot);
        if (location is null || snapshot is null || !FsPath.IsName(snapshot.Name))
        {
            throw new PithyxException(ErrorCodes.FsVersionNotFound);
        }
        using (var versionRoot = OpenVersionRoot(location, snapshot.Name))
        {
            var (parent, name) = FsPath.Split(location.ItemPath);
            using var folder = versionRoot is null ? null : TryOpenFolder(versionRoot, parent);
            if (folder is null || FileService.TryStat(folder, name) is not { Kind: FsKind.File or FsKind.Folder })
            {
                throw new PithyxException(ErrorCodes.FsVersionNotFound);
            }
        }
        var (targetFolder, currentName) = FsPath.Split(normalized);
        var targetName = request.Overwrite ? currentName : VersionName(currentName, snapshot.CreatedAt);
        return await operations.StartCopyInAsync(
            user,
            "files.restore_version",
            request,
            () => OpenVersionRoot(location, snapshot.Name) ?? throw new PithyxException(ErrorCodes.FsVersionNotFound),
            location.ItemPath,
            share,
            targetFolder,
            targetName,
            request.Overwrite,
            cancellationToken);
    }

    /// <summary>
    /// Where an entry lies in the snapshots: the dataset whose mount point holds it (the deepest one), the folder
    /// inside each snapshot that paths may not leave (the share's folder, or the dataset root when the share begins
    /// above it) and the entry's path below that folder.
    /// </summary>
    internal sealed record Location(string Dataset, string MountPoint, string RootPath, string ItemPath);

    internal static Location? Locate(IEnumerable<Dataset> datasets, string sharePath, string itemPath)
    {
        var hostPath = FsPath.Join(sharePath.TrimEnd('/'), itemPath);
        var dataset = datasets
            .Where(candidate => candidate is { Type: "filesystem", Mounted: true, MountPoint: { } mount } && FsPath.IsSameOrInside(hostPath, mount.TrimEnd('/')))
            .MaxBy(candidate => candidate.MountPoint!.Length);
        if (dataset?.MountPoint is not { } mountPoint)
        {
            return null;
        }
        var mount = mountPoint.TrimEnd('/');
        var share = sharePath.TrimEnd('/');
        var root = FsPath.IsSameOrInside(share, mount) ? share : mount;
        return new Location(dataset.Name, mountPoint, Relative(mount, root), Relative(root, hostPath));
    }

    /// <summary>
    /// The versions of the entry, newest first. A file appears once per distinct content (size and modification time),
    /// under the newest snapshot that has it, and not at all while it equals <paramref name="current"/>; a folder
    /// appears for every snapshot that has it.
    /// </summary>
    internal static IReadOnlyList<FileVersion> List(Location location, IReadOnlyList<Snapshot> datasetSnapshots, FsStat? current)
    {
        var (parent, name) = FsPath.Split(location.ItemPath);
        var versions = new List<FileVersion>();
        var seen = new HashSet<(long, DateTimeOffset)>();
        if (current is { Kind: FsKind.File })
        {
            seen.Add((current.Size, current.Modified));
        }
        foreach (var snapshot in datasetSnapshots.OrderByDescending(snapshot => snapshot.CreateTxg))
        {
            if (!FsPath.IsName(snapshot.Name))
            {
                continue;
            }
            using var root = OpenVersionRoot(location, snapshot.Name);
            using var folder = root is null ? null : TryOpenFolder(root, parent);
            if (folder is null)
            {
                continue;
            }
            FsStat? stat;
            try
            {
                stat = FileService.TryStat(folder, name);
            }
            catch (FsError)
            {
                continue;
            }
            switch (stat?.Kind)
            {
                case FsKind.File when seen.Add((stat.Size, stat.Modified)):
                    versions.Add(new FileVersion(snapshot.Name, snapshot.CreatedAt, FileKind.File, stat.Size, stat.Modified));
                    break;
                case FsKind.Folder:
                    versions.Add(new FileVersion(snapshot.Name, snapshot.CreatedAt, FileKind.Folder, 0, stat.Modified));
                    break;
            }
        }
        return versions;
    }

    /// <summary>"Report.pdf" from a snapshot of 27 September 2026, 21:00 UTC becomes "Report (version 2026-09-27 21.00 UTC).pdf".</summary>
    internal static string VersionName(string name, DateTimeOffset createdAt)
    {
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} (version {createdAt.UtcDateTime:yyyy-MM-dd HH.mm} UTC){extension}");
        return FsPath.IsName(candidate) ? candidate : name;
    }

    /// <summary>
    /// The share's folder inside a snapshot, opened for reading. Opening it (rather than a path handle) makes ZFS mount
    /// the snapshot, which it does on first access. Null when the snapshot does not have the folder.
    /// </summary>
    private static SafeFileHandle? OpenVersionRoot(Location location, string snapshot)
    {
        try
        {
            using var mount = LinuxFs.OpenRoot(location.MountPoint);
            return LinuxFs.OpenBeneath(mount, $"{SnapshotFolder}/{snapshot}/{FsPath.ForOpen(location.RootPath)}", LinuxFs.ReadOnly | LinuxFs.Directory);
        }
        catch (FsError)
        {
            return null;
        }
    }

    private static SafeFileHandle? TryOpenFolder(SafeFileHandle root, string path)
    {
        try
        {
            return LinuxFs.OpenBeneath(root, FsPath.ForOpen(path), LinuxFs.ReadOnly | LinuxFs.Directory);
        }
        catch (FsError)
        {
            return null;
        }
    }

    // Both paths without a trailing slash; "" is the file system root.
    private static string Relative(string from, string to) => to == from ? "" : from.Length == 0 ? to.TrimStart('/') : to[(from.Length + 1)..];

    private static string NonRoot(string? path)
    {
        var normalized = FsPath.Normalize(path);
        return normalized.Length > 0 ? normalized : throw new PithyxException(ErrorCodes.FsOperationInvalid);
    }
}
