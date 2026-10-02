using System.Globalization;
using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Users;

/// <summary>
/// User management for administrators: create, change, reset the password, delete. Every user gets a uid from the
/// Pithyx range that is never given out again, and a Linux account (<see cref="LinuxAccounts"/>). The last
/// active administrator can neither be deleted, deactivated nor lose the right, and nobody deletes or deactivates
/// themselves. A user created here takes
/// its uid as its id, so an id is never given out again either: notifications, jobs and grants that still name a
/// deleted user's id can never reach a new user.
/// </summary>
public sealed class UserService(
    SqliteConnectionFactory connections,
    PasswordHasher hasher,
    LinuxAccounts accounts,
    NotificationStore notifications,
    JobService jobs,
    ShareChanges shareChanges,
    FileServicePasswords passwords,
    FileServiceChanges fileServiceChanges,
    TimeProvider time)
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(Select + " ORDER BY username");
        var linux = passwords.LinuxWithPassword();
        return [.. rows.Select(row => ToSummary(row, linux))];
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
                INSERT INTO users (id, username, display_name, password_hash, is_admin, uid, smb, sftp, created_at, updated_at)
                VALUES (@uid, @username, @displayName, @hash, @admin, @uid, @smb, @sftp, @now, @now) RETURNING id
                """,
                new { username, displayName, hash, admin = request.IsAdmin ? 1 : 0, uid, smb = request.Smb ? 1 : 0, sftp = request.Sftp ? 1 : 0, now },
                transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        var home = await accounts.CreateHomeAsync(username, uid, cancellationToken);
        var problems = await accounts.ReconcileAsync(cancellationToken);
        if (problems.TryGetValue(username, out var reason))
        {
            // The uid stays allocated: numbers are never reused.
            await using var connection = await connections.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id });
            throw new PithyxException(ErrorCodes.UsersAccountRefused, new Dictionary<string, object?> { ["username"] = username, ["reason"] = reason });
        }
        // SMB and SFTP check the Linux and Samba accounts, which get the password now that the account exists.
        await passwords.SetAsync(username, request.Password, cancellationToken);
        if (request.IsAdmin)
        {
            // Administrators reach every shared folder: their ACLs follow.
            shareChanges.Announce();
        }
        fileServiceChanges.Announce();
        return (await FindAsync(id, cancellationToken))! with { Home = home ?? HomeOrNull(username) };
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
            var losesAdmin = user.IsAdmin && !user.Disabled && (request.IsAdmin == false || disabling);
            if (losesAdmin && await AdminCountAsync(connection, transaction) <= 1)
            {
                throw new PithyxException(ErrorCodes.UsersUserLastAdmin);
            }
            var displayName = request.DisplayName is null ? user.DisplayName : CredentialRules.NormalizeDisplayName(request.DisplayName);
            var admin = request.IsAdmin ?? user.IsAdmin;
            var disabled = request.Disabled ?? user.Disabled;
            var smb = request.Smb ?? user.Smb;
            var sftp = request.Sftp ?? user.Sftp;
            await connection.ExecuteAsync(
                """
                UPDATE users SET display_name = @displayName, is_admin = @admin, smb = @smb, sftp = @sftp,
                    disabled_at = CASE WHEN @disabled THEN coalesce(disabled_at, @now) END, updated_at = @now
                WHERE id = @id
                """,
                new { id, displayName, admin = admin ? 1 : 0, smb = smb ? 1 : 0, sftp = sftp ? 1 : 0, disabled, now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) },
                transaction);
            if (disabling)
            {
                await connection.ExecuteAsync("DELETE FROM sessions WHERE user_id = @id", new { id }, transaction);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        await accounts.ReconcileAsync(cancellationToken);
        if ((request.IsAdmin is { } isAdmin && isAdmin != user.IsAdmin) || (request.Disabled is { } off && off != user.Disabled))
        {
            shareChanges.Announce();
        }
        fileServiceChanges.Announce();
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
    /// Deletes the user and its Linux account. Its files and home stay; its uid is never given out again. Its queued
    /// and running jobs are cancelled, and its jobs and notifications leave memory.
    /// </summary>
    public async Task DeleteAsync(ClaimsPrincipal caller, long id, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        if (id == CurrentUser.Id(caller))
        {
            throw new PithyxException(ErrorCodes.UsersUserSelf);
        }
        var user = await FindAsync(id, cancellationToken) ?? throw NotFound();
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (user.IsAdmin && !user.Disabled && await AdminCountAsync(connection, transaction) <= 1)
            {
                throw new PithyxException(ErrorCodes.UsersUserLastAdmin);
            }
            await connection.ExecuteAsync("DELETE FROM share_access WHERE user_id = @id", new { id }, transaction);
            await connection.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id }, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        await jobs.ForgetUserAsync(id);
        notifications.Forget(id);
        await accounts.ReconcileAsync(cancellationToken);
        shareChanges.Announce();
        fileServiceChanges.Announce();
    }

    /// <summary>Administrators who can still sign in.</summary>
    private static Task<long> AdminCountAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction) =>
        connection.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE is_admin = 1 AND disabled_at IS NULL", transaction: transaction);

    private async Task<UserSummary?> FindAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(Select + " WHERE id = @id", new { id });
        return row is null ? null : ToSummary(row, passwords.LinuxWithPassword());
    }

    private string? HomeOrNull(string username) => accounts.HomeOf(username) is var home && home != AccountReconciler.NoHome ? home : null;

    private UserSummary ToSummary(Row row, IReadOnlySet<string> linuxWithPassword) => new(
        row.Id,
        row.Username,
        row.DisplayName,
        row.IsAdmin == 1,
        row.Uid,
        row.TotpEnabledAt is not null,
        DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        HomeOrNull(row.Username),
        row.DisabledAt is not null,
        row.IsAdmin == 1 || row.Smb == 1,
        row.IsAdmin == 1 || row.Sftp == 1,
        passwords.HasPassword(row.Username, linuxWithPassword));

    private static PithyxException NotFound() => new(ErrorCodes.UsersUserNotFound);

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }

    private const string Select = "SELECT id, username, display_name AS displayName, is_admin AS isAdmin, uid, totp_enabled_at AS totpEnabledAt, created_at AS createdAt, disabled_at AS disabledAt, smb, sftp FROM users";

    private sealed record Row(long Id, string Username, string? DisplayName, long IsAdmin, long? Uid, string? TotpEnabledAt, string CreatedAt, string? DisabledAt, long Smb, long Sftp);
}
