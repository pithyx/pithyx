using System.Globalization;
using Dapper;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Permissions;

/// <summary>
/// The two Linux groups of every shared folder on a dataset (D102, D290): <c>pithyx-s&lt;id&gt;-r</c> with the users
/// who may read and <c>pithyx-s&lt;id&gt;-rw</c> with those who may read and write, after their effective rights
/// (administrators read and write, deactivated users nothing). The share's ACL names only these two groups, so a
/// change of rights changes group members and never walks the files again. Their gids come from the group range and
/// are never reused.
/// </summary>
public static class ShareGroups
{
    public static string ReadName(long shareId) => $"pithyx-s{shareId.ToString(CultureInfo.InvariantCulture)}-r";

    public static string WriteName(long shareId) => $"pithyx-s{shareId.ToString(CultureInfo.InvariantCulture)}-rw";

    /// <summary>Gives every dataset share without them its two gids.</summary>
    public static async Task AllocateAsync(SqliteConnectionFactory connections, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(time);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var missing = (await connection.QueryAsync<long>("SELECT id FROM shares WHERE dataset IS NOT NULL AND (read_gid IS NULL OR write_gid IS NULL)")).ToList();
        foreach (var shareId in missing)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            var read = await UidAllocator.AllocateGroupAsync(connection, transaction, ReadName(shareId), now);
            var write = await UidAllocator.AllocateGroupAsync(connection, transaction, WriteName(shareId), now);
            await connection.ExecuteAsync("UPDATE shares SET read_gid = @read, write_gid = @write WHERE id = @shareId", new { read, write, shareId }, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>The wanted groups of every share with gids, members only from <paramref name="accounts"/>.</summary>
    public static IReadOnlyList<WantedGroup> Wanted(
        IReadOnlyList<(long Id, long ReadGid, long WriteGid)> shares,
        IReadOnlyList<UserPermissions> users,
        IReadOnlySet<string> accounts)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(accounts);
        var present = users.Where(user => !user.Disabled && accounts.Contains(user.Username)).ToList();
        return [.. shares.SelectMany(share => new[]
        {
            new WantedGroup(ReadName(share.Id), share.ReadGid, [.. present.Where(user => user.Permissions.Share(share.Id) == ShareAccess.Read).Select(user => user.Username).Order(StringComparer.Ordinal)]),
            new WantedGroup(WriteName(share.Id), share.WriteGid, [.. present.Where(user => user.Permissions.Share(share.Id) == ShareAccess.ReadWrite).Select(user => user.Username).Order(StringComparer.Ordinal)]),
        })];
    }
}
