using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// The SQL both log databases share: the current logs on the system disk (<see cref="LogStore"/>) and the archive on a
/// pool (<see cref="LogArchive"/>). Each is its own SQLite file, never the daemon database, so logs never grow the
/// config backup. Times are microseconds since the Unix epoch, levels 0 (debug) to 3 (error).
/// </summary>
public static class LogDatabase
{
    public const int SchemaVersion = 1;

    public static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = 5,
    }.ToString();

    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Creates the schema of a new file. Incremental auto-vacuum lets pruning hand space back to the file system, so
    /// the file follows its size limit instead of keeping its largest size.
    /// </summary>
    public static async Task EnsureSchemaAsync(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (await connection.ExecuteScalarAsync<long>("PRAGMA user_version") >= SchemaVersion)
        {
            await connection.ExecuteAsync("PRAGMA journal_mode = WAL; PRAGMA journal_size_limit = 16777216;");
            return;
        }
        await connection.ExecuteAsync(
            """
            PRAGMA auto_vacuum = INCREMENTAL;
            PRAGMA journal_mode = WAL;
            PRAGMA journal_size_limit = 16777216;
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY,
                time INTEGER NOT NULL,
                level INTEGER NOT NULL,
                source TEXT NOT NULL,
                message TEXT NOT NULL,
                request_id TEXT,
                user TEXT,
                data TEXT
            );
            CREATE INDEX IF NOT EXISTS entries_time ON entries (time);
            CREATE INDEX IF NOT EXISTS entries_source_time ON entries (source, time);
            CREATE TABLE IF NOT EXISTS sources (source TEXT PRIMARY KEY, last_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            PRAGMA user_version = 1;
            """);
    }

    public static long Micros(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    public static DateTimeOffset Time(long micros) => DateTimeOffset.UnixEpoch.AddTicks(micros * 10);

    public static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<PendingLogEntry> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO entries (time, level, source, message, request_id, user, data) VALUES ($time, $level, $source, $message, $request, $user, $data)";
        var time = command.Parameters.Add("$time", SqliteType.Integer);
        var level = command.Parameters.Add("$level", SqliteType.Integer);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var request = command.Parameters.Add("$request", SqliteType.Text);
        var user = command.Parameters.Add("$user", SqliteType.Text);
        var data = command.Parameters.Add("$data", SqliteType.Text);
        foreach (var entry in batch)
        {
            time.Value = Micros(entry.Time);
            level.Value = (int)entry.Level;
            source.Value = entry.Source;
            message.Value = entry.Message;
            request.Value = (object?)entry.RequestId ?? DBNull.Value;
            user.Value = (object?)entry.User ?? DBNull.Value;
            data.Value = entry.Data is { Count: > 0 } fields ? JsonSerializer.Serialize(fields) : DBNull.Value;
            await command.ExecuteNonQueryAsync();
        }
        foreach (var group in batch.GroupBy(entry => entry.Source, StringComparer.Ordinal))
        {
            await connection.ExecuteAsync(
                "INSERT INTO sources (source, last_seen) VALUES (@source, @seen) ON CONFLICT (source) DO UPDATE SET last_seen = max(last_seen, excluded.last_seen)",
                new { source = group.Key, seen = group.Max(entry => Micros(entry.Time)) },
                transaction);
        }
    }

    /// <summary>
    /// Entries matching the query, newest first. <paramref name="olderThan"/> restricts the archive to what the current
    /// logs no longer hold, so no entry appears twice.
    /// </summary>
    public static async Task<IReadOnlyList<LogEntry>> QueryAsync(SqliteConnection connection, LogQuery query, long? olderThan, bool archived)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sql = new StringBuilder("SELECT id, time, level, source, message, request_id AS requestId, user, data FROM entries WHERE 1 = 1");
        var parameters = new DynamicParameters();
        if (query.From is { } from)
        {
            sql.Append(" AND time >= @from");
            parameters.Add("from", Micros(from));
        }
        if (query.To is { } to)
        {
            sql.Append(" AND time <= @to");
            parameters.Add("to", Micros(to));
        }
        if (olderThan is { } boundary)
        {
            sql.Append(" AND time < @boundary");
            parameters.Add("boundary", boundary);
        }
        if (query.Before is { } cursor)
        {
            sql.Append(" AND (time < @cursorTime OR (time = @cursorTime AND id < @cursorId))");
            parameters.Add("cursorTime", cursor.TimeMicros);
            parameters.Add("cursorId", cursor.Id);
        }
        if (query.Levels is { Count: > 0 } levels)
        {
            sql.Append(" AND level IN @levels");
            parameters.Add("levels", levels.Select(level => (int)level).ToArray());
        }
        if (query.Sources is { Count: > 0 } sources)
        {
            sql.Append(" AND source IN @sources");
            parameters.Add("sources", sources.ToArray());
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            sql.Append(@" AND (message LIKE @search ESCAPE '\' OR data LIKE @search ESCAPE '\' OR request_id = @exact)");
            parameters.Add("search", "%" + EscapeLike(query.Search.Trim()) + "%");
            parameters.Add("exact", query.Search.Trim());
        }
        sql.Append(" ORDER BY time DESC, id DESC LIMIT @limit");
        parameters.Add("limit", query.Limit);
        var rows = await connection.QueryAsync<Row>(sql.ToString(), parameters);
        return [.. rows.Select(row => new LogEntry(
            row.Id,
            Time(row.Time),
            (LogLevelName)row.Level,
            row.Source,
            row.Message,
            row.RequestId,
            row.User,
            row.Data is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(row.Data),
            archived))];
    }

    public static async Task<IReadOnlyList<string>> SourcesAsync(SqliteConnection connection) =>
        [.. await connection.QueryAsync<string>("SELECT source FROM sources ORDER BY source")];

    public static async Task<(long Bytes, DateTimeOffset? Oldest)> StatusAsync(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var oldest = await connection.ExecuteScalarAsync<long?>("SELECT min(time) FROM entries");
        return (await UsedBytesAsync(connection), oldest is { } micros ? Time(micros) : null);
    }

    /// <summary>Pages in use, without the free pages an incremental vacuum has not handed back yet.</summary>
    public static async Task<long> UsedBytesAsync(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var pages = await connection.ExecuteScalarAsync<long>("PRAGMA page_count");
        var free = await connection.ExecuteScalarAsync<long>("PRAGMA freelist_count");
        var size = await connection.ExecuteScalarAsync<long>("PRAGMA page_size");
        return (pages - free) * size;
    }

    /// <summary>
    /// Drops entries older than <paramref name="notBefore"/>, then the oldest ones until the file is below 90 percent of
    /// <paramref name="maxBytes"/>, and gives the space back. Returns how many entries went.
    /// </summary>
    public static async Task<int> PruneAsync(SqliteConnection connection, DateTimeOffset notBefore, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var removed = await connection.ExecuteAsync("DELETE FROM entries WHERE time < @limit", new { limit = Micros(notBefore) });
        for (var round = 0; round < 8; round++)
        {
            var used = await UsedBytesAsync(connection);
            if (used <= maxBytes)
            {
                break;
            }
            var count = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM entries");
            if (count == 0)
            {
                break;
            }
            var excess = (double)(used - (maxBytes * 9 / 10)) / used;
            var take = Math.Max(1, (long)Math.Ceiling(count * excess));
            removed += await connection.ExecuteAsync("DELETE FROM entries WHERE id IN (SELECT id FROM entries ORDER BY time LIMIT @take)", new { take });
            await connection.ExecuteAsync("PRAGMA incremental_vacuum");
        }
        if (removed > 0)
        {
            await connection.ExecuteAsync("DELETE FROM sources WHERE last_seen < coalesce((SELECT min(time) FROM entries), 9223372036854775807)");
            await connection.ExecuteAsync("PRAGMA incremental_vacuum");
        }
        return removed;
    }

    public static Task<string?> MetaAsync(SqliteConnection connection, string key, SqliteTransaction? transaction = null) =>
        connection.ExecuteScalarAsync<string?>("SELECT value FROM meta WHERE key = @key", new { key }, transaction);

    public static Task SetMetaAsync(SqliteConnection connection, string key, string value, SqliteTransaction? transaction = null) =>
        connection.ExecuteAsync("INSERT INTO meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value", new { key, value }, transaction);

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    private sealed record Row(long Id, long Time, long Level, string Source, string Message, string? RequestId, string? User, string? Data);
}
