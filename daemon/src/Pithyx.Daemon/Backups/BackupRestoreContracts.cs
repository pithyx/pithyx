namespace Pithyx.Daemon.Backups;

/// <summary>What an entry of a version is.</summary>
public enum BackupFileKind
{
    File,
    Folder,
    Symlink,
    Other,
}

/// <summary>An entry of a folder in a version.</summary>
/// <param name="Name">Its name; at the top of a version the folder the backup holds, as the shared folder's or home's name and the path below it.</param>
/// <param name="Kind">File, folder, symlink or other.</param>
/// <param name="Size">Bytes of a file; null for anything else.</param>
/// <param name="Modified">When it was last changed, UTC.</param>
/// <param name="Change">Against the version before: added or modified; null when unchanged, or for the oldest version.</param>
public sealed record BackupFileEntry(string Name, BackupFileKind Kind, long? Size, DateTimeOffset Modified, BackupChangeKind? Change);

/// <summary>A folder of a version.</summary>
/// <param name="Path">The folder, as the shared folder's or home's name and the path below it; empty at the top.</param>
/// <param name="Compared">Whether <see cref="BackupFileEntry.Change"/> compares with a version before.</param>
/// <param name="Entries">Its entries by name.</param>
public sealed record BackupFileList(string Path, bool Compared, IReadOnlyList<BackupFileEntry> Entries);

/// <summary>A file or folder in one version, or its absence there.</summary>
/// <param name="VersionId">The version.</param>
/// <param name="Time">When the version was made, UTC.</param>
/// <param name="Kind">Null when the version does not have it.</param>
/// <param name="Size">Bytes of a file.</param>
/// <param name="Modified">When it was last changed, UTC.</param>
/// <param name="Owner">Its owner's user name, or the uid without an account.</param>
/// <param name="Mode">Its permission bits, octal as <c>ls</c> counts them (for example <c>0640</c>).</param>
public sealed record BackupFileVersion(long VersionId, DateTimeOffset Time, BackupFileKind? Kind, long? Size, DateTimeOffset? Modified, string? Owner, string? Mode);

/// <summary>The file or folder on the box now.</summary>
/// <param name="Kind">File, folder, symlink or other.</param>
/// <param name="Size">Bytes of a file.</param>
/// <param name="Modified">When it was last changed, UTC.</param>
public sealed record BackupFileCurrent(BackupFileKind Kind, long? Size, DateTimeOffset Modified);

/// <summary>A file or folder in every version of a backup.</summary>
/// <param name="Path">As in <see cref="BackupFileList.Path"/>.</param>
/// <param name="Current">It on the box now; null when it is not there.</param>
/// <param name="Versions">Newest first, one per version.</param>
public sealed record BackupFileHistory(string Path, BackupFileCurrent? Current, IReadOnlyList<BackupFileVersion> Versions);

/// <summary>Where a restore puts the entries.</summary>
/// <param name="ShareId">The shared folder or home.</param>
/// <param name="Path">The folder in it, empty for its top.</param>
public sealed record BackupRestoreDestination(long ShareId, string Path);

/// <summary>Restores entries of a version.</summary>
/// <param name="VersionId">The version.</param>
/// <param name="Paths">1 to 1000 files or folders, as in <see cref="BackupFileList.Path"/>.</param>
/// <param name="Destination">Null puts each where it was, with the folders above it; otherwise each goes into this folder under its name.</param>
/// <param name="Conflict">What happens with a file that is already there (keep both by default); folders are merged.</param>
public sealed record BackupRestoreRequest(long VersionId, IReadOnlyList<string> Paths, BackupRestoreDestination? Destination = null, BackupRestoreConflict Conflict = BackupRestoreConflict.KeepBoth);

/// <summary>What a restore did, the result of its box job.</summary>
/// <param name="Restored">Entries put where nothing was; a folder counts once with everything in it.</param>
/// <param name="Replaced">Files that replaced one in place.</param>
/// <param name="Renamed">Entries kept next to one in place under a name with the version's time.</param>
/// <param name="Skipped">Files left out because one was in place.</param>
public sealed record BackupRestoreResult(long Restored, long Replaced, long Renamed, long Skipped);
