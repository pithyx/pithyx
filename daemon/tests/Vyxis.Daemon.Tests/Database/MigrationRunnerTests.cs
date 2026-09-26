using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Vyxis.Daemon.Database;

namespace Vyxis.Daemon.Tests.Database;

public sealed class MigrationRunnerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vyxis-db-").FullName;
    private readonly SqliteConnectionFactory _connections;
    private readonly MigrationRunner _runner;

    public MigrationRunnerTests()
    {
        _connections = new SqliteConnectionFactory(Path.Combine(_directory, "nested", "vyxis.db"));
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunAsync([first], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EmbeddedMigrationsAreNumberedWithoutGaps() =>
        Assert.Equal(Enumerable.Range(1, Migration.Embedded().Count), Migration.Embedded().Select(m => m.Version));

    private string[] BackupFiles() => Directory.GetFiles(Path.Combine(_directory, "nested"), "*.bak");
}
