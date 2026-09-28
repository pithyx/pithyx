using System.Globalization;
using Dapper;

namespace Pithyx.Daemon.Database;

/// <summary>
/// Brings the database schema up to date at startup, before the daemon accepts requests. A database that already
/// holds data is copied with <c>VACUUM INTO</c> first. Each migration runs in its own transaction, so a failing
/// migration leaves the schema at the last good version and the daemon refuses to start.
/// </summary>
public sealed partial class MigrationRunner(SqliteConnectionFactory connections, TimeProvider time, ILogger<MigrationRunner> logger)
{
    public async Task RunAsync(IReadOnlyList<Migration> migrations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var directory = Path.GetDirectoryName(Path.GetFullPath(connections.DatabasePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL");
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            ) STRICT
            """);

        var applied = (await connection.QueryAsync<int>("SELECT version FROM schema_version")).ToHashSet();
        var known = migrations.Select(m => m.Version).ToHashSet();
        var unknown = applied.Where(version => !known.Contains(version)).Order().ToList();
        if (unknown.Count > 0)
        {
            LogNewerDatabase(connections.DatabasePath, unknown);
            throw new DatabaseNewerThanDaemonException(connections.DatabasePath, unknown);
        }

        var pending = migrations.Where(m => !applied.Contains(m.Version)).OrderBy(m => m.Version).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (applied.Count > 0)
        {
            var backup = $"{connections.DatabasePath}.pre-{pending[0].Version:D4}-{time.GetUtcNow():yyyyMMddTHHmmssZ}.bak";
            await connection.ExecuteAsync("VACUUM INTO @backup", new { backup });
            LogBackup(backup);
        }

        foreach (var migration in pending)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await connection.ExecuteAsync(migration.Sql, transaction: transaction);
                await connection.ExecuteAsync(
                    "INSERT INTO schema_version (version, name, applied_at) VALUES (@Version, @Name, @AppliedAt)",
                    new { migration.Version, migration.Name, AppliedAt = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) },
                    transaction);
                await transaction.CommitAsync(cancellationToken);
                LogApplied(migration.Version, migration.Name);
            }
            catch (Exception exception)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                LogFailed(exception, migration.Version, migration.Name);
                throw;
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "The database {DatabasePath} has schema versions {Versions} that this daemon does not know: a newer daemon wrote it. Start that daemon, or point PITHYX_DB at another database; the daemon does not start")]
    private partial void LogNewerDatabase(string databasePath, IReadOnlyList<int> versions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database copied to {Backup} before migrating")]
    private partial void LogBackup(string backup);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied database migration {Version} {Name}")]
    private partial void LogApplied(int version, string name);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration {Version} {Name} failed; the daemon does not start")]
    private partial void LogFailed(Exception exception, int version, string name);
}
