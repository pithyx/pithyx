using System.Data.Common;
using Dapper;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Users;

/// <summary>Gives out uids from the user range (D103), each exactly once: an allocation is never deleted.</summary>
public static class UidAllocator
{
    /// <summary>The next uid after every one ever given out, recorded in the caller's transaction.</summary>
    public static async Task<long> AllocateUserAsync(DbConnection connection, DbTransaction transaction, string username, string now)
    {
        var last = await connection.ExecuteScalarAsync<long?>("SELECT max(id) FROM id_allocations WHERE kind = 'user'", transaction: transaction);
        var uid = Math.Max(last ?? 0, AccountReconciler.MinUid - 1) + 1;
        if (uid > AccountReconciler.MaxUid)
        {
            throw new PithyxException(ErrorCodes.UsersUidExhausted);
        }
        await connection.ExecuteAsync(
            "INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (@uid, 'user', @username, @now)",
            new { uid, username, now },
            transaction);
        return uid;
    }
}
