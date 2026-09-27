namespace Pithyx.Daemon.Files;

public enum FileKind
{
    File,
    Folder,
    Symlink,
    Other,
}

/// <summary>A file or folder in a share.</summary>
/// <param name="Name">Name within its folder.</param>
/// <param name="Kind">File, folder, symlink (not followed in listings) or another kind such as a device node.</param>
/// <param name="Size">Bytes for files, 0 for folders.</param>
/// <param name="Modified">Last change of the content, UTC.</param>
public sealed record FileEntry(string Name, FileKind Kind, long Size, DateTimeOffset Modified);

/// <summary>One page of a folder listing.</summary>
/// <param name="Path">The folder, relative to the share ("" for its root).</param>
/// <param name="FolderId">Stable id of the folder, also after a rename.</param>
/// <param name="Total">Entries in the folder when the listing was made.</param>
/// <param name="Entries">This page, in the requested order.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; null on the last page.</param>
/// <param name="Topic">WebSocket topic with live changes of this folder while it is subscribed.</param>
public sealed record FolderPage(string Path, string FolderId, int Total, IReadOnlyList<FileEntry> Entries, string? NextCursor, string Topic);

/// <param name="Path">The new folder, relative to the share. Its parent must exist.</param>
public sealed record NewFolderRequest(string Path);

/// <param name="Path">The file or folder to rename.</param>
/// <param name="Name">The new name in the same folder.</param>
public sealed record RenameRequest(string Path, string Name);

/// <summary>What a folder topic sends: the names that changed since the last event, or a request to list again.</summary>
/// <param name="Changed">Entries that appeared or changed, with their current metadata.</param>
/// <param name="Removed">Names that are gone.</param>
/// <param name="Resync">True when changes were lost (the kernel queue overflowed); list the folder again.</param>
/// <param name="Gone">True when the folder itself was deleted or moved away.</param>
public sealed record FolderChanges(IReadOnlyList<FileEntry> Changed, IReadOnlyList<string> Removed, bool Resync, bool Gone);

/// <summary>The snapshot of a folder topic: the folder as the watch sees it.</summary>
/// <param name="Watching">Whether changes are being delivered.</param>
public sealed record FolderWatchState(bool Watching);
