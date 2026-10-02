using System.Globalization;
using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.Users;

/// <summary>
/// Gives every shared folder on a dataset the POSIX ACL of its rights (D264, D281): accounts have the primary group
/// <c>users</c> and a umask of 077, so a share is reached only through these entries. Active administrators get read
/// and write, active users with <c>read</c> read, with <c>readWrite</c> read and write; the owning user keeps its own
/// files, and the group and others get nothing. Folders also get the same default ACL, so what SMB, SFTP or the daemon
/// creates later inherits it. <c>setfacl</c> walks the tree without following symlinks. The ACL last set is kept in
/// <c>shares.acl</c>, so a share whose rights did not change is not walked again, not even after a restart. Real mode
/// only.
/// </summary>
public sealed partial class ShareAcls(SqliteConnectionFactory connections, ICommandRunner commands, AccountOptions options, ILogger<ShareAcls> logger) : IDisposable
{
    /// <summary>A large share takes a while; the walk is never cut short by the default command timeout.</summary>
    public static readonly TimeSpan WalkTimeout = TimeSpan.FromHours(2);

    private readonly SemaphoreSlim _running = new(1, 1);

    public void Dispose() => _running.Dispose();

    /// <summary>
    /// The <c>setfacl</c> entries for a share: the owner full, no group or other access, and one entry per uid with
    /// access, read and write as <c>rwX</c>, read as <c>r-X</c> (<c>X</c>: execute only for folders). The mask allows
    /// what the named users have.
    /// </summary>
    public static string Entries(IEnumerable<(long Uid, bool Write)> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        var named = users
            .GroupBy(user => user.Uid)
            .OrderBy(group => group.Key)
            .Select(group => $"u:{group.Key.ToString(CultureInfo.InvariantCulture)}:{(group.Any(user => user.Write) ? "rwX" : "r-X")}");
        return string.Join(',', ["u::rwX", "g::---", "o::---", "m::rwX", .. named]);
    }

    /// <summary>The access ACL on everything below the share, then the default ACL on its folders.</summary>
    public static IReadOnlyList<CommandRequest> Commands(string path, string entries) =>
    [
        new CommandRequest("setfacl", ["-R", "-P", "--set", entries, "--", path], WalkTimeout),
        new CommandRequest("setfacl", ["-R", "-P", "-d", "--set", entries, "--", path], WalkTimeout),
    ];

    /// <summary>Every dataset share with its folder, the entries it should have and the ones last set.</summary>
    public async Task<IReadOnlyList<(long ShareId, string Path, string Entries, string? Applied)>> WantedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var shares = await connection.QueryAsync<(long Id, string Path, string? Acl)>("SELECT id, path, acl FROM shares WHERE dataset IS NOT NULL ORDER BY id");
        var grants = (await connection.QueryAsync<(long ShareId, long Uid, string Level)>(
            """
            SELECT share_access.share_id, users.uid, share_access.level FROM share_access
            JOIN users ON users.id = share_access.user_id
            WHERE users.uid IS NOT NULL AND users.is_admin = 0 AND users.disabled_at IS NULL AND share_access.level <> 'none'
            """)).ToList();
        var admins = (await connection.QueryAsync<long>("SELECT uid FROM users WHERE is_admin = 1 AND uid IS NOT NULL AND disabled_at IS NULL")).ToList();
        return [.. shares.Select(share => (share.Id, share.Path, Entries(
            admins.Select(uid => (uid, true)).Concat(grants.Where(grant => grant.ShareId == share.Id).Select(grant => (grant.Uid, grant.Level == "readWrite")))), share.Acl))];
    }

    /// <summary>Sets the ACL of every share whose rights changed since it was last set. Never throws but for cancellation.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!options.Apply)
        {
            return;
        }
        await _running.WaitAsync(cancellationToken);
        try
        {
            foreach (var (shareId, path, entries, applied) in await WantedAsync(cancellationToken))
            {
                if (entries == applied || !Directory.Exists(path))
                {
                    continue;
                }
                if (await ApplyAsync(path, entries, cancellationToken))
                {
                    await using var connection = await connections.OpenAsync(cancellationToken);
                    await connection.ExecuteAsync("UPDATE shares SET acl = @entries WHERE id = @shareId", new { entries, shareId });
                    LogApplied(shareId, path);
                }
                else
                {
                    LogFailed(shareId, path);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The next change tries again; files keep the ACL they had.
            LogError(exception);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<bool> ApplyAsync(string path, string entries, CancellationToken cancellationToken)
    {
        foreach (var command in Commands(path, entries))
        {
            if ((await commands.RunAsync(command, cancellationToken)).ExitCode != 0)
            {
                return false;
            }
        }
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Share {ShareId} at {Path}: ACL set")]
    private partial void LogApplied(long shareId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Share {ShareId} at {Path}: setting the ACL failed")]
    private partial void LogFailed(long shareId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setting the ACLs of the shared folders failed")]
    private partial void LogError(Exception exception);
}
