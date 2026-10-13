using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Permissions;

/// <summary>A user with its effective rights, for the reconcilers of accounts, ACLs and file services.</summary>
/// <param name="Id">The user's id.</param>
/// <param name="Username">The Linux user name.</param>
/// <param name="Uid">The Linux uid, or null for a user from before Linux accounts.</param>
/// <param name="Disabled">Deactivated: no rights at all.</param>
/// <param name="Permissions">What the user may do.</param>
public sealed record UserPermissions(long Id, string Username, long? Uid, bool Disabled, EffectivePermissions Permissions);

/// <summary>
/// Grants in SQLite and the effective rights of every user (step 11, D290), resolved by <see cref="PermissionResolver"/>
/// and kept in memory until any grant, group, membership or user changes. Every request and every topic asks here, so
/// a changed right applies to the next one; <see cref="Changed"/> tells the WebSocket, the Linux groups, the ACLs and
/// the file services.
/// </summary>
public sealed class PermissionService(SqliteConnectionFactory connections)
{
    private readonly ConcurrentDictionary<long, EffectivePermissions> _cache = new();
    private long _version;

    /// <summary>Raised after anything that decides rights changed.</summary>
    public event Action? Changed;

    /// <summary>Forgets every resolved right and tells the listeners. Call after every write that touches rights.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _cache.Clear();
        Changed?.Invoke();
    }

    public async Task<EffectivePermissions> ForUserAsync(long userId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }
        var version = Interlocked.Read(ref _version);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var resolved = await ResolveAsync(connection, null, userId);
        // A change during the read must not leave the old answer behind.
        if (Interlocked.Read(ref _version) == version)
        {
            _cache[userId] = resolved;
        }
        return resolved;
    }

    /// <summary>For callers that must not wait, such as permission checks of realtime events.</summary>
    public EffectivePermissions ForUser(long userId)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }
        var version = Interlocked.Read(ref _version);
        using var connection = connections.Open();
        var resolved = ResolveAsync(connection, null, userId).GetAwaiter().GetResult();
        if (Interlocked.Read(ref _version) == version)
        {
            _cache[userId] = resolved;
        }
        return resolved;
    }

    /// <summary>The rights of the caller of a request or a WebSocket; nothing without a session.</summary>
    public EffectivePermissions ForUser(ClaimsPrincipal user) =>
        user?.Identity?.IsAuthenticated == true ? ForUser(CurrentUser.Id(user)) : EffectivePermissions.None;

    /// <summary>Every user with its effective rights.</summary>
    public async Task<IReadOnlyList<UserPermissions>> AllUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await AllUsersAsync(connection, null);
    }

    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The caller's transaction, if any.</param>
    /// <param name="asIfActive">Resolves deactivated users as if they were active, for showing what they would have.</param>
    public static async Task<IReadOnlyList<UserPermissions>> AllUsersAsync(DbConnection connection, DbTransaction? transaction, bool asIfActive = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var users = await connection.QueryAsync<(long Id, string Username, long? Uid, string? DisabledAt)>(
            "SELECT id, username, uid, disabled_at FROM users ORDER BY username", transaction: transaction);
        var grants = await GrantsAsync(connection, transaction);
        var groups = await GroupsOfAsync(connection, transaction, null);
        return [.. users.Select(user => new UserPermissions(
            user.Id,
            user.Username,
            user.Uid,
            user.DisabledAt is not null,
            PermissionResolver.Resolve(Principal(user.Id, groups[user.Id], asIfActive || user.DisabledAt is null), grants)))];
    }

    /// <summary>
    /// Refuses a change that leaves no active administrator. Runs inside the change's transaction, after the change
    /// and before the commit.
    /// </summary>
    public static async Task EnsureAdministratorRemainsAsync(DbConnection connection, DbTransaction transaction)
    {
        if (!(await AllUsersAsync(connection, transaction)).Any(user => user.Permissions.IsAdmin))
        {
            throw new PithyxException(ErrorCodes.UsersUserLastAdmin);
        }
    }

    public static Principal Principal(long userId, IEnumerable<long> groupIds, bool active) =>
        new(SubjectKind.User, userId, [GroupReconciler.UsersGid, .. groupIds], active);

    private static async Task<EffectivePermissions> ResolveAsync(DbConnection connection, DbTransaction? transaction, long userId)
    {
        var active = await connection.QuerySingleOrDefaultAsync<long?>(
            "SELECT disabled_at IS NULL FROM users WHERE id = @userId", new { userId }, transaction);
        if (active is not 1)
        {
            return EffectivePermissions.None;
        }
        var groups = (await GroupsOfAsync(connection, transaction, userId))[userId];
        var grants = await connection.QueryAsync<GrantRow>(
            $"""
            SELECT subject_kind AS SubjectKind, subject_id AS SubjectId, resource AS Resource, level AS Level FROM grants
            WHERE (subject_kind = 'user' AND subject_id = @userId)
                OR (subject_kind = 'group' AND (subject_id = @builtin OR subject_id IN (SELECT group_id FROM group_members WHERE user_id = @userId)))
            UNION ALL {HomeGrants} WHERE home_user_id = @userId
            """,
            new { userId, builtin = GroupReconciler.UsersGid },
            transaction);
        return PermissionResolver.Resolve(Principal(userId, groups, active: true), grants.Select(row => row.ToGrant()));
    }

    /// <summary>
    /// Every user reads and writes its own home (D348). The grant is not stored: it follows the home's row, so it
    /// cannot be granted, changed or revoked, and nobody else gets one.
    /// </summary>
    private const string HomeGrants =
        "SELECT 'user' AS SubjectKind, home_user_id AS SubjectId, 'share:' || id AS Resource, 'readWrite' AS Level FROM shares";

    /// <summary>Every grant in the database, and the grant of every user on its own home.</summary>
    public static async Task<IReadOnlyList<Grant>> GrantsAsync(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var rows = await connection.QueryAsync<GrantRow>(
            $"SELECT subject_kind AS SubjectKind, subject_id AS SubjectId, resource AS Resource, level AS Level FROM grants UNION ALL {HomeGrants} WHERE home_user_id IS NOT NULL",
            transaction: transaction);
        return [.. rows.Select(row => row.ToGrant())];
    }

    private static async Task<ILookup<long, long>> GroupsOfAsync(DbConnection connection, DbTransaction? transaction, long? userId) =>
        (await connection.QueryAsync<(long UserId, long GroupId)>(
            "SELECT user_id, group_id FROM group_members WHERE @userId IS NULL OR user_id = @userId",
            new { userId },
            transaction)).ToLookup(row => row.UserId, row => row.GroupId);

    public static string LevelText(GrantLevel level) => level switch
    {
        GrantLevel.Read => "read",
        GrantLevel.ReadWrite => "readWrite",
        _ => "allow",
    };

    public static GrantLevel LevelOf(string text) => text switch
    {
        "read" => GrantLevel.Read,
        "readWrite" => GrantLevel.ReadWrite,
        _ => GrantLevel.Allow,
    };

    public static string KindText(SubjectKind kind) => kind switch
    {
        SubjectKind.User => "user",
        SubjectKind.Group => "group",
        _ => "app",
    };

    private sealed record GrantRow(string SubjectKind, long SubjectId, string Resource, string Level)
    {
        public Grant ToGrant() => new(
            SubjectKind switch { "user" => Permissions.SubjectKind.User, "group" => Permissions.SubjectKind.Group, _ => Permissions.SubjectKind.App },
            SubjectId,
            Resource,
            Level == "read" ? ShareAccess.Read : ShareAccess.ReadWrite);
    }
}
