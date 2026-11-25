using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pithyx.Daemon.Audit;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Backups;

/// <summary>
/// The backup targets (#393, D414): servers the box reaches over SFTP, each with its own SSH key the box makes and the
/// host key the administrator confirmed. A target is saved only with a host key; a later connection accepts no other,
/// and a changed key is confirmed again by an administrator (<see cref="SetHostKeyAsync"/>). No password is ever stored.
/// </summary>
public sealed partial class BackupTargetService(
    SqliteConnectionFactory connections,
    BackupSsh ssh,
    AuditLog audit,
    TimeProvider time,
    ILogger<BackupTargetService> logger)
{
    public const string TargetCreated = "backup.target.created";
    public const string TargetDeleted = "backup.target.deleted";
    public const string HostKeyConfirmed = "backup.target.host_key_confirmed";

    private const string SelectColumns =
        """
        SELECT id AS Id, name AS Name, kind AS Kind, host AS Host, port AS Port, user_name AS UserName, path AS Path,
            host_key AS HostKey, ssh_private_key AS PrivateKey, ssh_public_key AS PublicKey, created_at AS CreatedAt,
            tested_at AS TestedAt, test_error_code AS TestErrorCode, test_error_params AS TestErrorParams,
            space_total_bytes AS SpaceTotalBytes, space_free_bytes AS SpaceFreeBytes, space_at AS SpaceAt, purpose AS Purpose
        FROM backup_targets
        """;

    public async Task<BackupTargetList> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<TargetRow>(new CommandDefinition(SelectColumns + " ORDER BY name COLLATE NOCASE", cancellationToken: cancellationToken));
        return new BackupTargetList([.. rows.Select(View)]);
    }

    public async Task<BackupTarget> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return View(await RowAsync(connection, id, cancellationToken));
    }

    /// <summary>The host keys the server offers, for the administrator to compare before the target is saved.</summary>
    public async Task<BackupHostKeyScan> ScanAsync(BackupHostKeyScanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = BackupTargetRules.CheckHost(request.Host);
        var port = BackupTargetRules.CheckPort(request.Port);
        return new BackupHostKeyScan(await ssh.ScanAsync(host, port, cancellationToken));
    }

    /// <summary>Saves a new target with the confirmed host key and makes its SSH key.</summary>
    public async Task<BackupTarget> CreateAsync(ClaimsPrincipal caller, BackupTargetRequest request, CancellationToken cancellationToken)
    {
        var target = BackupTargetRules.Check(request);
        if (target.HostKey is null)
        {
            throw BackupTargetRules.Invalid("hostKey");
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureNameFreeAsync(connection, target.Name, null, cancellationToken);
        var (privateKey, publicKey) = await ssh.KeygenAsync($"pithyx-backup@{Environment.MachineName}", cancellationToken);
        var now = SessionService.Format(time.GetUtcNow());
        long id;
        try
        {
            id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO backup_targets (name, kind, host, port, user_name, path, host_key, ssh_private_key, ssh_public_key, created_at, updated_at, purpose)
                VALUES (@Name, 'sftp', @Host, @Port, @User, @Path, @hostKey, @privateKey, @publicKey, @now, @now, @purpose)
                RETURNING id
                """,
                new { target.Name, target.Host, target.Port, target.User, target.Path, hostKey = target.HostKey.Key, privateKey, publicKey, now, purpose = Text(target.Purpose) },
                cancellationToken: cancellationToken));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw NameTaken(target.Name);
        }
        await audit.RecordAsync(caller, TargetCreated, target.Name, new Dictionary<string, string>
        {
            ["host"] = target.Host,
            ["port"] = BackupTargetRules.Text(target.Port),
            ["hostKey"] = target.HostKey.Fingerprint,
            ["purpose"] = Text(target.Purpose),
        }, cancellationToken);
        LogCreated(id, target.Host, target.Port, target.HostKey.Fingerprint);
        return View(await RowAsync(connection, id, cancellationToken));
    }

    /// <summary>
    /// Changes a target. A new host or port needs the new server's confirmed host key; the target keeps its SSH key. A
    /// change of where it points forgets the last test.
    /// </summary>
    public async Task<BackupTarget> UpdateAsync(ClaimsPrincipal caller, long id, BackupTargetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, id, cancellationToken);
        var target = BackupTargetRules.Check(request with { Purpose = Purpose(row) });
        var moved = row.Host != target.Host || row.Port != target.Port;
        if (moved && target.HostKey is null)
        {
            throw BackupTargetRules.Invalid("hostKey");
        }
        await EnsureNameFreeAsync(connection, target.Name, id, cancellationToken);
        var hostKey = target.HostKey?.Key ?? row.HostKey;
        var retest = moved || row.UserName != target.User || row.Path != target.Path || hostKey != row.HostKey;
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE backup_targets SET name = @Name, host = @Host, port = @Port, user_name = @User, path = @Path, host_key = @hostKey, updated_at = @now,
                    tested_at = CASE WHEN @retest THEN NULL ELSE tested_at END,
                    test_error_code = CASE WHEN @retest THEN NULL ELSE test_error_code END,
                    test_error_params = CASE WHEN @retest THEN NULL ELSE test_error_params END
                WHERE id = @id
                """,
                new { id, target.Name, target.Host, target.Port, target.User, target.Path, hostKey, retest, now = SessionService.Format(time.GetUtcNow()) },
                cancellationToken: cancellationToken));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw NameTaken(target.Name);
        }
        if (hostKey != row.HostKey)
        {
            await RecordHostKeyAsync(caller, target.Name, row.HostKey, target.HostKey!, cancellationToken);
        }
        return View(await RowAsync(connection, id, cancellationToken));
    }

    /// <summary>Pins a new host key after the administrator compared its fingerprint, for example after the server was reinstalled.</summary>
    public async Task<BackupTarget> SetHostKeyAsync(ClaimsPrincipal caller, long id, BackupHostKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hostKey = SshHostKeys.Parse(request.HostKey) ?? throw BackupTargetRules.Invalid("hostKey");
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, id, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE backup_targets SET host_key = @key, updated_at = @now, tested_at = NULL, test_error_code = NULL, test_error_params = NULL WHERE id = @id",
            new { id, key = hostKey.Key, now = SessionService.Format(time.GetUtcNow()) },
            cancellationToken: cancellationToken));
        if (hostKey.Key != row.HostKey)
        {
            await RecordHostKeyAsync(caller, row.Name, row.HostKey, hostKey, cancellationToken);
        }
        return View(await RowAsync(connection, id, cancellationToken));
    }

    /// <summary>Connects to the target as a backup would and keeps the outcome with it.</summary>
    public async Task<BackupTarget> TestAsync(long id, CancellationToken cancellationToken)
    {
        TargetRow row;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            row = await RowAsync(connection, id, cancellationToken);
        }
        var (error, space) = Purpose(row) == BackupTargetPurpose.Server
            ? new SftpProbe(await ssh.ProbeServerAsync(Endpoint(row), cancellationToken), null)
            : await ssh.ProbeAsync(Endpoint(row), row.Path, cancellationToken);
        if (error is null)
        {
            LogTestSucceeded(id);
        }
        else
        {
            LogTestFailed(id, error.Code, BackupSsh.Params(error));
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE backup_targets SET tested_at = @now, test_error_code = @code, test_error_params = @parameters WHERE id = @id",
                new { id, now = SessionService.Format(time.GetUtcNow()), code = error?.Code, parameters = error is null ? null : BackupSsh.Params(error) },
                cancellationToken: cancellationToken));
            if (space is not null)
            {
                await SaveSpaceAsync(connection, id, space, cancellationToken);
            }
            return View(await RowAsync(connection, id, cancellationToken));
        }
    }

    /// <summary>
    /// Asks the target's server for its space after a backup run and keeps it; a server that cannot be reached or does
    /// not report it keeps the last measurement.
    /// </summary>
    public async Task MeasureSpaceAsync(long id, CancellationToken cancellationToken)
    {
        TargetRow row;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            row = await RowAsync(connection, id, cancellationToken);
        }
        if (await ssh.SpaceAsync(Endpoint(row), row.Path, cancellationToken) is not { } space)
        {
            LogSpaceUnknown(id);
            return;
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await SaveSpaceAsync(connection, id, space, cancellationToken);
        }
    }

    private async Task SaveSpaceAsync(SqliteConnection connection, long id, SftpSpace space, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE backup_targets SET space_total_bytes = @TotalBytes, space_free_bytes = @FreeBytes, space_at = @now WHERE id = @id",
            new { id, space.TotalBytes, space.FreeBytes, now = SessionService.Format(time.GetUtcNow()) },
            cancellationToken: cancellationToken));

    /// <summary>Removes the target and its SSH key; the public key stays on the server until somebody removes it there.</summary>
    public async Task DeleteAsync(ClaimsPrincipal caller, long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, id, cancellationToken);
        var jobs = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM backup_jobs WHERE target_id = @id ORDER BY name COLLATE NOCASE", new { id }, cancellationToken: cancellationToken))).ToList();
        if (jobs.Count > 0)
        {
            throw new PithyxException(ErrorCodes.BackupTargetInUse, new Dictionary<string, object?> { ["jobs"] = string.Join(", ", jobs) });
        }
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM backup_targets WHERE id = @id", new { id }, cancellationToken: cancellationToken));
        await audit.RecordAsync(caller, TargetDeleted, row.Name, new Dictionary<string, string> { ["host"] = row.Host }, cancellationToken);
        LogDeleted(id);
    }

    /// <summary>
    /// The connection details of a target or server, keys included, for the backup runs. A target of the other purpose
    /// counts as missing.
    /// </summary>
    public async Task<(SshEndpoint Endpoint, string Path)> EndpointAsync(long id, CancellationToken cancellationToken, BackupTargetPurpose purpose = BackupTargetPurpose.Target)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, id, cancellationToken);
        if (Purpose(row) != purpose)
        {
            throw new PithyxException(ErrorCodes.BackupTargetNotFound, new Dictionary<string, object?> { ["id"] = id });
        }
        return (Endpoint(row), row.Path);
    }

    /// <summary>
    /// Keeps a connection failure of a backup run as the target's last test, so the Targets view shows it. Returns
    /// whether the target's host key changed since its last test, which administrators are told about once.
    /// </summary>
    public async Task<bool> RecordConnectionFailureAsync(long id, PithyxException error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var previous = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT test_error_code FROM backup_targets WHERE id = @id", new { id }, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE backup_targets SET tested_at = @now, test_error_code = @code, test_error_params = @parameters WHERE id = @id",
            new { id, now = SessionService.Format(time.GetUtcNow()), code = error.Code, parameters = BackupSsh.Params(error) },
            cancellationToken: cancellationToken));
        return error.Code == ErrorCodes.BackupTargetHostKeyChanged && previous != ErrorCodes.BackupTargetHostKeyChanged;
    }

    /// <summary>Keeps a run's successful connection as the target's last test.</summary>
    public async Task RecordConnectionAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE backup_targets SET tested_at = @now, test_error_code = NULL, test_error_params = NULL WHERE id = @id",
            new { id, now = SessionService.Format(time.GetUtcNow()) },
            cancellationToken: cancellationToken));
    }

    private static BackupTargetPurpose Purpose(TargetRow row) => row.Purpose == "server" ? BackupTargetPurpose.Server : BackupTargetPurpose.Target;

    private static string Text(BackupTargetPurpose purpose) => purpose == BackupTargetPurpose.Server ? "server" : "target";

    private static SshEndpoint Endpoint(TargetRow row) => new(row.Host, (int)row.Port, row.UserName, row.HostKey, row.PrivateKey);

    private async Task RecordHostKeyAsync(ClaimsPrincipal caller, string name, string previous, BackupHostKey current, CancellationToken cancellationToken)
    {
        await audit.RecordAsync(caller, HostKeyConfirmed, name, new Dictionary<string, string>
        {
            ["previous"] = SshHostKeys.Parse(previous)?.Fingerprint ?? "",
            ["hostKey"] = current.Fingerprint,
        }, cancellationToken);
    }

    private static async Task EnsureNameFreeAsync(SqliteConnection connection, string name, long? except, CancellationToken cancellationToken)
    {
        var taken = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM backup_targets WHERE name = @name COLLATE NOCASE AND id IS NOT @except",
            new { name, except },
            cancellationToken: cancellationToken));
        if (taken > 0)
        {
            throw NameTaken(name);
        }
    }

    private static PithyxException NameTaken(string name) =>
        new(ErrorCodes.BackupTargetNameTaken, new Dictionary<string, object?> { ["name"] = name });

    private static async Task<TargetRow> RowAsync(SqliteConnection connection, long id, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition(SelectColumns + " WHERE id = @id", new { id }, cancellationToken: cancellationToken))
        ?? throw new PithyxException(ErrorCodes.BackupTargetNotFound, new Dictionary<string, object?> { ["id"] = id });

    private static BackupTarget View(TargetRow row)
    {
        var hostKey = SshHostKeys.Parse(row.HostKey) ?? new BackupHostKey("", row.HostKey, "");
        var test = row.TestedAt is null
            ? null
            : new BackupTargetTest(
                SessionService.Parse(row.TestedAt),
                row.TestErrorCode is null,
                row.TestErrorCode,
                row.TestErrorParams is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(row.TestErrorParams));
        return new BackupTarget(
            row.Id,
            row.Name,
            BackupTargetKind.Sftp,
            row.Host,
            (int)row.Port,
            row.UserName,
            row.Path,
            hostKey,
            row.PublicKey,
            SessionService.Parse(row.CreatedAt),
            test,
            row is { SpaceTotalBytes: { } total, SpaceFreeBytes: { } free, SpaceAt: { } at } ? new BackupTargetSpace(total, free, SessionService.Parse(at)) : null,
            Purpose(row),
            Purpose(row) == BackupTargetPurpose.Server ? BackupTargetRules.AuthorizedKeys(row.Path, row.PublicKey) : null);
    }

    private sealed class TargetRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Host { get; init; } = "";
        public long Port { get; init; }
        public string UserName { get; init; } = "";
        public string Path { get; init; } = "";
        public string HostKey { get; init; } = "";
        public string PrivateKey { get; init; } = "";
        public string PublicKey { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string? TestedAt { get; init; }
        public string? TestErrorCode { get; init; }
        public string? TestErrorParams { get; init; }
        public long? SpaceTotalBytes { get; init; }
        public long? SpaceFreeBytes { get; init; }
        public string? SpaceAt { get; init; }
        public string Purpose { get; init; } = "target";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup target {TargetId} added for {Host} port {Port} with host key {Fingerprint}")]
    private partial void LogCreated(long targetId, string host, int port, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup target {TargetId} removed")]
    private partial void LogDeleted(long targetId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup target {TargetId} passed its connection test")]
    private partial void LogTestSucceeded(long targetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup target {TargetId} failed its connection test with {ErrorCode} {ErrorParams}")]
    private partial void LogTestFailed(long targetId, string errorCode, string errorParams);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup target {TargetId} did not report its space")]
    private partial void LogSpaceUnknown(long targetId);
}
