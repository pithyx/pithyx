using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.Tests.Database;

public sealed class MigrationRunnerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-db-").FullName;
    private readonly SqliteConnectionFactory _connections;
    private readonly MigrationRunner _runner;

    public MigrationRunnerTests()
    {
        _connections = new SqliteConnectionFactory(Path.Combine(_directory, "nested", "pithyx.db"));
        _runner = new MigrationRunner(_connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task AppliesEmbeddedMigrationsToANewDatabase()
    {
        await _runner.RunAsync(Migration.Embedded(), TestContext.Current.CancellationToken);

        await using var connection = await _connections.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Migration.Embedded().Select(m => m.Version), await connection.QueryAsync<int>("SELECT version FROM schema_version ORDER BY version"));
        Assert.Equal("wal", await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master WHERE name = 'users'"));
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task SecondRunChangesNothing()
    {
        await _runner.RunAsync(Migration.Embedded(), TestContext.Current.CancellationToken);
        await _runner.RunAsync(Migration.Embedded(), TestContext.Current.CancellationToken);

        await using var connection = await _connections.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Migration.Embedded().Count, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_version"));
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task FailedMigrationIsRolledBackAfterABackup()
    {
        var first = new Migration(1, "first", "CREATE TABLE a (id INTEGER PRIMARY KEY);");
        await _runner.RunAsync([first], TestContext.Current.CancellationToken);
        var broken = new Migration(2, "broken", "CREATE TABLE b (id INTEGER PRIMARY KEY); INSERT INTO missing VALUES (1);");

        await Assert.ThrowsAsync<SqliteException>(() => _runner.RunAsync([first, broken], TestContext.Current.CancellationToken));

        await using var connection = await _connections.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1], await connection.QueryAsync<int>("SELECT version FROM schema_version"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master WHERE name = 'b'"));
        Assert.Contains(".pre-0002-", Assert.Single(BackupFiles()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesADatabaseFromANewerDaemon()
    {
        var first = new Migration(1, "first", "CREATE TABLE a (id INTEGER PRIMARY KEY);");
        var second = new Migration(2, "second", "CREATE TABLE b (id INTEGER PRIMARY KEY);");
        await _runner.RunAsync([first, second], TestContext.Current.CancellationToken);

        var refused = await Assert.ThrowsAsync<DatabaseNewerThanDaemonException>(() => _runner.RunAsync([first], TestContext.Current.CancellationToken));

        Assert.Equal([2], refused.Versions);
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task RestorePointsAreSwitchedOnForExistingSharedFoldersButKeepAnExistingDailySchedule()
    {
        var token = TestContext.Current.CancellationToken;
        var before = Migration.Embedded().Where(migration => migration.Version < 15).ToList();
        await _runner.RunAsync(before, token);
        await using (var connection = await _connections.OpenAsync(token))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO shares (name, path, dataset, created_at, updated_at) VALUES
                    ('Photos', '/tank/photos', 'tank/pithyx/shares/photos', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                    ('Media', '/tank/media', 'tank/pithyx/shares/media', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                    ('Folder', '/srv/folder', NULL, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                INSERT INTO snapshot_schedules (dataset, kind, enabled, keep, updated_at)
                    VALUES ('tank/pithyx/shares/media', 'daily', 0, 30, '2026-01-01T00:00:00Z');
                """);
        }

        await _runner.RunAsync(Migration.Embedded(), token);

        await using var migrated = await _connections.OpenAsync(token);
        Assert.Equal(
            [("tank/pithyx/shares/media", 0L, 30L), ("tank/pithyx/shares/photos", 1L, 7L)],
            await migrated.QueryAsync<(string, long, long)>("SELECT dataset, enabled, keep FROM snapshot_schedules WHERE kind = 'daily' ORDER BY dataset"));
    }

    // Parallel branches reserve their numbers ahead, so a gap can exist until the branch that owns it is merged.
    [Fact]
    public void EmbeddedMigrationsAreNumberedWithoutGaps() =>
        Assert.Equal(Enumerable.Range(1, Migration.Embedded().Count), Migration.Embedded().Select(m => m.Version));

    private string[] BackupFiles() => Directory.GetFiles(Path.Combine(_directory, "nested"), "*.bak");
}
