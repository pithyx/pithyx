using Dapper;
using Microsoft.Data.Sqlite;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// The optional archive of the logs on a pool, in <c>logs/archive.db</c> of the pool's system dataset
/// (<c>&lt;pool&gt;/pithyx/system</c>, mounted at <c>/&lt;pool&gt;/.pithyx/system</c>). Entries are copied from the current
/// logs once a day and before the current logs drop entries the archive does not have yet, so the pool's disks are
/// woken rarely. A pool that is missing, locked or read-only leaves the entries on the system disk until it is back.
/// </summary>
public sealed class LogArchive(PoolInventory pools, LogStore store, TimeProvider time)
{
    public const string RelativePath = "logs/archive.db";
    public static readonly TimeSpan SyncInterval = TimeSpan.FromDays(1);

    private const string OriginKey = "origin";
    private const string CopiedKey = "copied_until";
    private const string SyncedKey = "synced_at";
    private const int CopyBatch = 50_000;

    /// <summary>Pools whose system dataset can hold the archive, by name.</summary>
    public static IReadOnlyList<string> Candidates(PoolList current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var writable = current.Pools.Where(pool => !pool.ReadOnly && pool.State is "ONLINE" or "DEGRADED").Select(pool => pool.Name).ToHashSet(StringComparer.Ordinal);
        return [.. current.Datasets
            .Where(dataset => writable.Contains(dataset.Pool) && dataset.Name == $"{dataset.Pool}/pithyx/system" && dataset is { Mounted: true, MountPoint: not null } && dataset.KeyStatus is null or "available")
            .Select(dataset => dataset.Pool)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Where the archive is for these settings: its state, its pool and its file.</summary>
    public static (LogArchiveState State, string? Pool, string? File) Locate(PoolList current, LogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        var candidates = Candidates(current);
        var pool = settings.ArchivePool ?? (candidates.Count > 0 ? candidates[0] : null);
        if (!settings.ArchiveEnabled)
        {
            return (LogArchiveState.Off, pool, null);
        }
        if (pool is null || !candidates.Contains(pool))
        {
            return (LogArchiveState.Unavailable, pool, null);
        }
        var mount = current.Datasets.First(dataset => dataset.Name == $"{pool}/pithyx/system").MountPoint!;
        return (LogArchiveState.Ready, pool, Path.Combine(mount, RelativePath));
    }

    public (LogArchiveState State, string? Pool, string? File) Locate(LogSettings settings) => Locate(pools.Current, settings);

    /// <summary>
    /// Copies what the current logs hold beyond the archive when a day has passed since the last copy or
    /// <paramref name="force"/> is set, then applies the archive's retention. False when the archive is not ready.
    /// </summary>
    public async Task<bool> SyncAsync(LogSettings settings, bool force, CancellationToken cancellationToken)
    {
        if (Locate(settings) is not (LogArchiveState.Ready, _, { } file))
        {
            return false;
        }
        await using var connection = await OpenAsync(file, cancellationToken);
        var now = time.GetUtcNow();
        if (!force && await LogDatabase.MetaAsync(connection, SyncedKey) is { } synced
            && DateTimeOffset.TryParse(synced, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var last)
            && now - last < SyncInterval)
        {
            return true;
        }
        var generation = await store.GenerationAsync(cancellationToken);
        var copied = await LogDatabase.MetaAsync(connection, OriginKey) == generation
            && long.TryParse(await LogDatabase.MetaAsync(connection, CopiedKey), System.Globalization.CultureInfo.InvariantCulture, out var until)
            ? until
            : 0;
        await connection.ExecuteAsync("ATTACH DATABASE @path AS current", new { path = store.Path });
        try
        {
            while (true)
            {
                await using var transaction = connection.BeginTransaction();
                var batchEnd = await connection.ExecuteScalarAsync<long?>(
                    "SELECT max(id) FROM (SELECT id FROM current.entries WHERE id > @copied ORDER BY id LIMIT @batch)",
                    new { copied, batch = CopyBatch },
                    transaction);
                if (batchEnd is not { } upTo)
                {
                    break;
                }
                await connection.ExecuteAsync(
                    """
                    INSERT INTO entries (time, level, source, message, request_id, user, data)
                    SELECT time, level, source, message, request_id, user, data FROM current.entries WHERE id > @copied AND id <= @upTo ORDER BY id;
                    INSERT INTO sources (source, last_seen)
                    SELECT source, max(time) FROM current.entries WHERE id > @copied AND id <= @upTo GROUP BY source
                    ON CONFLICT (source) DO UPDATE SET last_seen = max(last_seen, excluded.last_seen);
                    """,
                    new { copied, upTo },
                    transaction);
                await LogDatabase.SetMetaAsync(connection, OriginKey, generation, transaction);
                await LogDatabase.SetMetaAsync(connection, CopiedKey, upTo.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction);
                await transaction.CommitAsync(cancellationToken);
                copied = upTo;
            }
        }
        finally
        {
            await connection.ExecuteAsync("DETACH DATABASE current");
        }
        await LogDatabase.SetMetaAsync(connection, SyncedKey, now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await LogDatabase.PruneAsync(connection, now - TimeSpan.FromDays(settings.ArchiveRetentionDays), settings.ArchiveMaxBytes);
        return true;
    }

    /// <summary>The id up to which the current logs are archived, or null when the archive is not ready.</summary>
    public async Task<long?> CopiedUntilAsync(LogSettings settings, CancellationToken cancellationToken)
    {
        if (Locate(settings) is not (LogArchiveState.Ready, _, { } file) || !File.Exists(file))
        {
            return null;
        }
        await using var connection = await OpenAsync(file, cancellationToken);
        return await LogDatabase.MetaAsync(connection, OriginKey) == await store.GenerationAsync(cancellationToken)
            && long.TryParse(await LogDatabase.MetaAsync(connection, CopiedKey), System.Globalization.CultureInfo.InvariantCulture, out var until)
            ? until
            : 0;
    }

    public async Task<IReadOnlyList<LogEntry>> QueryAsync(LogSettings settings, LogQuery query, long olderThan, CancellationToken cancellationToken)
    {
        if (Locate(settings) is not (LogArchiveState.Ready, _, { } file) || !File.Exists(file))
        {
            return [];
        }
        await using var connection = await OpenAsync(file, cancellationToken);
        return await LogDatabase.QueryAsync(connection, query, olderThan, archived: true);
    }

    public async Task<IReadOnlyList<string>> SourcesAsync(LogSettings settings, CancellationToken cancellationToken)
    {
        if (Locate(settings) is not (LogArchiveState.Ready, _, { } file) || !File.Exists(file))
        {
            return [];
        }
        await using var connection = await OpenAsync(file, cancellationToken);
        return await LogDatabase.SourcesAsync(connection);
    }

    public async Task<(long Bytes, DateTimeOffset? Oldest, DateTimeOffset? SyncedAt)?> StatusAsync(LogSettings settings, CancellationToken cancellationToken)
    {
        if (Locate(settings) is not (LogArchiveState.Ready, _, { } file) || !File.Exists(file))
        {
            return null;
        }
        await using var connection = await OpenAsync(file, cancellationToken);
        var (bytes, oldest) = await LogDatabase.StatusAsync(connection);
        var synced = await LogDatabase.MetaAsync(connection, SyncedKey);
        return (bytes, oldest, synced is null ? null : DateTimeOffset.Parse(synced, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    private static async Task<SqliteConnection> OpenAsync(string file, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var connection = await LogDatabase.OpenAsync(LogDatabase.ConnectionString(file), cancellationToken);
        await LogDatabase.EnsureSchemaAsync(connection);
        return connection;
    }
}
