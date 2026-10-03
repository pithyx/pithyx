using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.FileServices;

/// <summary>A place in the tree WebDAV and FTP show: the root (no share), a shared folder's root (empty path) or below it.</summary>
/// <param name="ShareId">The shared folder, or null for the root that lists them.</param>
/// <param name="ShareName">Its name as the tree shows it.</param>
/// <param name="Path">The path below the shared folder, "" for its root.</param>
public sealed record ProtocolPath(long? ShareId, string ShareName, string Path)
{
    public static readonly ProtocolPath Root = new(null, "", "");

    public bool IsRoot => ShareId is null;
}

/// <summary>
/// The file tree of WebDAV and FTP (D291): the root lists the shared folders the user may use and that offer the
/// protocol, and below them everything goes through the file service and the file operations, with their rights,
/// owners (<c>fchownat</c>) and recycle bin. Nothing here touches a file itself.
/// </summary>
public sealed class ProtocolFiles(SqliteConnectionFactory connections, PermissionService permissions, FileService files, FileOperations operations)
{
    /// <summary>The column of <c>shares</c> that says whether a shared folder offers a protocol.</summary>
    private static string Column(string protocol) => protocol switch
    {
        Resources.WebDav => "webdav",
        Resources.Ftp => "ftp",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    /// <summary>The shared folders on datasets that offer the protocol and that the user may at least read, by name.</summary>
    public async Task<IReadOnlyList<(long Id, string Name)>> SharesAsync(ClaimsPrincipal user, string protocol, CancellationToken cancellationToken)
    {
        var rights = await permissions.ForUserAsync(Auth.CurrentUser.Id(user), cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var shares = await connection.QueryAsync<(long Id, string Name)>(
            $"SELECT id, name FROM shares WHERE {Column(protocol)} = 1 ORDER BY name COLLATE NOCASE");
        return [.. shares.Where(share => rights.Share(share.Id) != ShareAccess.None)];
    }

    /// <summary>Splits a path of the tree (<c>/Media/photos/a.jpg</c>) into its shared folder and the path below it.</summary>
    public async Task<ProtocolPath> ResolveAsync(ClaimsPrincipal user, string protocol, string path, CancellationToken cancellationToken)
    {
        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return ProtocolPath.Root;
        }
        var share = (await SharesAsync(user, protocol, cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.Name, segments[0], StringComparison.OrdinalIgnoreCase));
        if (share.Name is null)
        {
            throw new PithyxException(ErrorCodes.FsShareNotFound);
        }
        return new ProtocolPath(share.Id, share.Name, string.Join('/', segments[1..]));
    }

    /// <summary>What a path is: a folder for the root and the shared folders, otherwise the file service's entry.</summary>
    public async Task<FileEntry> StatAsync(ClaimsPrincipal user, string protocol, ProtocolPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            return new FileEntry("", FileKind.Folder, 0, DateTimeOffset.UnixEpoch);
        }
        if (path.Path.Length == 0)
        {
            // Listing the root of the share checks the access and that it is available.
            _ = await files.ListAsync(user, path.ShareId!.Value, "", FileSort.Name, SortOrder.Asc, 1, null, cancellationToken);
            return new FileEntry(path.ShareName, FileKind.Folder, 0, DateTimeOffset.UnixEpoch);
        }
        return await files.StatAsync(user, path.ShareId!.Value, path.Path, cancellationToken);
    }

    /// <summary>Every entry of a folder: the shared folders at the root, otherwise the folder's files and folders by name.</summary>
    public async Task<IReadOnlyList<FileEntry>> ListAsync(ClaimsPrincipal user, string protocol, ProtocolPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            return [.. (await SharesAsync(user, protocol, cancellationToken)).Select(share => new FileEntry(share.Name, FileKind.Folder, 0, DateTimeOffset.UnixEpoch))];
        }
        var entries = new List<FileEntry>();
        string? cursor = null;
        do
        {
            var page = await files.ListAsync(user, path.ShareId!.Value, path.Path, FileSort.Name, SortOrder.Asc, FileService.MaxPageSize, cursor, cancellationToken);
            entries.AddRange(page.Entries);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return entries;
    }

    public Task<(FileStream Stream, FsStat Stat, string Name)> OpenReadAsync(ClaimsPrincipal user, ProtocolPath path, CancellationToken cancellationToken) =>
        files.OpenReadAsync(user, InShare(path).ShareId!.Value, path.Path, cancellationToken);

    /// <summary>Writes a file; returns whether it existed before.</summary>
    public async Task<bool> WriteAsync(ClaimsPrincipal user, ProtocolPath path, Stream body, long? length, bool overwrite, CancellationToken cancellationToken)
    {
        var shareId = InShare(path).ShareId!.Value;
        var existed = await ExistsAsync(user, path, cancellationToken);
        await files.UploadAsync(user, shareId, path.Path, body, length, overwrite, cancellationToken);
        return existed;
    }

    public Task<FileEntry> MakeFolderAsync(ClaimsPrincipal user, ProtocolPath path, CancellationToken cancellationToken) =>
        files.CreateFolderAsync(user, InShare(path).ShareId!.Value, path.Path, cancellationToken);

    /// <summary>Moves a file or folder into the shared folder's recycle bin, like a delete in Files.</summary>
    public Task DeleteAsync(ClaimsPrincipal user, ProtocolPath path, CancellationToken cancellationToken) =>
        operations.RunNowAsync(
            user,
            new FileOperationRequest(FileOperationKind.Delete, [new FileLocation(InShare(path).ShareId!.Value, path.Path)]),
            cancellationToken);

    /// <summary>
    /// Moves or copies a file or folder to another path, also into another shared folder, and gives it the target's name.
    /// An existing target is replaced only with <paramref name="overwrite"/>, and then lands in the recycle bin first.
    /// Returns whether the target existed.
    /// </summary>
    public async Task<bool> TransferAsync(ClaimsPrincipal user, ProtocolPath source, ProtocolPath target, bool move, bool overwrite, CancellationToken cancellationToken)
    {
        var from = InShare(source);
        var to = InShare(target);
        var (sourceFolder, sourceName) = FsPath.Split(from.Path);
        var (targetFolder, targetName) = FsPath.Split(to.Path);
        var existed = await ExistsAsync(user, to, cancellationToken);
        if (existed)
        {
            if (!overwrite)
            {
                throw new PithyxException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = targetName });
            }
            await DeleteAsync(user, to, cancellationToken);
        }
        if (move && from.ShareId == to.ShareId && sourceFolder == targetFolder)
        {
            await files.RenameAsync(user, from.ShareId!.Value, from.Path, targetName, cancellationToken);
            return existed;
        }
        // Into the target folder under the source's name, then renamed. A name already taken there is a conflict.
        var landed = new ProtocolPath(to.ShareId, to.ShareName, FsPath.Join(targetFolder, sourceName));
        if (sourceName != targetName && await ExistsAsync(user, landed, cancellationToken))
        {
            throw new PithyxException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = sourceName });
        }
        await operations.RunNowAsync(
            user,
            new FileOperationRequest(
                move ? FileOperationKind.Move : FileOperationKind.Copy,
                [new FileLocation(from.ShareId!.Value, from.Path)],
                new FileLocation(to.ShareId!.Value, targetFolder)),
            cancellationToken);
        if (sourceName != targetName)
        {
            await files.RenameAsync(user, to.ShareId!.Value, landed.Path, targetName, cancellationToken);
        }
        return existed;
    }

    public async Task<bool> ExistsAsync(ClaimsPrincipal user, ProtocolPath path, CancellationToken cancellationToken)
    {
        try
        {
            await files.StatAsync(user, InShare(path).ShareId!.Value, path.Path, cancellationToken);
            return true;
        }
        catch (PithyxException exception) when (exception.Code == ErrorCodes.FsEntryNotFound)
        {
            return false;
        }
    }

    /// <summary>A path inside a shared folder; the root and a shared folder itself cannot be written, moved or deleted.</summary>
    private static ProtocolPath InShare(ProtocolPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.ShareId is null || path.Path.Length == 0 ? throw new PithyxException(ErrorCodes.FsPathInvalid) : path;
    }
}
