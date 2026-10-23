using Microsoft.Data.Sqlite;

namespace Pithyx.Daemon.Database;

/// <summary>Opens connections to the daemon database with foreign keys enforced.</summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
            // Seconds a write waits for another connection's write lock before failing (D370): long enough that a
            // busy box (a scrub, a slow system disk, several jobs) makes requests wait instead of answering 500.
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>A connection for synchronous callers such as permission checks of realtime events.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
