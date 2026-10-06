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

    /// <summary>
    /// The uid of an app (40000 to 49999): the one it had before when the same id was installed already, so data kept
    /// on an uninstall still belongs to it; otherwise the next one after every app uid given out.
    /// </summary>
    public static async Task<long> AllocateAppAsync(DbConnection connection, DbTransaction transaction, string appId, string now)
    {
        var existing = await connection.ExecuteScalarAsync<long?>("SELECT id FROM id_allocations WHERE kind = 'app' AND name = @appId", new { appId }, transaction);
        return existing ?? await AllocateAsync(connection, transaction, "app", appId, now, MinAppUid, MaxAppUid, ErrorCodes.UsersUidExhausted);
    }

    public const long MinAppUid = 40000;
    public const long MaxAppUid = 49999;

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
