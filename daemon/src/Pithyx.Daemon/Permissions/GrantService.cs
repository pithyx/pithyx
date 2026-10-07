using System.Data.Common;
using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Permissions;

/// <summary>
/// The grant editors of Settings (D290): the grants of one user, of one group and on one shared folder, each read and
/// replaced as a whole. Administrators only. A change that would leave no active administrator is refused.
/// </summary>
public sealed class GrantService(SqliteConnectionFactory connections, PermissionService permissions)
{
    public static PermissionCatalog Catalog { get; } = new(Resources.Rights, Resources.Protocols, CoreApps.Granted);

    public async Task<UserGrants> UserGrantsAsync(ClaimsPrincipal caller, long userId, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE id = @userId", new { userId }) == 0)
        {
            throw new PithyxException(ErrorCodes.UsersUserNotFound);
        }
        var own = await SubjectGrantsAsync(connection, SubjectKind.User, userId);
        var inherited = await connection.QueryAsync<(string Resource, string Level, long GroupId, string GroupName)>(
            """
            SELECT grants.resource, grants.level, groups.id, groups.name FROM grants JOIN groups ON groups.id = grants.subject_id
            WHERE grants.subject_kind = 'group'
                AND (groups.builtin = 1 OR groups.id IN (SELECT group_id FROM group_members WHERE user_id = @userId))
            ORDER BY grants.resource, groups.name
            """,
            new { userId });
        return new UserGrants(
            own,
            [.. inherited.Select(row => new InheritedGrant(row.Resource, PermissionService.LevelOf(row.Level), row.GroupId, row.GroupName))],
            Session(await permissions.ForUserAsync(userId, cancellationToken)));
    }

    public async Task<UserGrants> ReplaceUserGrantsAsync(ClaimsPrincipal caller, long userId, GrantsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE id = @userId", new { userId }, transaction) == 0)
            {
                throw new PithyxException(ErrorCodes.UsersUserNotFound);
            }
            await ReplaceAsync(connection, transaction, SubjectKind.User, userId, request.Grants);
            await transaction.CommitAsync(cancellationToken);
        }
        permissions.Invalidate();
        return await UserGrantsAsync(caller, userId, cancellationToken);
    }

    public async Task<GroupGrants> GroupGrantsAsync(ClaimsPrincipal caller, long groupId, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await RequireGroupAsync(connection, null, groupId);
        return new GroupGrants(await SubjectGrantsAsync(connection, SubjectKind.Group, groupId));
    }

    public async Task<GroupGrants> ReplaceGroupGrantsAsync(ClaimsPrincipal caller, long groupId, GrantsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await RequireGroupAsync(connection, transaction, groupId);
            await ReplaceAsync(connection, transaction, SubjectKind.Group, groupId, request.Grants);
            await transaction.CommitAsync(cancellationToken);
        }
        permissions.Invalidate();
        return await GroupGrantsAsync(caller, groupId, cancellationToken);
    }

    public async Task<ShareGrants> ShareGrantsAsync(ClaimsPrincipal caller, long shareId, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await RequireShareAsync(connection, null, shareId);
        var resource = Resources.Share(shareId);
        var grants = await connection.QueryAsync<(string SubjectKind, long SubjectId, string Level)>(
            "SELECT subject_kind, subject_id, level FROM grants WHERE resource = @resource AND subject_kind <> 'app' ORDER BY subject_kind, subject_id",
            new { resource });
        var users = await connection.QueryAsync<(long Id, string Username, string? DisplayName)>("SELECT id, username, display_name FROM users ORDER BY username");
        var effective = (await PermissionService.AllUsersAsync(connection, null)).ToDictionary(user => user.Id);
        return new ShareGrants(
            [.. grants.Select(grant => new SubjectGrant(grant.SubjectKind == "group" ? SubjectKind.Group : SubjectKind.User, grant.SubjectId, PermissionService.LevelOf(grant.Level)))],
            [.. users.Select(user =>
            {
                var rights = effective[user.Id];
                return new ShareUserAccess(user.Id, user.Username, user.DisplayName, rights.Permissions.IsAdmin, rights.Disabled, rights.Permissions.Share(shareId));
            })]);
    }

    public async Task<ShareGrants> ReplaceShareGrantsAsync(ClaimsPrincipal caller, long shareId, ShareGrantsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var resource = Resources.Share(shareId);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await RequireShareAsync(connection, transaction, shareId);
            var seen = new HashSet<(SubjectKind, long)>();
            foreach (var grant in request.Grants ?? [])
            {
                if (grant.SubjectKind == SubjectKind.App || grant.Level == GrantLevel.Allow || !seen.Add((grant.SubjectKind, grant.SubjectId)))
                {
                    throw Invalid(resource);
                }
                if (grant.SubjectKind == SubjectKind.User)
                {
                    if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE id = @id", new { id = grant.SubjectId }, transaction) == 0)
                    {
                        throw new PithyxException(ErrorCodes.UsersUserNotFound);
                    }
                }
                else
                {
                    await RequireGroupAsync(connection, transaction, grant.SubjectId);
                }
            }
            await connection.ExecuteAsync("DELETE FROM grants WHERE resource = @resource AND subject_kind <> 'app'", new { resource }, transaction);
            foreach (var grant in request.Grants ?? [])
            {
                await connection.ExecuteAsync(
                    "INSERT INTO grants (subject_kind, subject_id, resource, level) VALUES (@kind, @id, @resource, @level)",
                    new { kind = PermissionService.KindText(grant.SubjectKind), id = grant.SubjectId, resource, level = PermissionService.LevelText(grant.Level) },
                    transaction);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        permissions.Invalidate();
        return await ShareGrantsAsync(caller, shareId, cancellationToken);
    }

    /// <summary>Who may use a third-party app: the grants of users and groups on <c>app:&lt;id&gt;</c>, and every user's result.</summary>
    public async Task<AppGrants> AppGrantsAsync(ClaimsPrincipal caller, string appId, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await RequireAppAsync(connection, null, appId);
        var resource = Resources.App(appId);
        var grants = await connection.QueryAsync<(string SubjectKind, long SubjectId)>(
            "SELECT subject_kind, subject_id FROM grants WHERE resource = @resource AND subject_kind <> 'app' ORDER BY subject_kind, subject_id",
            new { resource });
        var users = await connection.QueryAsync<(long Id, string Username, string? DisplayName)>("SELECT id, username, display_name FROM users ORDER BY username");
        var effective = (await PermissionService.AllUsersAsync(connection, null)).ToDictionary(user => user.Id);
        return new AppGrants(
            [.. grants.Select(grant => new SubjectGrant(grant.SubjectKind == "group" ? SubjectKind.Group : SubjectKind.User, grant.SubjectId, GrantLevel.Allow))],
            [.. users.Select(user =>
            {
                var rights = effective[user.Id];
                return new AppUserAccess(user.Id, user.Username, user.DisplayName, rights.Permissions.IsAdmin, rights.Disabled, !rights.Disabled && rights.Permissions.Has(resource));
            })]);
    }

    /// <summary>Replaces who may use a third-party app; a grant to the built-in group <c>users</c> gives it to everybody.</summary>
    public async Task<AppGrants> ReplaceAppGrantsAsync(ClaimsPrincipal caller, string appId, AppGrantsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var resource = Resources.App(appId);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await RequireAppAsync(connection, transaction, appId);
            var seen = new HashSet<(SubjectKind, long)>();
            foreach (var grant in request.Grants ?? [])
            {
                if (grant is null || grant.SubjectKind == SubjectKind.App || grant.Level != GrantLevel.Allow || !seen.Add((grant.SubjectKind, grant.SubjectId)))
                {
                    throw Invalid(resource);
                }
                if (grant.SubjectKind == SubjectKind.User)
                {
                    if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE id = @id", new { id = grant.SubjectId }, transaction) == 0)
                    {
                        throw new PithyxException(ErrorCodes.UsersUserNotFound);
                    }
                }
                else
                {
                    await RequireGroupAsync(connection, transaction, grant.SubjectId);
                }
            }
            await connection.ExecuteAsync("DELETE FROM grants WHERE resource = @resource AND subject_kind <> 'app'", new { resource }, transaction);
            foreach (var grant in request.Grants ?? [])
            {
                await connection.ExecuteAsync(
                    "INSERT INTO grants (subject_kind, subject_id, resource, level) VALUES (@kind, @id, @resource, 'allow')",
                    new { kind = PermissionService.KindText(grant.SubjectKind), id = grant.SubjectId, resource },
                    transaction);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        permissions.Invalidate();
        return await AppGrantsAsync(caller, appId, cancellationToken);
    }

    /// <summary>The desktop's view of effective rights: which rights, protocols and apps.</summary>
    public static SessionPermissions Session(EffectivePermissions effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return new SessionPermissions(
            effective.IsAdmin,
            [.. Resources.Rights.Where(effective.Has)],
            [.. Resources.Protocols.Where(effective.Has)],
            [
                .. CoreApps.Granted.Where(app => effective.Has(Resources.App(app))),
                .. effective.Allowed.Order(StringComparer.Ordinal).Select(Resources.ThirdPartyApp).OfType<string>(),
            ]);
    }

    /// <summary>Gives a new user its first grants, in the transaction that creates it.</summary>
    public static Task GrantNewUserAsync(DbConnection connection, DbTransaction transaction, long userId, IReadOnlyList<GrantEntry> grants) =>
        ReplaceAsync(connection, transaction, SubjectKind.User, userId, grants);

    private static async Task ReplaceAsync(DbConnection connection, DbTransaction transaction, SubjectKind kind, long subjectId, IReadOnlyList<GrantEntry>? grants)
    {
        var wanted = grants ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in wanted)
        {
            if (grant.Resource is null || !seen.Add(grant.Resource))
            {
                throw Invalid(grant.Resource);
            }
            if (Resources.ShareId(grant.Resource) is { } shareId)
            {
                if (grant.Level == GrantLevel.Allow)
                {
                    throw Invalid(grant.Resource);
                }
                await RequireShareAsync(connection, transaction, shareId);
            }
            else if (Resources.ThirdPartyApp(grant.Resource) is { } appId)
            {
                // A third-party app is given only while it is installed.
                if (grant.Level != GrantLevel.Allow || kind == SubjectKind.App
                    || await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM apps WHERE id = @appId", new { appId }, transaction) == 0)
                {
                    throw Invalid(grant.Resource);
                }
            }
            else if (!Resources.IsKnown(grant.Resource) || grant.Level != GrantLevel.Allow || (kind == SubjectKind.App && grant.Resource == Resources.Admin))
            {
                throw Invalid(grant.Resource);
            }
        }
        var subjectKind = PermissionService.KindText(kind);
        await connection.ExecuteAsync("DELETE FROM grants WHERE subject_kind = @subjectKind AND subject_id = @subjectId", new { subjectKind, subjectId }, transaction);
        foreach (var grant in wanted)
        {
            await connection.ExecuteAsync(
                "INSERT INTO grants (subject_kind, subject_id, resource, level) VALUES (@subjectKind, @subjectId, @resource, @level)",
                new { subjectKind, subjectId, resource = grant.Resource, level = PermissionService.LevelText(grant.Level) },
                transaction);
        }
        await PermissionService.EnsureAdministratorRemainsAsync(connection, transaction);
    }

    private static async Task<IReadOnlyList<GrantEntry>> SubjectGrantsAsync(DbConnection connection, SubjectKind kind, long subjectId)
    {
        var rows = await connection.QueryAsync<(string Resource, string Level)>(
            "SELECT resource, level FROM grants WHERE subject_kind = @subjectKind AND subject_id = @subjectId ORDER BY resource",
            new { subjectKind = PermissionService.KindText(kind), subjectId });
        return [.. rows.Select(row => new GrantEntry(row.Resource, PermissionService.LevelOf(row.Level)))];
    }

    private static async Task RequireGroupAsync(DbConnection connection, DbTransaction? transaction, long groupId)
    {
        if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM groups WHERE id = @groupId", new { groupId }, transaction) == 0)
        {
            throw new PithyxException(ErrorCodes.UsersGroupNotFound);
        }
    }

    /// <summary>Only installed third-party apps have grants of their own here; the core apps' grants belong to the user and group editors.</summary>
    private static async Task RequireAppAsync(DbConnection connection, DbTransaction? transaction, string appId)
    {
        if (Resources.ThirdPartyApp(Resources.App(appId)) is null
            || await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM apps WHERE id = @appId", new { appId }, transaction) == 0)
        {
            throw new PithyxException(ErrorCodes.AppAppNotFound, new Dictionary<string, object?> { ["id"] = appId.Length > 100 ? appId[..100] : appId });
        }
    }

    private static async Task RequireShareAsync(DbConnection connection, DbTransaction? transaction, long shareId)
    {
        if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM shares WHERE id = @shareId", new { shareId }, transaction) == 0)
        {
            throw new PithyxException(ErrorCodes.FsShareNotFound);
        }
    }

    private static PithyxException Invalid(string? resource) =>
        new(ErrorCodes.PermissionsGrantInvalid, new Dictionary<string, object?> { ["resource"] = resource ?? "" });

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}
