using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.Backups;

/// <summary>What a restore does with a file that is already in place.</summary>
public enum BackupRestoreConflict
{
    /// <summary>The restored file goes next to it, named after the version's time, unless the two have the same size and time.</summary>
    KeepBoth,

    /// <summary>The restored file replaces it; a folder is never replaced, the two are kept instead.</summary>
    Overwrite,

    /// <summary>It stays, the restored file is dropped.</summary>
    Skip,
}

/// <summary>What a restore did, entry by entry.</summary>
public sealed class BackupRestoreTally
{
    /// <summary>Entries put where nothing was; a folder counts once with everything in it.</summary>
    public long Restored { get; set; }

    public long Replaced { get; set; }

    /// <summary>Entries kept next to one in place under a name with the version's time.</summary>
    public long Renamed { get; set; }

    public long Skipped { get; set; }
}

/// <summary>
/// Moves what restic restored into a private folder of the share to its place (#395, D419), by handles only: every
/// step opens one name below a folder it holds open, without following a symlink, so nothing a user puts into the share
/// meanwhile can send the daemon's writes elsewhere. Folders in place are merged; files follow the conflict choice.
/// Staging and place must be on one file system: moves are renames.
/// </summary>
internal static class BackupRestoreMerge
{
    /// <summary>Moves entry <paramref name="name"/> of <paramref name="from"/> to <paramref name="target"/> in <paramref name="to"/>.</summary>
    public static void Entry(
        SafeFileHandle from,
        string name,
        SafeFileHandle to,
        string target,
        BackupRestoreConflict conflict,
        DateTimeOffset versionTime,
        BackupRestoreTally tally,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existing = FileService.TryStat(to, target);
        if (existing is null)
        {
            try
            {
                LinuxFs.Rename(from, name, to, target);
                tally.Restored++;
                return;
            }
            catch (FsError error) when (error.Errno == LinuxFs.EEXIST)
            {
                // Something appeared there meanwhile: handled as a conflict.
                existing = LinuxFs.Stat(to, target);
            }
        }
        var incoming = LinuxFs.Stat(from, name);
        if (incoming.Kind == FsKind.Folder && existing.Kind == FsKind.Folder)
        {
            using var source = LinuxFs.OpenBeneath(from, name, LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
            using var place = LinuxFs.OpenBeneath(to, target, LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
            foreach (var (child, _) in LinuxFs.ReadNames(source))
            {
                Entry(source, child, place, child, conflict, versionTime, tally, cancellationToken);
            }
            return;
        }
        if (conflict == BackupRestoreConflict.KeepBoth && Same(incoming, existing))
        {
            // Unchanged since the version, compared as restic compares: a copy next to it would only clutter the folder.
            tally.Skipped++;
            return;
        }
        switch (conflict)
        {
            case BackupRestoreConflict.Skip:
                tally.Skipped++;
                return;
            case BackupRestoreConflict.Overwrite when incoming.Kind != FsKind.Folder && existing.Kind != FsKind.Folder:
                LinuxFs.Rename(from, name, to, target, replace: true);
                tally.Replaced++;
                return;
            default:
                var versioned = PreviousVersions.VersionName(target, versionTime);
                LinuxFs.Rename(from, name, to, FileService.TryStat(to, versioned) is null ? versioned : FileOperations.FreeName(to, versioned));
                tally.Renamed++;
                return;
        }
    }

    /// <summary>Whether two files have the same size and modification time.</summary>
    private static bool Same(FsStat incoming, FsStat existing) =>
        incoming.Kind == FsKind.File && existing.Kind == FsKind.File && incoming.Size == existing.Size && incoming.Modified == existing.Modified;

    /// <summary>
    /// Opens the folders of <paramref name="path"/> (an absolute path as restic stored it) one by one below
    /// <paramref name="root"/>, where restic restored it; null when restic did not restore it.
    /// </summary>
    public static SafeFileHandle? OpenStaged(SafeFileHandle root, string path)
    {
        var current = LinuxFs.OpenBeneath(root, ".", LinuxFs.ReadOnly | LinuxFs.Directory);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var next = LinuxFs.OpenBeneath(current, part, LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
                current.Dispose();
                current = next;
            }
            catch (FsError error) when (error.Errno is LinuxFs.ENOENT or LinuxFs.ENOTDIR or LinuxFs.ELOOP)
            {
                current.Dispose();
                return null;
            }
        }
        return current;
    }
}
