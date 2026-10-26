using System.Security.Claims;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Files;

/// <summary>
/// Files in shares. Every call resolves the share and the caller's access first, and every path is opened below the
/// share root by the kernel (<see cref="LinuxFs.OpenBeneath"/>), so no path, symlink or rename race leads outside it.
/// </summary>
public sealed class FileService(ShareService shares, FolderCache cache, TimeProvider time, FileOwners owners, Storage.StorageUsage? usage = null, Layouts.DesktopFileLinks? links = null)
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 1000;

    /// <summary>Size and date orders are rebuilt after this long; name order is checked against the folder's change time.</summary>
    public static readonly TimeSpan MetadataOrderLifetime = TimeSpan.FromSeconds(10);

    public static string TopicOf(long shareId, string folderId) => $"files/{shareId}/{folderId}";

    public async Task<FolderPage> ListAsync(
        ClaimsPrincipal user,
        long shareId,
        string? path,
        FileSort sort,
        SortOrder order,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken,
        string within = "")
    {
        var share = await shares.ResolveAsync(user, shareId, write: false, cancellationToken);
        var normalized = FsPath.Normalize(path);
        var inShare = FsPath.Join(within, normalized);
        var size = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        using var root = OpenRoot(share, within);
        using var folder = Open(root, normalized, LinuxFs.ReadOnly | LinuxFs.Directory);
        var folderStat = Stat(folder, "");
        var snapshot = Snapshot(share.Id, inShare, folder, folderStat, sort);

        var (after, afterValue) = cursor is null ? (null, 0L) : FolderCursor.Decode(cursor, sort, order);
        var entries = new List<FileEntry>(size);
        string? lastKey = null;
        long lastValue = 0;
        var more = false;
        foreach (var (name, key, value) in Ordered(snapshot, folder, sort, order, after, afterValue, size + 1))
        {
            if (entries.Count == size)
            {
                more = true;
                break;
            }
            // Entries removed since the snapshot are skipped; the next listing no longer has them.
            if (TryStat(folder, name) is { } stat)
            {
                entries.Add(ToEntry(name, stat));
                lastKey = key;
                lastValue = value;
            }
        }
        var next = more && lastKey is not null ? FolderCursor.Encode(sort, order, lastKey, lastValue) : null;
        cache.Register(share.Id, share.Path, snapshot.FolderId, inShare);
        return new FolderPage(normalized, snapshot.FolderId, snapshot.Count, entries, next, TopicOf(share.Id, snapshot.FolderId));
    }

    public async Task<FileEntry> StatAsync(ClaimsPrincipal user, long shareId, string? path, CancellationToken cancellationToken, string within = "")
    {
        var share = await shares.ResolveAsync(user, shareId, write: false, cancellationToken);
        var normalized = NonRoot(path);
        var (parent, name) = FsPath.Split(normalized);
        using var root = OpenRoot(share, within);
        using var folder = Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
        return ToEntry(name, Stat(folder, name));
    }

    /// <summary>Opens a file for reading; the caller owns the stream.</summary>
    public async Task<(FileStream Stream, FsStat Stat, string Name)> OpenReadAsync(ClaimsPrincipal user, long shareId, string? path, CancellationToken cancellationToken, string within = "")
    {
        var share = await shares.ResolveAsync(user, shareId, write: false, cancellationToken);
        var normalized = NonRoot(path);
        using var root = OpenRoot(share, within);
        var handle = Open(root, normalized, LinuxFs.ReadOnly);
        try
        {
            var stat = Stat(handle, "");
            if (stat.Kind == FsKind.Folder)
            {
                throw new PithyxException(ErrorCodes.FsEntryIsFolder);
            }
            if (stat.Kind != FsKind.File)
            {
                throw new PithyxException(ErrorCodes.FsEntryNotFound);
            }
            return (new FileStream(handle, FileAccess.Read, bufferSize: 0, isAsync: false), stat, FsPath.Split(normalized).Name);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Checks what <see cref="UploadAsync"/> checks before it reads the body: the right to write the share and the
    /// name, so a protocol can refuse an upload before it accepts any data.
    /// </summary>
    public async Task CheckUploadAsync(ClaimsPrincipal user, long shareId, string? path, CancellationToken cancellationToken)
    {
        _ = await shares.ResolveAsync(user, shareId, write: true, cancellationToken);
        FsPath.CheckName(FsPath.Split(NonRoot(path)).Name);
    }

    /// <summary>
    /// Streams a new file into a hidden temporary file next to its target and renames it into place when the body is
    /// complete, so nobody ever sees half a file. Without <paramref name="overwrite"/> an existing name fails.
    /// </summary>
    public async Task<FileEntry> UploadAsync(
        ClaimsPrincipal user,
        long shareId,
        string? path,
        Stream body,
        long? expectedLength,
        bool overwrite,
        CancellationToken cancellationToken,
        string within = "")
    {
        ArgumentNullException.ThrowIfNull(body);
        var share = await shares.ResolveAsync(user, shareId, write: true, cancellationToken);
        var owner = await owners.ForAsync(user, cancellationToken);
        var normalized = NonRoot(path);
        var (parent, name) = FsPath.Split(normalized);
        FsPath.CheckName(name);
        using var root = OpenRoot(share, within);
        using var folder = Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
        var temporary = $"{FsPath.UploadPrefix}{Guid.NewGuid():N}";
        var written = 0L;
        try
        {
            using (var handle = Open(root, FsPath.Join(parent, temporary), LinuxFs.WriteOnly | LinuxFs.Create | LinuxFs.Exclusive, LinuxFs.NewFileMode))
            await using (var file = new FileStream(handle, FileAccess.Write, bufferSize: 0, isAsync: false))
            {
                owner?.Apply(handle);
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                }
                if (expectedLength is { } length && length != written)
                {
                    throw new PithyxException(ErrorCodes.FsUploadIncomplete);
                }
                file.Flush(flushToDisk: true);
            }
            Rename(folder, temporary, folder, name, replace: overwrite);
            usage?.Written();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or BadHttpRequestException or PithyxException)
        {
            TryUnlink(folder, temporary);
            if (exception is IOException and not FsError)
            {
                // The client went away mid-body.
                throw new PithyxException(ErrorCodes.FsUploadIncomplete, null, exception);
            }
            throw;
        }
        return ToEntry(name, Stat(folder, name));
    }

    public async Task<FileEntry> CreateFolderAsync(ClaimsPrincipal user, long shareId, string? path, CancellationToken cancellationToken, string within = "")
    {
        var share = await shares.ResolveAsync(user, shareId, write: true, cancellationToken);
        var owner = await owners.ForAsync(user, cancellationToken);
        var normalized = NonRoot(path);
        var (parent, name) = FsPath.Split(normalized);
        FsPath.CheckName(name);
        using var root = OpenRoot(share, within);
        using var folder = Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
        try
        {
            LinuxFs.MakeDirectory(folder, name);
            owner?.ApplyAt(folder, name);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error, name);
        }
        return ToEntry(name, Stat(folder, name));
    }

    /// <summary>Renames within the same folder; an existing name is never replaced.</summary>
    public async Task<FileEntry> RenameAsync(ClaimsPrincipal user, long shareId, string? path, string? newName, CancellationToken cancellationToken, string within = "")
    {
        var share = await shares.ResolveAsync(user, shareId, write: true, cancellationToken);
        var normalized = NonRoot(path);
        var (parent, name) = FsPath.Split(normalized);
        var target = FsPath.CheckName(newName);
        using var root = OpenRoot(share, within);
        using var folder = Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
        if (target != name)
        {
            Rename(folder, name, folder, target, replace: false);
            if (links is not null)
            {
                var prefix = FsPath.Normalize(within);
                await links.MovedAsync(shareId, FsPath.Join(prefix, normalized), shareId, FsPath.Join(prefix, FsPath.Join(parent, target)), CancellationToken.None);
            }
        }
        return ToEntry(target, Stat(folder, target));
    }

    internal static SafeFileHandle OpenRoot(ResolvedShare share)
    {
        try
        {
            return LinuxFs.OpenRoot(share.Path);
        }
        catch (FsError error)
        {
            // The share's folder is gone (for example a pool that is not imported).
            throw new PithyxException(ErrorCodes.FsShareNotFound, null, error);
        }
    }

    /// <summary>
    /// The share's root, or the folder <paramref name="within"/> it (normalized, "" for the root) as the root of every
    /// path that follows: nothing below, no symlink either, leads out of it.
    /// </summary>
    internal static SafeFileHandle OpenRoot(ResolvedShare share, string within)
    {
        var root = OpenRoot(share);
        if (within.Length == 0)
        {
            return root;
        }
        using (root)
        {
            return Open(root, within, LinuxFs.Path | LinuxFs.Directory);
        }
    }

    internal static SafeFileHandle Open(SafeFileHandle root, string normalized, int flags, uint mode = 0)
    {
        try
        {
            return LinuxFs.OpenBeneath(root, FsPath.ForOpen(normalized), flags, mode);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error, FsPath.Split(normalized).Name);
        }
    }

    internal static FsStat Stat(SafeFileHandle folder, string name)
    {
        try
        {
            return LinuxFs.Stat(folder, name);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error, name);
        }
    }

    internal static FsStat? TryStat(SafeFileHandle folder, string name)
    {
        try
        {
            return LinuxFs.Stat(folder, name);
        }
        catch (FsError error) when (error.Errno == LinuxFs.ENOENT)
        {
            return null;
        }
    }

    internal static void Rename(SafeFileHandle fromFolder, string fromName, SafeFileHandle toFolder, string toName, bool replace)
    {
        try
        {
            LinuxFs.Rename(fromFolder, fromName, toFolder, toName, replace);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error, toName);
        }
    }

    internal static void TryUnlink(SafeFileHandle folder, string name)
    {
        try
        {
            LinuxFs.Unlink(folder, name, folder: false);
        }
        catch (FsError)
        {
            // Already gone.
        }
    }

    internal static FileEntry ToEntry(string name, FsStat stat) => new(
        name,
        stat.Kind switch
        {
            FsKind.File => FileKind.File,
            FsKind.Folder => FileKind.Folder,
            FsKind.Symlink => FileKind.Symlink,
            _ => FileKind.Other,
        },
        stat.Kind == FsKind.Folder ? 0 : stat.Size,
        stat.Modified);

    private static string NonRoot(string? path)
    {
        var normalized = FsPath.Normalize(path);
        return normalized.Length > 0 ? normalized : throw new PithyxException(ErrorCodes.FsOperationInvalid);
    }

    private FolderSnapshot Snapshot(long shareId, string normalized, SafeFileHandle folder, FsStat folderStat, FileSort sort)
    {
        var now = time.GetUtcNow();
        var cached = cache.Get(shareId, normalized);
        var fresh = cached is not null
            && cached.FolderId == folderStat.Id
            && cached.FolderChanged == Max(folderStat.Modified, folderStat.Changed)
            && (sort == FileSort.Name || now - cached.CreatedAt < MetadataOrderLifetime);
        if (fresh)
        {
            return cached!;
        }
        List<(string Name, FsKind Kind)> names;
        try
        {
            names = LinuxFs.ReadNames(folder);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error);
        }
        names.RemoveAll(entry => entry.Name.StartsWith(FsPath.UploadPrefix, StringComparison.Ordinal)
            || (normalized.Length == 0 && entry.Name == FsPath.TrashFolder));
        var snapshot = new FolderSnapshot(folderStat.Id, Max(folderStat.Modified, folderStat.Changed), [.. names], now);
        cache.Put(shareId, normalized, snapshot);
        return snapshot;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>At most <paramref name="count"/> of the snapshot's entries in order, starting after the cursor position.</summary>
    private static IEnumerable<(string Name, string Key, long Value)> Ordered(
        FolderSnapshot snapshot,
        SafeFileHandle folder,
        FileSort sort,
        SortOrder order,
        string? afterKey,
        long afterValue,
        int count)
    {
        if (sort == FileSort.Name)
        {
            foreach (var (name, key) in snapshot.ByName(afterKey, order, count))
            {
                yield return (name, key, 0);
            }
            yield break;
        }

        var sorted = snapshot.ByMetadata(sort, name => TryStat(folder, name));
        var first = order == SortOrder.Asc ? 0 : sorted.Length - 1;
        if (afterKey is not null)
        {
            // After an exact match, or at the insertion point when that entry is gone.
            var probe = Array.BinarySearch(sorted, (afterValue, afterKey, ""), MetadataOrder.Instance);
            first = probe >= 0 ? (order == SortOrder.Asc ? probe + 1 : probe - 1) : (order == SortOrder.Asc ? ~probe : ~probe - 1);
        }
        for (var i = first; i >= 0 && i < sorted.Length && count-- > 0; i += order == SortOrder.Asc ? 1 : -1)
        {
            yield return (sorted[i].Name, sorted[i].Key, sorted[i].Value);
        }
    }
}
