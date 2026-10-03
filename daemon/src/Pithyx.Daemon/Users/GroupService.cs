using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Users;

/// <summary>
/// Group management for administrators (step 10, D289): create, rename, describe, set members, delete. Every group
/// gets a gid from the Pithyx range that is never given out again, and a Linux group with its members
/// (<see cref="LinuxAccounts"/>). The built-in group <c>users</c> holds every user and cannot be changed.
/// </summary>
public sealed class GroupService(SqliteConnectionFactory connections, LinuxAccounts accounts, Permissions.PermissionService permissions, TimeProvider time)
{
    public const int MaxDescriptionLength = 200;

    public async Task<IReadOnlyList<GroupSummary>> ListAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await ListAsync(connection, null);
    }

    public async Task<GroupSummary> CreateAsync(ClaimsPrincipal caller, NewGroup request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var name = CredentialRules.NormalizeGroupName(request.Name);
        var description = CheckDescription(request.Description);
        RefuseSystemName(name);
        var now = Now();
        long id;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await RefuseTakenAsync(connection, transaction, name, null);
            id = await UidAllocator.AllocateGroupAsync(connection, transaction, name, now);
            await connection.ExecuteAsync(
                "INSERT INTO groups (id, name, description, builtin, created_at, updated_at) VALUES (@id, @name, @description, 0, @now, @now)",
                new { id, name, description, now },
                transaction);
            await SetMembersAsync(connection, transaction, id, request.MemberIds ?? []);
            await transaction.CommitAsync(cancellationToken);
        }
        permissions.Invalidate();
        if ((await accounts.ReconcileAsync(cancellationToken)).TryGetValue(LinuxAccounts.GroupProblemKey(name), out var reason))
        {
            // The gid stays allocated: numbers are never reused.
            await using var connection = await connections.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("DELETE FROM groups WHERE id = @id", new { id });
            permissions.Invalidate();
            await accounts.ReconcileAsync(cancellationToken);
            throw new PithyxException(ErrorCodes.UsersGroupRefused, new Dictionary<string, object?> { ["name"] = name, ["reason"] = reason });
        }
        return await FindAsync(id, cancellationToken) ?? throw NotFound();
    }

    public async Task<GroupSummary> UpdateAsync(ClaimsPrincipal caller, long id, GroupUpdate request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var group = await FindAsync(id, cancellationToken) ?? throw NotFound();
        if (group.Builtin)
        {
            throw new PithyxException(ErrorCodes.UsersGroupBuiltin);
        }
        var name = request.Name is null ? group.Name : CredentialRules.NormalizeGroupName(request.Name);
        var description = request.Description is null ? group.Description : CheckDescription(request.Description);
        if (name != group.Name)
        {
            RefuseSystemName(name);
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await RefuseTakenAsync(connection, transaction, name, id);
            await connection.ExecuteAsync(
                "UPDATE groups SET name = @name, description = @description, updated_at = @now WHERE id = @id",
                new { id, name, description, now = Now() },
                transaction);
            if (request.MemberIds is { } members)
            {
                await SetMembersAsync(connection, transaction, id, members);
                // Members may get their administrator right from this group.
                await Permissions.PermissionService.EnsureAdministratorRemainsAsync(connection, transaction);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        permissions.Invalidate();
        if ((await accounts.ReconcileAsync(cancellationToken)).TryGetValue(LinuxAccounts.GroupProblemKey(name), out var reason))
        {
            throw new PithyxException(ErrorCodes.UsersGroupRefused, new Dictionary<string, object?> { ["name"] = name, ["reason"] = reason });
        }
        return await FindAsync(id, cancellationToken) ?? throw NotFound();
    }

    /// <summary>Deletes the group and its Linux group. Files keep its gid, which is never given out again.</summary>
    public async Task DeleteAsync(ClaimsPrincipal caller, long id, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var group = await FindAsync(id, cancellationToken) ?? throw NotFound();
        if (group.Builtin)
        {
            throw new PithyxException(ErrorCodes.UsersGroupBuiltin);
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            // Its grants and memberships go with it.
            await connection.ExecuteAsync("DELETE FROM groups WHERE id = @id", new { id }, transaction);
            await Permissions.PermissionService.EnsureAdministratorRemainsAsync(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        permissions.Invalidate();
        await accounts.ReconcileAsync(cancellationToken);
    }

    /// <summary>Replaces the memberships of a user in the groups that are not built in. Every id must name such a group.</summary>
    public static async Task SetUserGroupsAsync(DbConnection connection, DbTransaction transaction, long userId, IReadOnlyList<long> groupIds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(groupIds);
        var wanted = groupIds.Distinct().ToList();
        var known = (await connection.QueryAsync<long>("SELECT id FROM groups WHERE builtin = 0", transaction: transaction)).ToHashSet();
        if (wanted.Any(groupId => !known.Contains(groupId)))
        {
            throw wanted.Contains(GroupReconciler.UsersGid) ? new PithyxException(ErrorCodes.UsersGroupBuiltin) : NotFound();
        }
        await connection.ExecuteAsync("DELETE FROM group_members WHERE user_id = @userId", new { userId }, transaction);
        foreach (var groupId in wanted)
        {
            await connection.ExecuteAsync("INSERT INTO group_members (group_id, user_id) VALUES (@groupId, @userId)", new { groupId, userId }, transaction);
        }
    }

    /// <summary>The ids of the groups a user is an explicit member of, the built-in one left out.</summary>
    public static async Task<IReadOnlyList<long>> GroupIdsOfAsync(DbConnection connection, long userId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return [.. await connection.QueryAsync<long>("SELECT group_id FROM group_members WHERE user_id = @userId ORDER BY group_id", new { userId })];
    }

    private static async Task SetMembersAsync(DbConnection connection, DbTransaction transaction, long groupId, IReadOnlyList<long> memberIds)
    {
        var wanted = memberIds.Distinct().ToList();
        var known = (await connection.QueryAsync<long>("SELECT id FROM users", transaction: transaction)).ToHashSet();
        if (wanted.Any(userId => !known.Contains(userId)))
        {
            throw new PithyxException(ErrorCodes.UsersUserNotFound);
        }
        await connection.ExecuteAsync("DELETE FROM group_members WHERE group_id = @groupId", new { groupId }, transaction);
        foreach (var userId in wanted)
        {
            await connection.ExecuteAsync("INSERT INTO group_members (group_id, user_id) VALUES (@groupId, @userId)", new { groupId, userId }, transaction);
        }
    }

    private async Task<GroupSummary?> FindAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await ListAsync(connection, id) is [var group, ..] ? group : null;
    }

    private static async Task<IReadOnlyList<GroupSummary>> ListAsync(DbConnection connection, long? id)
    {
        var groups = await connection.QueryAsync<(long Id, string Name, string Description, long Builtin, string CreatedAt)>(
            "SELECT id, name, description, builtin, created_at FROM groups WHERE @id IS NULL OR id = @id ORDER BY builtin DESC, name",
            new { id });
        var members = (await connection.QueryAsync<(long GroupId, long UserId)>("SELECT group_id, user_id FROM group_members ORDER BY user_id"))
            .ToLookup(member => member.GroupId, member => member.UserId);
        var everybody = (await connection.QueryAsync<long>("SELECT id FROM users ORDER BY id")).ToList();
        return [.. groups.Select(group => new GroupSummary(
            group.Id,
            group.Name,
            group.Description,
            group.Builtin == 1,
            group.Builtin == 1 ? everybody : [.. members[group.Id]],
            DateTimeOffset.Parse(group.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)))];
    }

    /// <summary>A group outside the Pithyx range with this name belongs to the system or to someone else.</summary>
    private void RefuseSystemName(string name)
    {
        if (accounts.ActualGroups().FirstOrDefault(entry => entry.Name == name) is { } foreign && !GroupReconciler.IsManaged(foreign.Gid))
        {
            throw new PithyxException(ErrorCodes.UsersGroupRefused, new Dictionary<string, object?> { ["name"] = name, ["reason"] = "name_taken" });
        }
    }

    private static async Task RefuseTakenAsync(DbConnection connection, DbTransaction transaction, string name, long? exceptId)
    {
        if (await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM groups WHERE name = @name AND (@exceptId IS NULL OR id <> @exceptId)",
                new { name, exceptId },
                transaction) > 0)
        {
            throw new PithyxException(ErrorCodes.UsersGroupExists, new Dictionary<string, object?> { ["name"] = name });
        }
    }

    private static string CheckDescription(string? description)
    {
        var trimmed = description?.Trim() ?? "";
        return trimmed.Length <= MaxDescriptionLength && !trimmed.Any(char.IsControl)
            ? trimmed
            : throw new PithyxException(ErrorCodes.UsersGroupDescriptionInvalid, new Dictionary<string, object?> { ["max"] = MaxDescriptionLength });
    }

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static PithyxException NotFound() => new(ErrorCodes.UsersGroupNotFound);

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}
