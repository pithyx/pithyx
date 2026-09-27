using System.Globalization;
using System.Security.Claims;
using Dapper;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Files;

/// <summary>One item in a share's recycle bin.</summary>
/// <param name="Id">Id for restoring or deleting it.</param>
/// <param name="Name">Its name when it was deleted.</param>
/// <param name="OriginalPath">Where it was, relative to the share; restoring puts it back there.</param>
/// <param name="Kind">File, folder, symlink or another kind.</param>
/// <param name="Size">Bytes, for a folder everything inside it.</param>
/// <param name="DeletedAt">When it was deleted, UTC.</param>
/// <param name="DeletedBy">User name of who deleted it, if that user still exists.</param>
/// <param name="ExpiresAt">When it is deleted for good, or null when the share keeps deleted items until emptied.</param>
public sealed record TrashItem(long Id, string Name, string OriginalPath, FileKind Kind, long Size, DateTimeOffset DeletedAt, string? DeletedBy, DateTimeOffset? ExpiresAt);

/// <param name="Items">Newest first.</param>
/// <param name="NextCursor">Pass as cursor for the next page; null on the last page.</param>
/// <param name="RetentionDays">Days the share keeps deleted items; 0 keeps them until the bin is emptied.</param>
public sealed record TrashPage(IReadOnlyList<TrashItem> Items, string? NextCursor, int RetentionDays);

/// <param name="Ids">Items of the share's recycle bin, at most 1000.</param>
/// <param name="OnConflict">For restoring: what happens when the original place is taken; rename when left out.</param>
public sealed record TrashItemsRequest(IReadOnlyList<long> Ids, ConflictPolicy? OnConflict = null);

/// <param name="RetentionDays">0 to 3650; 0 keeps deleted items until the bin is emptied.</param>
public sealed record TrashSettingsRequest(int RetentionDays);

/// <summary>
/// The recycle bin of each share: a hidden folder <c>.pithyx-trash</c> at the share root, one entry per deleted item
/// named after its row id, and a row with where it came from. Deleting moves an item there with a rename, so it is
/// instant on one file system; restoring renames it back. Items older than the share's retention are removed for good
/// by <see cref="TrashCleanupService"/>.
/// </summary>
public sealed class TrashService(SqliteConnectionFactory connections, ShareService shares, TimeProvider time)
{
    public const string Folder = FsPath.TrashFolder;
    public const int MaxRetentionDays = 3650;
    public const int DefaultPageSize = 100;

    public async Task<TrashPage> ListAsync(ClaimsPrincipal user, long shareId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var share = await shares.ResolveAsync(user, shareId, write: false, cancellationToken);
        var size = Math.Clamp(limit ?? DefaultPageSize, 1, FileService.MaxPageSize);
        long? before = null;
        if (cursor is not null)
        {
            before = long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : throw new PithyxException(ErrorCodes.FsCursorInvalid);
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        var retention = await connection.ExecuteScalarAsync<int>("SELECT trash_retention_days FROM shares WHERE id = @shareId", new { shareId = share.Id });
        var rows = (await connection.QueryAsync<Row>(
            $"""
            {SelectRows}
            WHERE trash_items.share_id = @shareId AND (@before IS NULL OR trash_items.id < @before)
            ORDER BY trash_items.id DESC
            LIMIT @take
            """,
            new { shareId = share.Id, before, take = size + 1 })).ToList();
        var more = rows.Count > size;
        var items = rows.Take(size).Select(row => ToItem(row, retention)).ToList();
        return new TrashPage(items, more ? items[^1].Id.ToString(CultureInfo.InvariantCulture) : null, retention);
    }

    public async Task SetRetentionAsync(ClaimsPrincipal user, long shareId, int retentionDays, CancellationToken cancellationToken)
    {
        if (!ShareService.IsAdmin(user))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
        if (retentionDays is < 0 or > MaxRetentionDays)
        {
            throw new PithyxException(ErrorCodes.FsTrashRetentionInvalid, new Dictionary<string, object?> { ["max"] = MaxRetentionDays });
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await connection.ExecuteAsync("UPDATE shares SET trash_retention_days = @retentionDays WHERE id = @shareId", new { shareId, retentionDays }) == 0)
        {
            throw new PithyxException(ErrorCodes.FsShareNotFound);
        }
    }

    /// <summary>The rows of the given ids that belong to the share; unknown ids fail with <c>fs.entry.not_found</c>.</summary>
    internal async Task<IReadOnlyList<TrashItem>> GetAsync(long shareId, IReadOnlyList<long>? ids, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Row>(
            ids is null ? $"{SelectRows} WHERE trash_items.share_id = @shareId" : $"{SelectRows} WHERE trash_items.share_id = @shareId AND trash_items.id IN @ids",
            new { shareId, ids })).ToList();
        if (ids is not null && rows.Count != ids.Distinct().Count())
        {
            throw new PithyxException(ErrorCodes.FsEntryNotFound);
        }
        return [.. rows.Select(row => ToItem(row, 0))];
    }

    /// <summary>Records an item that is about to move into the bin and returns the name it gets there.</summary>
    internal long Reserve(long shareId, string originalPath, FsKind kind, long size, long userId)
    {
        using var connection = connections.Open();
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO trash_items (share_id, original_path, kind, size, deleted_by, deleted_at)
            VALUES (@shareId, @originalPath, @kind, @size, @userId, @now) RETURNING id
            """,
            new { shareId, originalPath, kind = KindName(kind), size, userId, now = SessionService.Format(time.GetUtcNow()) });
    }

    internal void Forget(long id)
    {
        using var connection = connections.Open();
        connection.Execute("DELETE FROM trash_items WHERE id = @id", new { id });
    }

    /// <summary>The bin folder of a share, created on first use; only the daemon can enter it.</summary>
    internal static SafeFileHandle OpenFolder(SafeFileHandle root)
    {
        try
        {
            LinuxFs.MakeDirectory(root, Folder, 0x1c0);
        }
        catch (FsError error) when (error.Errno == LinuxFs.EEXIST)
        {
            // Already there.
        }
        catch (FsError error)
        {
            throw FsErrors.From(error);
        }
        return FileService.Open(root, Folder, LinuxFs.Path | LinuxFs.Directory);
    }

    /// <summary>Deletes items older than their share's retention for good. Returns how many.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var now = time.GetUtcNow();
        var expired = (await connection.QueryAsync<(long Id, long ShareId, string Path, string DeletedAt, long Retention)>(
            """
            SELECT trash_items.id, trash_items.share_id, shares.path, trash_items.deleted_at, shares.trash_retention_days
            FROM trash_items JOIN shares ON shares.id = trash_items.share_id
            WHERE shares.trash_retention_days > 0
            """)).Where(row => DateTimeOffset.Parse(row.DeletedAt, CultureInfo.InvariantCulture) + TimeSpan.FromDays(row.Retention) <= now).ToList();
        var deleted = 0;
        foreach (var group in expired.GroupBy(row => row.Path))
        {
            SafeFileHandle root;
            try
            {
                root = LinuxFs.OpenRoot(group.Key);
            }
            catch (FsError)
            {
                // The share's folder is not there (a pool that is not imported); try again later.
                continue;
            }
            using (root)
            using (var bin = OpenFolder(root))
            {
                foreach (var row in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RemoveTree(bin, row.Id.ToString(CultureInfo.InvariantCulture));
                    await connection.ExecuteAsync("DELETE FROM trash_items WHERE id = @id", new { id = row.Id });
                    deleted++;
                }
            }
        }
        return deleted;
    }

    /// <summary>Removes an entry and everything inside it without following symlinks; a missing entry is fine.</summary>
    internal static void RemoveTree(SafeFileHandle folder, string name)
    {
        FsStat? stat;
        try
        {
            stat = LinuxFs.Stat(folder, name);
        }
        catch (FsError error) when (error.Errno == LinuxFs.ENOENT)
        {
            return;
        }
        if (stat.Kind == FsKind.Folder)
        {
            using var child = FileService.Open(folder, name, LinuxFs.ReadOnly | LinuxFs.Directory);
            foreach (var (entry, _) in LinuxFs.ReadNames(child))
            {
                RemoveTree(child, entry);
            }
        }
        try
        {
            LinuxFs.Unlink(folder, name, stat.Kind == FsKind.Folder);
        }
        catch (FsError error) when (error.Errno == LinuxFs.ENOENT)
        {
            // Removed in the meantime.
        }
        catch (FsError error)
        {
            throw FsErrors.From(error, name);
        }
    }

    private const string SelectRows =
        """
        SELECT trash_items.id AS Id, trash_items.original_path AS OriginalPath, trash_items.kind AS Kind, trash_items.size AS Size,
            trash_items.deleted_at AS DeletedAt, users.username AS DeletedBy
        FROM trash_items LEFT JOIN users ON users.id = trash_items.deleted_by
        """;

    private static TrashItem ToItem(Row row, int retention)
    {
        var deletedAt = DateTimeOffset.Parse(row.DeletedAt, CultureInfo.InvariantCulture);
        return new TrashItem(
            row.Id,
            FsPath.Split(row.OriginalPath).Name,
            row.OriginalPath,
            row.Kind switch { "file" => FileKind.File, "folder" => FileKind.Folder, "symlink" => FileKind.Symlink, _ => FileKind.Other },
            row.Size,
            deletedAt,
            row.DeletedBy,
            retention > 0 ? deletedAt + TimeSpan.FromDays(retention) : null);
    }

    private static string KindName(FsKind kind) => kind switch
    {
        FsKind.File => "file",
        FsKind.Folder => "folder",
        FsKind.Symlink => "symlink",
        _ => "other",
    };

    private sealed class Row
    {
        public long Id { get; init; }
        public string OriginalPath { get; init; } = "";
        public string Kind { get; init; } = "";
        public long Size { get; init; }
        public string DeletedAt { get; init; } = "";
        public string? DeletedBy { get; init; }
    }
}

/// <summary>Deletes recycle bin items past their share's retention once an hour.</summary>
public sealed partial class TrashCleanupService(TrashService trash, TimeProvider time, ILogger<TrashCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var deleted = await trash.DeleteExpiredAsync(stoppingToken);
                    if (deleted > 0)
                    {
                        LogDeleted(logger, deleted);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A failing share must not stop the daemon; the next hour tries again.
                    LogFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired recycle bin items")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deleting expired recycle bin items failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
