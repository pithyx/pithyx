using System.Data.Common;
using Dapper;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Users;

/// <summary>Gives out uids and gids from the Pithyx ranges (D103), each exactly once: an allocation is never deleted.</summary>
public static class UidAllocator
{
    /// <summary>The next uid after every one ever given out, recorded in the caller's transaction.</summary>
    public static Task<long> AllocateUserAsync(DbConnection connection, DbTransaction transaction, string username, string now) =>
        AllocateAsync(connection, transaction, "user", username, now, AccountReconciler.MinUid, AccountReconciler.MaxUid, ErrorCodes.UsersUidExhausted);

    /// <summary>The next gid after every one ever given out, recorded in the caller's transaction.</summary>
    public static Task<long> AllocateGroupAsync(DbConnection connection, DbTransaction transaction, string name, string now) =>
        AllocateAsync(connection, transaction, "group", name, now, GroupReconciler.MinGid, GroupReconciler.MaxGid, ErrorCodes.UsersGidExhausted);

    private static async Task<long> AllocateAsync(DbConnection connection, DbTransaction transaction, string kind, string name, string now, long min, long max, string exhausted)
    {
        var last = await connection.ExecuteScalarAsync<long?>("SELECT max(id) FROM id_allocations WHERE kind = @kind", new { kind }, transaction);
        var id = Math.Max(last ?? 0, min - 1) + 1;
        if (id > max)
        {
            throw new PithyxException(exhausted);
        }
        await connection.ExecuteAsync(
            "INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (@id, @kind, @name, @now)",
            new { id, kind, name, now },
            transaction);
        return id;
    }
}
