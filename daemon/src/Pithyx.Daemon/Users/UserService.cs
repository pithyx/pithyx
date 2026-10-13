using System.Globalization;
using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.Users;

/// <summary>
/// User management for administrators: create, change, reset the password, delete. Every user gets a uid from the
/// Pithyx range that is never given out again, and a Linux account (<see cref="LinuxAccounts"/>). Its rights are
/// grants (<see cref="GrantService"/>); no change may leave the box without an active administrator, and nobody
/// deletes or deactivates themselves. A user created here takes
/// its uid as its id, so an id is never given out again either: notifications, jobs and grants that still name a
/// deleted user's id can never reach a new user.
/// </summary>
public sealed class UserService(
    SqliteConnectionFactory connections,
    PasswordHasher hasher,
    LinuxAccounts accounts,
    NotificationStore notifications,
    JobService jobs,
    PermissionService permissions,
    FileServicePasswords passwords,
    Homes homes,
    HomeService homeService,
    TimeProvider time)
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await SummariesAsync(connection, null);
    }

    public async Task<UserSummary> CreateAsync(ClaimsPrincipal caller, NewUser request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var username = CredentialRules.NormalizeUsername(request.Username);
        var displayName = CredentialRules.NormalizeDisplayName(request.DisplayName);
        CredentialRules.ValidateNewPassword(request.Password);
        // An account outside the Pithyx range with this name belongs to the system or to someone else.
        if (accounts.Actual().FirstOrDefault(entry => entry.Name == username) is { } foreign && !AccountReconciler.IsManaged(foreign.Uid))
        {
            throw new PithyxException(ErrorCodes.UsersAccountRefused, new Dictionary<string, object?> { ["username"] = username, ["reason"] = "name_taken" });
        }
        var hash = await hasher.HashAsync(request.Password, cancellationToken);
        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        long id;
        long uid;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE username = @username", new { username }, transaction) > 0)
            {
                throw new PithyxException(ErrorCodes.UsersUserExists, new Dictionary<string, object?> { ["username"] = username });
            }
            uid = await UidAllocator.AllocateUserAsync(connection, transaction, username, now);
            id = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (id, username, display_name, password_hash, uid, created_at, updated_at)
                VALUES (@uid, @username, @displayName, @hash, @uid, @now, @now) RETURNING id
                """,
                new { username, displayName, hash, uid, now },
                transaction);
            await GroupService.SetUserGroupsAsync(connection, transaction, id, request.GroupIds ?? []);
            await GrantService.GrantNewUserAsync(
                connection,
                transaction,
                id,
                request.Grants ?? [.. Resources.NewUserDefaults.Select(resource => new GrantEntry(resource, GrantLevel.Allow))]);
            await transaction.CommitAsync(cancellationToken);
        }
        permissions.Invalidate();
        // The home first, so the account gets it as its home directory.
        await homes.ReconcileAsync(cancellationToken);
        var problems = await accounts.ReconcileAsync(cancellationToken);
        if (problems.TryGetValue(username, out var reason))
        {
            // The uid stays allocated: numbers are never reused.
            await using var connection = await connections.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id });
            permissions.Invalidate();
            throw new PithyxException(ErrorCodes.UsersAccountRefused, new Dictionary<string, object?> { ["username"] = username, ["reason"] = reason });
        }
        // SMB and SFTP check the Linux and Samba accounts, which get the password now that the account exists.
        await passwords.SetAsync(username, request.Password, cancellationToken);
        return (await FindAsync(id, cancellationToken))!;
    }

    public async Task<UserSummary> UpdateAsync(ClaimsPrincipal caller, long id, UserUpdate request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var user = await FindAsync(id, cancellationToken) ?? throw NotFound();
        var disabling = request.Disabled == true && !user.Disabled;
        if (disabling && id == CurrentUser.Id(caller))
        {
            throw new PithyxException(ErrorCodes.UsersUserSelf);
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var displayName = request.DisplayName is null ? user.DisplayName : CredentialRules.NormalizeDisplayName(request.DisplayName);
            var disabled = request.Disabled ?? user.Disabled;
            await connection.ExecuteAsync(
                """
                UPDATE users SET display_name = @displayName,
                    disabled_at = CASE WHEN @disabled THEN coalesce(disabled_at, @now) END, updated_at = @now
                WHERE id = @id
                """,
                new { id, displayName, disabled, now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) },
                transaction);
            if (disabling)
            {
                await connection.ExecuteAsync("DELETE FROM sessions WHERE user_id = @id", new { id }, transaction);
            }
            if (request.GroupIds is { } groupIds)
            {
                await GroupService.SetUserGroupsAsync(connection, transaction, id, groupIds);
            }
            await PermissionService.EnsureAdministratorRemainsAsync(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        await accounts.ReconcileAsync(cancellationToken);
        permissions.Invalidate();
        return (await FindAsync(id, cancellationToken))!;
    }

    /// <summary>Sets a new password and ends every session of the user; returns the user's id for closing its connections.</summary>
    public async Task ResetPasswordAsync(ClaimsPrincipal caller, long id, PasswordReset request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        CredentialRules.ValidateNewPassword(request.Password);
        var user = await FindAsync(id, cancellationToken) ?? throw NotFound();
        var hash = await hasher.HashAsync(request.Password, cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE users SET password_hash = @hash, updated_at = @now WHERE id = @id",
            new { id, hash, now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) },
            transaction);
        await connection.ExecuteAsync("DELETE FROM sessions WHERE user_id = @id", new { id }, transaction);
        await transaction.CommitAsync(cancellationToken);
        await passwords.SetAsync(user.Username, request.Password, cancellationToken);
    }

    /// <summary>
    /// Deletes the user and its Linux account; its uid is never given out again. Its home stays under
    /// <c>&lt;name&gt;.deleted-&lt;uid&gt;</c>, is deleted after the user name was typed, or moves into a shared folder as a
    /// job (D348); its other files stay. Its queued and running jobs are cancelled, and its jobs and notifications
    /// leave memory.
    /// </summary>
    public async Task<UserDeletion> DeleteAsync(ClaimsPrincipal caller, long id, HomeDataChoice home, long? shareId, string? confirm, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        if (id == CurrentUser.Id(caller))
        {
            throw new PithyxException(ErrorCodes.UsersUserSelf);
        }
        var user = await FindAsync(id, cancellationToken) ?? throw NotFound();
        await homeService.CheckChoiceAsync(user.Username, home, shareId, confirm, cancellationToken);
        long? homeId;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            homeId = await connection.ExecuteScalarAsync<long?>("SELECT id FROM shares WHERE home = 1 AND home_user_id = @id", new { id }, transaction);
            // Its grants and memberships go with it; its home loses its user.
            await connection.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id }, transaction);
            await PermissionService.EnsureAdministratorRemainsAsync(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        await jobs.ForgetUserAsync(id);
        notifications.Forget(id);
        await accounts.ReconcileAsync(cancellationToken);
        permissions.Invalidate();
        if (homeId is not { } homeShare || user.Uid is not { } uid)
        {
            return new UserDeletion(null);
        }
        return new UserDeletion(await homeService.UserDeletedAsync(caller, homeShare, user.Username, uid, home, shareId, cancellationToken));
    }

    private async Task<UserSummary?> FindAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return (await SummariesAsync(connection, id)) is [var user] ? user : null;
    }

    private async Task<IReadOnlyList<UserSummary>> SummariesAsync(System.Data.Common.DbConnection connection, long? id)
    {
        var rows = await connection.QueryAsync<Row>(Select + " WHERE @id IS NULL OR users.id = @id ORDER BY users.username", new { id });
        var linux = passwords.LinuxWithPassword();
        var groups = (await connection.QueryAsync<(long UserId, long GroupId)>("SELECT user_id, group_id FROM group_members ORDER BY group_id"))
            .ToLookup(member => member.UserId, member => member.GroupId);
        // What deactivated users would have, so the page shows it next to "Deactivated".
        var rights = (await PermissionService.AllUsersAsync(connection, null, asIfActive: true)).ToDictionary(user => user.Id, user => user.Permissions);
        return [.. rows.Select(row => ToSummary(row, linux, [.. groups[row.Id]], rights[row.Id]))];
    }

    private UserSummary ToSummary(Row row, IReadOnlySet<string> linuxWithPassword, IReadOnlyList<long> groupIds, EffectivePermissions rights)
    {
        var session = GrantService.Session(rights);
        return new(
            row.Id,
            row.Username,
            row.DisplayName,
            rights.IsAdmin,
            row.Uid,
            row.TotpEnabledAt is not null,
            DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            row.Home,
            row.DisabledAt is not null,
            session.Rights,
            session.Protocols,
            passwords.HasPassword(row.Username, linuxWithPassword),
            groupIds);
    }

    private static PithyxException NotFound() => new(ErrorCodes.UsersUserNotFound);

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }

    private const string Select =
        """
        SELECT users.id, users.username, users.display_name AS displayName, users.uid, users.totp_enabled_at AS totpEnabledAt,
            users.created_at AS createdAt, users.disabled_at AS disabledAt, shares.path AS home, users.home_quota_bytes AS homeQuotaBytes
        FROM users LEFT JOIN shares ON shares.home = 1 AND shares.home_user_id = users.id
        """;

    private sealed record Row(long Id, string Username, string? DisplayName, long? Uid, string? TotpEnabledAt, string CreatedAt, string? DisabledAt, string? Home, long? HomeQuotaBytes);
}
