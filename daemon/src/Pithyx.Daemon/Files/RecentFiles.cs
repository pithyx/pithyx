using System.Security.Claims;
using Dapper;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Files;

/// <summary>A file the user opened recently.</summary>
/// <param name="Share">The share the file is in.</param>
/// <param name="Path">The file, relative to the share.</param>
/// <param name="Size">Bytes, as the file is now.</param>
/// <param name="Modified">Last change of the content as the file is now, UTC.</param>
/// <param name="OpenedAt">When the user last opened it, UTC.</param>
public sealed record RecentFile(long Share, string Path, long Size, DateTimeOffset Modified, DateTimeOffset OpenedAt);

/// <param name="Share">The share the file is in.</param>
/// <param name="Path">The file, relative to the share.</param>
public sealed record RecentFileRequest(long Share, string Path);

/// <summary>Snapshot and event of <c>files/recent</c>.</summary>
/// <param name="Revision">Changes whenever the user's list changed; compare it, do not count on it.</param>
public sealed record RecentFilesRevision(long Revision);

/// <summary>
/// The files each user opened recently (#333), newest first. Only files the user can read are recorded and listed, so
/// a revoked right, a deleted or moved file or a locked pool hides an entry without anybody cleaning up. The topic
/// <c>files/recent</c> tells each user, and only them, that their list changed.
/// </summary>
public sealed class RecentFiles(SqliteConnectionFactory connections, ShareService shares, TimeProvider time, TopicHub hub)
{
    public const int Limit = 50;
    public const string Topic = "files/recent";

    // A random start, so a restarted daemon does not repeat a revision a client saw before.
    private long _revision = Random.Shared.Next(1, int.MaxValue);

    public static TopicDefinition Definition(Func<RecentFiles> service) => new(
        Topic,
        typeof(RecentFilesRevision),
        typeof(RecentFilesRevision),
        (_, _) => ValueTask.FromResult<object>(new RecentFilesRevision(Interlocked.Read(ref service()._revision))),
        TopicDefinition.AnyUser,
        ForUser: (user, data) => data switch
        {
            RecentFilesRevision snapshot => snapshot,
            UserChange change when change.UserId == CurrentUser.Id(user) => new RecentFilesRevision(change.Revision),
            _ => null,
        });

    /// <summary>Records that the user opened the file; opening it again moves it to the top.</summary>
    public async Task RecordAsync(ClaimsPrincipal user, RecentFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = FsPath.Normalize(request.Path);
        if (path.Length == 0)
        {
            throw new PithyxException(ErrorCodes.FsOperationInvalid);
        }
        var share = await shares.ResolveAsync(user, request.Share, write: false, cancellationToken);
        var (parent, name) = FsPath.Split(path);
        using (var root = FileService.OpenRoot(share))
        using (var folder = FileService.Open(root, parent, LinuxFs.Path | LinuxFs.Directory))
        {
            var stat = FileService.Stat(folder, name);
            if (stat.Kind != FsKind.File)
            {
                throw new PithyxException(stat.Kind == FsKind.Folder ? ErrorCodes.FsEntryIsFolder : ErrorCodes.FsEntryNotFound);
            }
        }
        var userId = CurrentUser.Id(user);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO recent_files (user_id, share_id, path, opened_at) VALUES (@userId, @shareId, @path, @now)
                ON CONFLICT (user_id, share_id, path) DO UPDATE SET opened_at = excluded.opened_at;
            DELETE FROM recent_files WHERE user_id = @userId AND rowid NOT IN
                (SELECT rowid FROM recent_files WHERE user_id = @userId ORDER BY opened_at DESC LIMIT @Limit);
            """,
            new { userId, shareId = share.Id, path, now = SessionService.Format(time.GetUtcNow()), Limit },
            cancellationToken: cancellationToken));
        Announce(userId);
    }

    /// <summary>The user's recent files that are still files the user can read, newest first.</summary>
    public async Task<IReadOnlyList<RecentFile>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        IReadOnlyList<Row> rows;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            rows = [.. await connection.QueryAsync<Row>(new CommandDefinition(
                "SELECT share_id AS ShareId, path AS Path, opened_at AS OpenedAt FROM recent_files WHERE user_id = @userId ORDER BY opened_at DESC",
                new { userId = CurrentUser.Id(user) },
                cancellationToken: cancellationToken))];
        }
        var roots = new Dictionary<long, SafeFileHandle?>();
        try
        {
            var files = new List<RecentFile>(rows.Count);
            foreach (var row in rows)
            {
                if (!roots.TryGetValue(row.ShareId, out var root))
                {
                    roots[row.ShareId] = root = await TryOpenRootAsync(user, row.ShareId, cancellationToken);
                }
                if (root is not null && TryStat(root, row.Path) is { Kind: FsKind.File } stat)
                {
                    files.Add(new RecentFile(row.ShareId, row.Path, stat.Size, stat.Modified, SessionService.Parse(row.OpenedAt)));
                }
            }
            return files;
        }
        finally
        {
            foreach (var root in roots.Values)
            {
                root?.Dispose();
            }
        }
    }

    /// <summary>Forgets every recent file of the user.</summary>
    public async Task ClearAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userId = CurrentUser.Id(user);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM recent_files WHERE user_id = @userId", new { userId }, cancellationToken: cancellationToken));
        Announce(userId);
    }

    private void Announce(long userId) => hub.Publish(Topic, new UserChange(userId, Interlocked.Increment(ref _revision)));

    private async Task<SafeFileHandle?> TryOpenRootAsync(ClaimsPrincipal user, long shareId, CancellationToken cancellationToken)
    {
        try
        {
            return FileService.OpenRoot(await shares.ResolveAsync(user, shareId, write: false, cancellationToken));
        }
        catch (PithyxException)
        {
            return null;
        }
    }

    private static FsStat? TryStat(SafeFileHandle root, string path)
    {
        var (parent, name) = FsPath.Split(path);
        try
        {
            using var folder = FileService.Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
            return FileService.TryStat(folder, name);
        }
        catch (Exception error) when (error is PithyxException or FsError)
        {
            return null;
        }
    }

    private sealed record Row(long ShareId, string Path, string OpenedAt);

    private sealed record UserChange(long UserId, long Revision);
}
