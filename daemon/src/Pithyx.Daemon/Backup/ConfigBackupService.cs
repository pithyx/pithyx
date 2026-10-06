using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.SystemSettings;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Backup;

/// <summary>
/// The configuration backup (D293): everything needed to rebuild the system disk, in one file encrypted with a password
/// the administrator chooses. It holds <c>pithyx.db</c> without runtime state, the Linux and Samba password hashes of
/// the Pithyx users, the time zone and time servers, the daemon's certificates and the SSH host keys. A restore checks
/// the file first, then replaces the chosen categories, brings the accounts, passwords and keys in line and restarts
/// the daemon, which reconciles everything else from SQLite on start.
/// </summary>
public sealed partial class ConfigBackupService(
    SqliteConnectionFactory connections,
    PasswordHasher hasher,
    LinuxAccounts accounts,
    FileServicePasswords passwords,
    SystemSettingsService system,
    ICommandRunner commands,
    DaemonRestart restart,
    BackupOptions options,
    TimeProvider time,
    ILoggerFactory loggers) : IDisposable
{
    public const string ManifestEntry = "manifest.json";
    public const string DatabaseEntry = "pithyx.db";
    public const string ShadowEntry = "users/shadow";
    public const string PassdbEntry = "users/smbpasswd";
    public const string TimeEntry = "system/time.json";
    public const string TlsPrefix = "certificates/tls/";
    public const string SshPrefix = "certificates/ssh/";

    /// <summary>How long a checked backup waits for its restore.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILogger _logger = loggers.CreateLogger<ConfigBackupService>();
    private readonly SemaphoreSlim _restoring = new(1, 1);
    private readonly Lock _pendingLock = new();
    private Pending? _pending;

    public void Dispose()
    {
        _restoring.Dispose();
        Discard();
    }

    /// <summary>Creates an encrypted backup of the configuration; returns the file and its suggested name.</summary>
    public async Task<(byte[] Archive, string FileName)> CreateAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        CredentialRules.ValidateNewPassword(password);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var copy = Path.Combine(PrepareWorkDirectory(), $"backup-{Secrets.NewToken(12)}.db");
        int schemaVersion;
        IReadOnlySet<string> usernames;
        try
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await BackupDatabase.CopyAsync(connection, copy, cancellationToken);
            }
            await using (var backup = BackupDatabase.Open(copy))
            {
                await backup.OpenAsync(cancellationToken);
                schemaVersion = await backup.ExecuteScalarAsync<int>("SELECT coalesce(max(version), 0) FROM schema_version");
                usernames = (await backup.QueryAsync<string>("SELECT username FROM users")).ToHashSet(StringComparer.Ordinal);
            }
            entries[DatabaseEntry] = await File.ReadAllBytesAsync(copy, cancellationToken);
        }
        finally
        {
            File.Delete(copy);
        }

        if (options.Apply)
        {
            foreach (var (name, content) in await ReadPasswordsAsync(commands, options, usernames, cancellationToken))
            {
                entries[name] = content;
            }
            foreach (var hostKey in SshHostKeys())
            {
                entries[SshPrefix + Path.GetFileName(hostKey)] = await File.ReadAllBytesAsync(hostKey, cancellationToken);
            }
        }
        if (Directory.Exists(options.TlsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(options.TlsDirectory).Where(file => IsEntryName(Path.GetFileName(file))))
            {
                entries[TlsPrefix + Path.GetFileName(file)] = await File.ReadAllBytesAsync(file, cancellationToken);
            }
        }
        var current = system.Time();
        entries[TimeEntry] = JsonSerializer.SerializeToUtf8Bytes(new TimeSettingsRequest(current.TimeZone, current.NtpServers), _json);

        var createdAt = time.GetUtcNow();
        var hostName = Environment.MachineName;
        BackupCategory[] categories = entries.Keys.Any(name => name.StartsWith(TlsPrefix, StringComparison.Ordinal) || name.StartsWith(SshPrefix, StringComparison.Ordinal))
            ? [BackupCategory.Users, BackupCategory.SharedFolders, BackupCategory.Storage, BackupCategory.System, BackupCategory.Certificates, BackupCategory.Apps]
            : [BackupCategory.Users, BackupCategory.SharedFolders, BackupCategory.Storage, BackupCategory.System, BackupCategory.Apps];
        var manifest = new BackupManifest(1, createdAt, hostName, BuildInfo.Current.Version, schemaVersion, categories);
        entries[ManifestEntry] = JsonSerializer.SerializeToUtf8Bytes(manifest, _json);

        var salt = RandomNumberGenerator.GetBytes(BackupArchive.SaltBytes);
        var key = await hasher.DeriveKeyAsync(password, salt, PasswordHasher.MemoryKib, PasswordHasher.Passes, cancellationToken);
        try
        {
            var archive = BackupArchive.Seal(BackupArchive.Pack(entries), key, salt, PasswordHasher.MemoryKib, PasswordHasher.Passes);
            LogCreated(archive.Length, categories);
            return (archive, FileName(hostName, createdAt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Decrypts and checks a backup, brings its database to the current schema and keeps it for a restore by the same
    /// user. A newer check replaces the one before.
    /// </summary>
    public async Task<BackupSummary> InspectAsync(long userId, InspectBackupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var archive = Decode(request.Archive);
        var envelope = BackupArchive.Parse(archive) ?? throw new PithyxException(ErrorCodes.SystemBackupInvalid);
        var key = await hasher.DeriveKeyAsync(request.Password ?? "", envelope.Salt, envelope.MemoryKib, envelope.Passes, cancellationToken);
        byte[]? packed;
        try
        {
            packed = BackupArchive.Open(envelope, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        if (packed is null)
        {
            throw new PithyxException(ErrorCodes.SystemBackupPasswordWrong);
        }
        IReadOnlyDictionary<string, byte[]> entries;
        BackupManifest manifest;
        try
        {
            entries = BackupArchive.Unpack(packed);
            manifest = JsonSerializer.Deserialize<BackupManifest>(entries.GetValueOrDefault(ManifestEntry) ?? [], _json)
                ?? throw new InvalidDataException("The backup has no manifest.");
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new PithyxException(ErrorCodes.SystemBackupInvalid);
        }
        if (manifest.Format != 1 || !entries.TryGetValue(DatabaseEntry, out var database) || manifest.Categories is not { Count: > 0 })
        {
            throw new PithyxException(ErrorCodes.SystemBackupInvalid);
        }

        var id = Secrets.NewToken(18);
        var path = Path.Combine(PrepareWorkDirectory(), $"restore-{id}.db");
        (int Users, int Groups, int SharedFolders) counts;
        try
        {
            await File.WriteAllBytesAsync(path, database, cancellationToken);
            Restrict(path);
            var migrations = new MigrationRunner(new SqliteConnectionFactory(path), time, loggers.CreateLogger<MigrationRunner>());
            await migrations.RunAsync(Migration.Embedded(), cancellationToken);
            // The runner's pooled connections would keep the file open after it is deleted.
            SqliteConnection.ClearAllPools();
            await using var backup = BackupDatabase.Open(path);
            await backup.OpenAsync(cancellationToken);
            counts = await BackupDatabase.CountAsync(backup, cancellationToken);
        }
        catch (DatabaseNewerThanDaemonException)
        {
            DeleteDatabase(path);
            throw new PithyxException(ErrorCodes.SystemBackupTooNew, new Dictionary<string, object?> { ["version"] = manifest.Version });
        }
        catch (SqliteException)
        {
            DeleteDatabase(path);
            throw new PithyxException(ErrorCodes.SystemBackupInvalid);
        }

        var files = entries.Where(entry => entry.Key is not (DatabaseEntry or ManifestEntry)).ToDictionary(StringComparer.Ordinal);
        lock (_pendingLock)
        {
            Discard();
            _pending = new Pending(id, userId, time.GetUtcNow() + PendingLifetime, path, files, manifest);
        }
        return new BackupSummary(id, manifest.CreatedAt, manifest.HostName, manifest.Version, manifest.Categories, counts.Users, counts.Groups, counts.SharedFolders);
    }

    /// <summary>Restores the chosen categories of a checked backup, then restarts the daemon.</summary>
    public async Task<RestoreResult> RestoreAsync(long userId, RestoreBackupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Categories is not { Count: > 0 })
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        if (!await _restoring.WaitAsync(0, cancellationToken))
        {
            throw new PithyxException(ErrorCodes.SystemBackupRestoreRunning);
        }
        try
        {
            Pending pending;
            lock (_pendingLock)
            {
                if (_pending is not { } current || current.Id != request.RestoreId || current.UserId != userId || current.ExpiresAt < time.GetUtcNow())
                {
                    throw new PithyxException(ErrorCodes.SystemBackupRestoreExpired);
                }
                pending = current;
            }
            var categories = request.Categories.ToHashSet();
            foreach (var category in categories.Where(category => !pending.Manifest.Categories.Contains(category)))
            {
                throw new PithyxException(ErrorCodes.SystemBackupCategoryMissing, new Dictionary<string, object?> { ["category"] = JsonNamingPolicy.CamelCase.ConvertName(category.ToString()) });
            }

            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                var copy = $"{connections.DatabasePath}.pre-restore-{time.GetUtcNow():yyyyMMddTHHmmssZ}.bak";
                await connection.ExecuteAsync("VACUUM INTO @copy", new { copy });
                LogSaved(copy);
                try
                {
                    await BackupDatabase.RestoreAsync(connection, pending.DatabasePath, categories, cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidOperationException or SqliteException)
                {
                    LogRestoreFailed(exception);
                    throw new PithyxException(ErrorCodes.SystemBackupRestoreFailed);
                }
            }

            // The database changed: every step runs, and the daemon restarts whatever a step does.
            var problems = new List<RestoreProblem>();
            try
            {
                if (categories.Contains(BackupCategory.Users))
                {
                    await RestoreAccountsAsync(pending.Files, problems, cancellationToken);
                }
                if (categories.Contains(BackupCategory.System) && pending.Files.TryGetValue(TimeEntry, out var timeJson))
                {
                    await RestoreTimeAsync(timeJson, problems, cancellationToken);
                }
                if (categories.Contains(BackupCategory.Certificates))
                {
                    await RestoreCertificatesAsync(pending.Files, problems, cancellationToken);
                }
            }
            finally
            {
                lock (_pendingLock)
                {
                    Discard();
                }
                restart.Request();
            }
            var restored = categories.Order().ToList();
            LogRestored(restored, problems.Count);
            return new RestoreResult(restored, problems, restart.Requested);
        }
        finally
        {
            _restoring.Release();
        }
    }

    /// <summary>Linux accounts and groups from the restored users, then their Linux and Samba passwords.</summary>
    private async Task RestoreAccountsAsync(IReadOnlyDictionary<string, byte[]> files, List<RestoreProblem> problems, CancellationToken cancellationToken)
    {
        if (!options.Apply)
        {
            return;
        }
        try
        {
            if ((await accounts.ReconcileAsync(cancellationToken)).Count > 0)
            {
                problems.Add(new RestoreProblem(BackupCategory.Users, "accounts"));
            }
            var existing = accounts.Actual().Where(entry => AccountReconciler.IsManaged(entry.Uid)).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            problems.AddRange((await RestorePasswordsAsync(commands, passwords, options, files, existing, cancellationToken))
                .Select(step => new RestoreProblem(BackupCategory.Users, step)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStepFailed(exception, "accounts");
            problems.Add(new RestoreProblem(BackupCategory.Users, "accounts"));
        }
    }

    /// <summary>The Linux and Samba password hashes of the named accounts, as backup entries.</summary>
    internal static async Task<Dictionary<string, byte[]>> ReadPasswordsAsync(ICommandRunner commands, BackupOptions options, IReadOnlySet<string> usernames, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (options.ShadowPath is { } shadowPath && File.Exists(shadowPath))
        {
            var hashes = ShadowParser.Hashes(await File.ReadAllTextAsync(shadowPath, cancellationToken), usernames);
            entries[ShadowEntry] = Encoding.UTF8.GetBytes(string.Concat(hashes.Select(pair => $"{pair.Key}:{pair.Value}\n")));
        }
        var passdb = await commands.RunAsync(new CommandRequest("pdbedit", ["-L", "-w"]), cancellationToken);
        if (passdb.ExitCode == 0)
        {
            var lines = PassdbParser.Lines(passdb.StandardOutput, AccountReconciler.IsManaged)
                .Split('\n').Where(line => usernames.Contains(line.Split(':')[0]));
            entries[PassdbEntry] = Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")));
        }
        return entries;
    }

    /// <summary>
    /// Gives the existing accounts their hashes back: <c>chpasswd -e</c> for Linux, <c>pdbedit -i smbpasswd:</c> for
    /// Samba. Returns the steps that failed.
    /// </summary>
    internal static async Task<List<string>> RestorePasswordsAsync(
        ICommandRunner commands,
        FileServicePasswords passwords,
        BackupOptions options,
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlySet<string> existing,
        CancellationToken cancellationToken)
    {
        var failed = new List<string>();
        if (files.TryGetValue(ShadowEntry, out var shadow))
        {
            var lines = Encoding.UTF8.GetString(shadow).Split('\n').Where(line => line.Split(':') is [var name, _] && existing.Contains(name)).ToList();
            if (lines.Count > 0
                && (await commands.RunAsync(new CommandRequest("chpasswd", ["-e"], StandardInput: string.Concat(lines.Select(line => line + "\n"))), cancellationToken)).ExitCode != 0)
            {
                failed.Add("linuxPasswords");
            }
        }
        if (files.TryGetValue(PassdbEntry, out var passdb))
        {
            var lines = Encoding.UTF8.GetString(passdb).Split('\n').Where(line => line.Split(':') is { Length: >= 5 } fields && existing.Contains(fields[0])).ToList();
            if (lines.Count > 0 && !await ImportPassdbAsync(commands, passwords, options, lines, cancellationToken))
            {
                failed.Add("sambaPasswords");
            }
            await passwords.RefreshAsync(cancellationToken);
        }
        return failed;
    }

    // pdbedit imports from a file only; it lives in the work directory (tmpfs) for the moment of the import. Accounts it
    // already knows are removed first, because an import does not replace them.
    private static async Task<bool> ImportPassdbAsync(ICommandRunner commands, FileServicePasswords passwords, BackupOptions options, IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        await passwords.RefreshAsync(cancellationToken);
        foreach (var name in lines.Select(line => line.Split(':')[0]).Where(passwords.Passdb.ContainsKey))
        {
            await commands.RunAsync(new CommandRequest("pdbedit", ["-x", "-u", name]), cancellationToken);
        }
        var file = Path.Combine(PrepareWorkDirectory(options), $"smbpasswd-{Secrets.NewToken(12)}");
        try
        {
            await File.WriteAllTextAsync(file, string.Concat(lines.Select(line => line + "\n")), cancellationToken);
            Restrict(file);
            return (await commands.RunAsync(new CommandRequest("pdbedit", ["-i", $"smbpasswd:{file}"]), cancellationToken)).ExitCode == 0;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private async Task RestoreTimeAsync(byte[] json, List<RestoreProblem> problems, CancellationToken cancellationToken)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<TimeSettingsRequest>(json, _json);
            if (settings is not null)
            {
                await system.SetTimeAsync(settings, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStepFailed(exception, "time");
            problems.Add(new RestoreProblem(BackupCategory.System, "time"));
        }
    }

    private async Task RestoreCertificatesAsync(IReadOnlyDictionary<string, byte[]> files, List<RestoreProblem> problems, CancellationToken cancellationToken)
    {
        var tls = Entries(files, TlsPrefix).ToList();
        if (tls.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(options.TlsDirectory);
                foreach (var (name, content) in tls)
                {
                    await WriteAsync(Path.Combine(options.TlsDirectory, name), content, secret: true, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogStepFailed(exception, "tls");
                problems.Add(new RestoreProblem(BackupCategory.Certificates, "tls"));
            }
        }
        var ssh = Entries(files, SshPrefix).Where(entry => IsSshHostKey(entry.Name)).ToList();
        if (!options.Apply || options.SshDirectory is not { } directory || ssh.Count == 0)
        {
            return;
        }
        try
        {
            foreach (var (name, content) in ssh)
            {
                await WriteAsync(Path.Combine(directory, name), content, secret: !name.EndsWith(".pub", StringComparison.Ordinal), cancellationToken);
            }
            // sshd reads its host keys when it starts; a reload re-executes it.
            if ((await commands.RunAsync(new CommandRequest("systemctl", ["try-reload-or-restart", FileServiceSync.SshUnit]), cancellationToken)).ExitCode != 0)
            {
                problems.Add(new RestoreProblem(BackupCategory.Certificates, "sshKeys"));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStepFailed(exception, "sshKeys");
            problems.Add(new RestoreProblem(BackupCategory.Certificates, "sshKeys"));
        }
    }

    private IEnumerable<string> SshHostKeys() =>
        options.SshDirectory is { } directory && Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "ssh_host_*").Where(path => IsSshHostKey(Path.GetFileName(path))).Order(StringComparer.Ordinal)
            : [];

    private static IEnumerable<(string Name, byte[] Content)> Entries(IReadOnlyDictionary<string, byte[]> files, string prefix) =>
        files.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) && IsEntryName(entry.Key[prefix.Length..]))
            .Select(entry => (entry.Key[prefix.Length..], entry.Value));

    /// <summary>A file name without folders, as the archive and the target folders take it.</summary>
    private static bool IsEntryName(string name) =>
        name.Length is > 0 and <= 100 && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary><c>ssh_host_&lt;type&gt;_key</c> and its <c>.pub</c>; certificates and other files stay where they are.</summary>
    public static bool IsSshHostKey(string name) =>
        IsEntryName(name) && name.StartsWith("ssh_host_", StringComparison.Ordinal) && (name.EndsWith("_key", StringComparison.Ordinal) || name.EndsWith("_key.pub", StringComparison.Ordinal));

    /// <summary>The suggested file name: the host name and the UTC time of the backup.</summary>
    public static string FileName(string hostName, DateTimeOffset createdAt)
    {
        var host = new string([.. (hostName ?? "").ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        return string.Create(CultureInfo.InvariantCulture, $"pithyx-config-{(host.Length == 0 ? "box" : host)}-{createdAt.UtcDateTime:yyyyMMdd-HHmm}.pithyxbackup");
    }

    private static byte[] Decode(string? archive)
    {
        // Base64 grows by a third; anything longer cannot decode to an archive the daemon reads.
        if (archive is null || archive.Length > (BackupArchive.MaxArchiveBytes / 3 * 4) + 4)
        {
            throw new PithyxException(ErrorCodes.SystemBackupTooLarge, new Dictionary<string, object?> { ["maxMb"] = BackupArchive.MaxArchiveBytes / 1024 / 1024 });
        }
        try
        {
            return Convert.FromBase64String(archive);
        }
        catch (FormatException)
        {
            throw new PithyxException(ErrorCodes.SystemBackupInvalid);
        }
    }

    private static async Task WriteAsync(string path, byte[] content, bool secret, CancellationToken cancellationToken)
    {
        // Written beside the target and moved over it, so a reader never sees half a key.
        var temporary = $"{path}.pithyx-restore";
        await File.WriteAllBytesAsync(temporary, content, cancellationToken);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(temporary, secret
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private string PrepareWorkDirectory() => PrepareWorkDirectory(options);

    private static string PrepareWorkDirectory(BackupOptions options)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(options.WorkDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(options.WorkDirectory);
        }
        return options.WorkDirectory;
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // The checked backup and the copies the migration runner made of it.
    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(path) + "*"))
        {
            File.Delete(file);
        }
    }

    // Callers hold _pendingLock.
    private void Discard()
    {
        if (_pending is { } pending)
        {
            DeleteDatabase(pending.DatabasePath);
            _pending = null;
        }
    }

    private sealed record Pending(string Id, long UserId, DateTimeOffset ExpiresAt, string DatabasePath, IReadOnlyDictionary<string, byte[]> Files, BackupManifest Manifest);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration backup created ({Bytes} bytes, {Categories})")]
    private partial void LogCreated(int bytes, IReadOnlyList<BackupCategory> categories);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database copied to {Copy} before the restore")]
    private partial void LogSaved(string copy);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring the database from the backup failed; nothing was changed")]
    private partial void LogRestoreFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restoring {Step} from the backup failed")]
    private partial void LogStepFailed(Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration restored ({Categories}, {Problems} problems); the daemon restarts")]
    private partial void LogRestored(IReadOnlyList<BackupCategory> categories, int problems);
}
