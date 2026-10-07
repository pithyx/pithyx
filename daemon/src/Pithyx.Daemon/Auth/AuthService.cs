using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using OtpNet;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Layouts;

namespace Pithyx.Daemon.Auth;

/// <summary>Setup, login, TOTP and recovery. Sessions and cookies are handled by the endpoints.</summary>
public sealed class AuthService(SqliteConnectionFactory connections, PasswordHasher hasher, LoginChallenges challenges, Permissions.PermissionService permissions, TimeProvider time)
{
    public const string RecoveryKeySetting = "recovery_key_hash";
    public const string RecoveryKeyPrefix = "PTHX";
    private const int RecoveryKeyCharacters = 26;
    private const int RecoveryCodeCount = 10;
    private const int RecoveryCodeCharacters = 10;

    public sealed record LoginOutcome(UserInfo? User, string? Challenge);

    public async Task<bool> SetupRequiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return !await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM users)");
    }

    /// <summary>Creates the first administrator and the recovery key. Only possible while no user exists.</summary>
    public async Task<(UserInfo User, string RecoveryKey)> SetupAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = CredentialRules.NormalizeUsername(request.Username);
        var displayName = CredentialRules.NormalizeDisplayName(request.DisplayName);
        CredentialRules.ValidateNewPassword(request.Password);
        if (!await SetupRequiredAsync(cancellationToken))
        {
            throw new PithyxException(ErrorCodes.AuthSetupAlreadyDone);
        }

        var passwordHash = await hasher.HashAsync(request.Password, cancellationToken);
        var recoveryKey = NewRecoveryKey();
        var recoveryKeyHash = await hasher.HashAsync(NormalizeRecoveryKey(recoveryKey), cancellationToken);
        var now = SessionService.Format(time.GetUtcNow());

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM users", transaction: transaction) > 0)
        {
            throw new PithyxException(ErrorCodes.AuthSetupAlreadyDone);
        }
        var uid = await Users.UidAllocator.AllocateUserAsync(connection, transaction, username, now);
        var id = await connection.ExecuteScalarAsync<long?>(
            """
            INSERT INTO users (username, display_name, password_hash, uid, created_at, updated_at)
            SELECT @username, @displayName, @passwordHash, @uid, @now, @now
            WHERE NOT EXISTS (SELECT 1 FROM users)
            RETURNING id
            """,
            new { username, displayName, passwordHash, uid, now },
            transaction);
        if (id is null)
        {
            throw new PithyxException(ErrorCodes.AuthSetupAlreadyDone);
        }
        // The first administrator gets the administrator right and the desktop of its own; it has everything else through the first.
        await Permissions.GrantService.GrantNewUserAsync(connection, transaction, id.Value, [new(Permissions.Resources.Admin, Permissions.GrantLevel.Allow), new(Permissions.Resources.DesktopLogin, Permissions.GrantLevel.Allow)]);
        await SetSettingAsync(connection, transaction, RecoveryKeySetting, recoveryKeyHash);
        await DesktopItemService.SeedDefaultsAsync(connection, transaction, id.Value, isAdmin: true, now);
        await transaction.CommitAsync(cancellationToken);
        permissions.Invalidate();
        return ((await GetUserAsync(id.Value, cancellationToken))!, recoveryKey);
    }

    /// <summary>Checks the password. Returns the user, or a challenge when TOTP is enabled.</summary>
    public async Task<LoginOutcome> LoginAsync(LoginRequest request, string address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await FindByUsernameAsync(request.Username, cancellationToken);
        if (user is null)
        {
            await hasher.WasteTimeAsync(request.Password, cancellationToken);
            throw new PithyxException(ErrorCodes.AuthCredentialsInvalid);
        }

        var (valid, needsRehash) = await hasher.VerifyAsync(request.Password, user.PasswordHash, cancellationToken);
        if (!valid)
        {
            throw new PithyxException(ErrorCodes.AuthCredentialsInvalid);
        }
        // Only after the password: a stranger never tells a deactivated account from a wrong password.
        if (user.DisabledAt is not null)
        {
            throw new PithyxException(ErrorCodes.AuthAccountDisabled);
        }
        var info = await InfoAsync(user, cancellationToken);
        if (!info.Permissions.Rights.Contains(Permissions.Resources.DesktopLogin))
        {
            throw new PithyxException(ErrorCodes.AuthDesktopNotAllowed);
        }
        if (needsRehash)
        {
            await SetPasswordAsync(user.Id, request.Password, cancellationToken);
        }

        return user.TotpEnabledAt is null
            ? new LoginOutcome(info, null)
            : new LoginOutcome(null, challenges.Create(user.Id, user.Username, address));
    }

    /// <summary>The user name a pending login challenge belongs to, for rate limiting.</summary>
    public string? ChallengeUsername(string challenge, string address) => challenges.Find(challenge, address)?.Username;

    /// <summary>Completes a login with a TOTP code or a recovery code.</summary>
    public async Task<UserInfo> CompleteTotpLoginAsync(TotpLoginRequest request, string address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pending = challenges.Find(request.Challenge, address) ?? throw new PithyxException(ErrorCodes.AuthChallengeExpired);
        var user = await FindByIdAsync(pending.UserId, cancellationToken) ?? throw new PithyxException(ErrorCodes.AuthChallengeExpired);
        if (user.DisabledAt is not null)
        {
            throw new PithyxException(ErrorCodes.AuthAccountDisabled);
        }

        var accepted = request.Code is { Length: > 0 } code
            ? await TryUseTotpCodeAsync(user, code, cancellationToken)
            : request.RecoveryCode is { Length: > 0 } recoveryCode && await TryUseRecoveryCodeAsync(user.Id, recoveryCode, cancellationToken);
        if (!accepted)
        {
            throw new PithyxException(ErrorCodes.AuthTotpInvalid);
        }

        var info = await InfoAsync(user, cancellationToken);
        if (!info.Permissions.Rights.Contains(Permissions.Resources.DesktopLogin))
        {
            throw new PithyxException(ErrorCodes.AuthDesktopNotAllowed);
        }
        challenges.Complete(request.Challenge);
        return info;
    }

    /// <summary>The user as the desktop sees it, with its groups.</summary>
    public async Task<UserInfo?> GetUserAsync(long userId, CancellationToken cancellationToken) =>
        await FindByIdAsync(userId, cancellationToken) is { } user ? await InfoAsync(user, cancellationToken) : null;

    /// <summary>
    /// The user and its rights for authenticating one request, without the group lookup (<see cref="UserInfo.Groups"/>
    /// is empty): every request asks this.
    /// </summary>
    public async Task<(UserInfo User, Permissions.EffectivePermissions Rights)?> SignedInAsync(long userId, CancellationToken cancellationToken)
    {
        if (await FindByIdAsync(userId, cancellationToken) is not { } user)
        {
            return null;
        }
        var effective = await permissions.ForUserAsync(user.Id, cancellationToken);
        return (Info(user, effective, []), effective);
    }

    private async Task<UserInfo> InfoAsync(UserRow user, CancellationToken cancellationToken)
    {
        var effective = await permissions.ForUserAsync(user.Id, cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var groups = await connection.QueryAsync<GroupRef>(
            "SELECT g.id AS Id, g.name AS Name FROM group_members m JOIN groups g ON g.id = m.group_id WHERE m.user_id = @Id ORDER BY g.name",
            new { user.Id });
        return Info(user, effective, [.. groups]);
    }

    private static UserInfo Info(UserRow user, Permissions.EffectivePermissions effective, IReadOnlyList<GroupRef> groups) =>
        new(user.Id, user.Username, user.DisplayName, effective.IsAdmin, user.TotpEnabledAt is not null, Permissions.GrantService.Session(effective), groups);

    /// <summary>Starts TOTP enrollment with a new secret. TOTP stays off until a code is confirmed.</summary>
    public async Task<TotpEnrollment> EnrollTotpAsync(long userId, string hostName, CancellationToken cancellationToken)
    {
        var user = await FindByIdAsync(userId, cancellationToken) ?? throw new PithyxException(ErrorCodes.AuthSessionExpired);
        if (user.TotpEnabledAt is not null)
        {
            throw new PithyxException(ErrorCodes.AuthTotpAlreadyEnabled);
        }

        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE users SET totp_secret = @secret, totp_last_used_step = NULL, updated_at = @now WHERE id = @userId",
            new { secret, now = SessionService.Format(time.GetUtcNow()), userId });

        var label = $"{Uri.EscapeDataString("Pithyx")}:{Uri.EscapeDataString($"{user.Username}@{hostName}")}";
        return new TotpEnrollment(secret, $"otpauth://totp/{label}?secret={secret}&issuer=Pithyx&algorithm=SHA1&digits=6&period=30");
    }

    /// <summary>Enables TOTP after the first valid code and returns new recovery codes.</summary>
    public async Task<IReadOnlyList<string>> ConfirmTotpAsync(long userId, string code, CancellationToken cancellationToken)
    {
        var user = await FindByIdAsync(userId, cancellationToken) ?? throw new PithyxException(ErrorCodes.AuthSessionExpired);
        if (user.TotpEnabledAt is not null)
        {
            throw new PithyxException(ErrorCodes.AuthTotpAlreadyEnabled);
        }
        if (user.TotpSecret is null)
        {
            throw new PithyxException(ErrorCodes.AuthTotpNotEnrolled);
        }
        if (!await TryUseTotpCodeAsync(user, code, cancellationToken))
        {
            throw new PithyxException(ErrorCodes.AuthTotpInvalid);
        }

        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => Secrets.NewCode(RecoveryCodeCharacters, 5)).ToList();
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE users SET totp_enabled_at = @now, updated_at = @now WHERE id = @userId",
            new { now = SessionService.Format(time.GetUtcNow()), userId },
            transaction);
        await connection.ExecuteAsync("DELETE FROM recovery_codes WHERE user_id = @userId", new { userId }, transaction);
        await connection.ExecuteAsync(
            "INSERT INTO recovery_codes (user_id, code_hash) VALUES (@userId, @hash)",
            codes.Select(c => new { userId, hash = Secrets.Sha256(Secrets.NormalizeCode(c)) }),
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return codes;
    }

    public async Task DisableTotpAsync(long userId, string password, CancellationToken cancellationToken)
    {
        var user = await FindByIdAsync(userId, cancellationToken) ?? throw new PithyxException(ErrorCodes.AuthSessionExpired);
        if (!(await hasher.VerifyAsync(password, user.PasswordHash, cancellationToken)).Valid)
        {
            throw new PithyxException(ErrorCodes.AuthCredentialsInvalid);
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ClearTotpAsync(connection, transaction, userId);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Resets the administrator password with the recovery key, ends all sessions, turns TOTP off (the device may be
    /// lost) and replaces the recovery key. Returns the new key, which is shown once.
    /// </summary>
    public async Task<(long UserId, string RecoveryKey)> RecoverAsync(RecoverRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CredentialRules.ValidateNewPassword(request.NewPassword);
        var user = await FindByUsernameAsync(request.Username, cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var keyHash = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = RecoveryKeySetting });
        var normalizedKey = NormalizeRecoveryKey(request.RecoveryKey);
        // Only for administrators; a deactivated one counts, as before the permission model.
        var admin = user is not null
            && (await Permissions.PermissionService.AllUsersAsync(connection, null, asIfActive: true)).Any(other => other.Id == user.Id && other.Permissions.IsAdmin);
        if (user is null || !admin || keyHash is null)
        {
            await hasher.WasteTimeAsync(normalizedKey, cancellationToken);
            throw new PithyxException(ErrorCodes.AuthRecoveryInvalid);
        }
        if (!(await hasher.VerifyAsync(normalizedKey, keyHash, cancellationToken)).Valid)
        {
            throw new PithyxException(ErrorCodes.AuthRecoveryInvalid);
        }

        var newKey = NewRecoveryKey();
        var newKeyHash = await hasher.HashAsync(NormalizeRecoveryKey(newKey), cancellationToken);
        var passwordHash = await hasher.HashAsync(request.NewPassword, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE users SET password_hash = @passwordHash, updated_at = @now WHERE id = @Id",
            new { passwordHash, now = SessionService.Format(time.GetUtcNow()), user.Id },
            transaction);
        await ClearTotpAsync(connection, transaction, user.Id);
        await connection.ExecuteAsync("DELETE FROM sessions WHERE user_id = @Id", new { user.Id }, transaction);
        await SetSettingAsync(connection, transaction, RecoveryKeySetting, newKeyHash);
        await transaction.CommitAsync(cancellationToken);
        return (user.Id, newKey);
    }

    /// <summary>A new recovery key such as <c>PTHX-7KQ2-...</c>; the prefix only marks it and is not part of the secret.</summary>
    internal static string NewRecoveryKey() => $"{RecoveryKeyPrefix}-{Secrets.NewCode(RecoveryKeyCharacters, 4)}";

    internal static string NormalizeRecoveryKey(string key) => Secrets.NormalizeCode(key, RecoveryKeyPrefix, RecoveryKeyCharacters);

    private async Task<bool> TryUseTotpCodeAsync(UserRow user, string code, CancellationToken cancellationToken)
    {
        if (user.TotpSecret is null)
        {
            return false;
        }
        var totp = new Totp(Base32Encoding.ToBytes(user.TotpSecret));
        if (!totp.VerifyTotp(time.GetUtcNow().UtcDateTime, code.Trim(), out var step, new VerificationWindow(previous: 1, future: 1)))
        {
            return false;
        }
        // A code is valid for one login only: the matched time step must be newer than the last one used.
        await using var connection = await connections.OpenAsync(cancellationToken);
        var updated = await connection.ExecuteAsync(
            "UPDATE users SET totp_last_used_step = @step WHERE id = @Id AND (totp_last_used_step IS NULL OR totp_last_used_step < @step)",
            new { step, user.Id });
        return updated == 1;
    }

    private async Task<bool> TryUseRecoveryCodeAsync(long userId, string code, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var updated = await connection.ExecuteAsync(
            "UPDATE recovery_codes SET used_at = @now WHERE user_id = @userId AND code_hash = @hash AND used_at IS NULL",
            new { now = SessionService.Format(time.GetUtcNow()), userId, hash = Secrets.Sha256(Secrets.NormalizeCode(code)) });
        return updated == 1;
    }

    private async Task SetPasswordAsync(long userId, string password, CancellationToken cancellationToken)
    {
        var passwordHash = await hasher.HashAsync(password, cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE users SET password_hash = @passwordHash, updated_at = @now WHERE id = @userId",
            new { passwordHash, now = SessionService.Format(time.GetUtcNow()), userId });
    }

    private async Task<UserRow?> FindByUsernameAsync(string input, CancellationToken cancellationToken)
    {
        string username;
        try
        {
            username = CredentialRules.NormalizeUsername(input);
        }
        catch (PithyxException)
        {
            return null;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<UserRow>(UserRow.Select + " WHERE username = @username", new { username });
    }

    private async Task<UserRow?> FindByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<UserRow>(UserRow.Select + " WHERE id = @id", new { id });
    }

    private static Task<int> ClearTotpAsync(SqliteConnection connection, IDbTransaction transaction, long userId) =>
        connection.ExecuteAsync(
            """
            UPDATE users SET totp_secret = NULL, totp_enabled_at = NULL, totp_last_used_step = NULL WHERE id = @userId;
            DELETE FROM recovery_codes WHERE user_id = @userId;
            """,
            new { userId },
            transaction);

    private static Task<int> SetSettingAsync(SqliteConnection connection, IDbTransaction transaction, string key, string value) =>
        connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { key, value },
            transaction);

    private sealed record UserRow(
        long Id,
        string Username,
        string? DisplayName,
        string PasswordHash,
        string? TotpSecret,
        string? TotpEnabledAt,
        string? DisabledAt)
    {
        public const string Select =
            "SELECT id AS Id, username AS Username, display_name AS DisplayName, password_hash AS PasswordHash, totp_secret AS TotpSecret, totp_enabled_at AS TotpEnabledAt, disabled_at AS DisabledAt FROM users";
    }
}
