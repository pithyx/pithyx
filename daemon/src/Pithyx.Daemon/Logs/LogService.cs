using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// The Log Center's queries over the current logs and the archive together, the retention settings (in the daemon
/// database, so they are part of the config backup) and the entries apps write through the SDK.
/// </summary>
public sealed partial class LogService(
    LogStore store,
    LogArchive archive,
    AppLogLimiter limiter,
    SqliteConnectionFactory connections,
    PoolInventory pools,
    TimeProvider time)
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;
    public const int MaxBatch = 100;
    public const int MaxMessageLength = 4000;
    public const int MaxDataFields = 20;
    public const int MaxDataLength = 1000;
    public const int MaxExportEntries = 1_000_000;

    public const string SettingsKey = "logs.settings";

    private LogSettings? _settings;

    public async Task<LogSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        if (_settings is { } cached)
        {
            return cached;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = SettingsKey });
        _settings = json is null ? LogSettings.Default : JsonSerializer.Deserialize<LogSettings>(json) ?? LogSettings.Default;
        return _settings;
    }

    public async Task<LogStatus> SetSettingsAsync(LogSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings, LogArchive.Candidates(pools.Current));
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                new { key = SettingsKey, value = JsonSerializer.Serialize(settings) });
        }
        _settings = settings;
        return await StatusAsync(cancellationToken);
    }

    /// <summary>Refuses values outside the documented ranges, and an archive pool that cannot hold it.</summary>
    public static void Validate(LogSettings settings, IReadOnlyList<string> archivePools)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(archivePools);
        Range("retentionDays", settings.RetentionDays, 1, 365);
        Range("maxBytes", settings.MaxBytes, LogSettings.MinMaxBytes, LogSettings.MaxMaxBytes);
        Range("archiveRetentionDays", settings.ArchiveRetentionDays, 1, 3650);
        Range("archiveMaxBytes", settings.ArchiveMaxBytes, LogSettings.MinArchiveBytes, LogSettings.MaxArchiveBytes);
        if (settings.ArchiveEnabled && settings.ArchivePool is { } pool && !archivePools.Contains(pool))
        {
            throw new PithyxException(ErrorCodes.LogsArchivePoolUnavailable, new Dictionary<string, object?> { ["pool"] = pool });
        }
    }

    private static void Range(string field, long value, long min, long max)
    {
        if (value < min || value > max)
        {
            throw new PithyxException(ErrorCodes.LogsSettingsInvalid, new Dictionary<string, object?> { ["field"] = field, ["min"] = min, ["max"] = max });
        }
    }

    public async Task<LogStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        await using var connection = await store.OpenAsync(cancellationToken);
        var (bytes, oldest) = await LogDatabase.StatusAsync(connection);
        var (state, pool, _) = archive.Locate(settings);
        var archived = await archive.StatusAsync(settings, cancellationToken);
        return new LogStatus(settings, bytes, oldest, state, pool, archived?.Bytes, archived?.Oldest, archived?.SyncedAt, LogArchive.Candidates(pools.Current));
    }

    /// <summary>
    /// One page, newest first: the current logs, then the archive for what the current logs no longer hold. The
    /// cursor is the last entry's time and id.
    /// </summary>
    public async Task<LogPage> QueryAsync(LogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var settings = await SettingsAsync(cancellationToken);
        var wanted = query with { Limit = query.Limit + 1 };
        List<LogEntry> entries;
        long? oldestCurrent;
        await using (var connection = await store.OpenAsync(cancellationToken))
        {
            entries = [.. await LogDatabase.QueryAsync(connection, wanted, olderThan: null, archived: false)];
            oldestCurrent = await connection.ExecuteScalarAsync<long?>("SELECT min(time) FROM entries");
        }
        if (entries.Count < wanted.Limit)
        {
            var boundary = oldestCurrent ?? long.MaxValue;
            entries.AddRange(await archive.QueryAsync(settings, wanted with { Limit = wanted.Limit - entries.Count }, boundary, cancellationToken));
        }
        if (entries.Count <= query.Limit)
        {
            return new LogPage(entries, null);
        }
        var last = entries[query.Limit - 1];
        return new LogPage(entries[..query.Limit], new LogCursor(LogDatabase.Micros(last.Time), last.Id).ToString());
    }

    public async Task<IReadOnlyList<string>> SourcesAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        await using var connection = await store.OpenAsync(cancellationToken);
        var current = await LogDatabase.SourcesAsync(connection);
        var archived = await archive.SourcesAsync(settings, cancellationToken);
        return [.. current.Union(archived, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>Every matching entry, newest first, as JSON lines or as text, up to <see cref="MaxExportEntries"/>.</summary>
    public async Task ExportAsync(LogQuery query, bool text, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        var page = query with { Limit = MaxLimit };
        var written = 0;
        while (written < MaxExportEntries)
        {
            var result = await QueryAsync(page, cancellationToken);
            foreach (var entry in result.Entries)
            {
                await writer.WriteLineAsync(text ? TextLine(entry) : JsonSerializer.Serialize(entry, ExportJson.Options));
            }
            written += result.Entries.Count;
            if (result.NextCursor is null)
            {
                break;
            }
            page = page with { Before = LogCursor.Parse(result.NextCursor) };
        }
        await writer.FlushAsync(cancellationToken);
    }

    public static string TextLine(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = new StringBuilder()
            .Append(entry.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(entry.Level.ToString().ToUpperInvariant().PadRight(5))
            .Append(' ')
            .Append(entry.Source)
            .Append(' ')
            .Append(entry.Message.ReplaceLineEndings(" "));
        if (entry.RequestId is { } request)
        {
            line.Append(" request=").Append(request);
        }
        if (entry.User is { } user)
        {
            line.Append(" user=").Append(user);
        }
        return line.ToString();
    }

    /// <summary>Stores what an app logs for a user, within the app's rate limit; long texts are cut, not refused.</summary>
    public AppLogResult WriteApp(string app, string user, AppLogBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!AppIdPattern().IsMatch(app))
        {
            throw new PithyxException(ErrorCodes.LogsAppInvalid, new Dictionary<string, object?> { ["app"] = app });
        }
        if (batch.Entries.Count > MaxBatch)
        {
            throw new PithyxException(ErrorCodes.LogsBatchTooLarge, new Dictionary<string, object?> { ["max"] = MaxBatch });
        }
        var allowed = limiter.Take(app, batch.Entries.Count);
        var now = time.GetUtcNow();
        foreach (var entry in batch.Entries.Take(allowed))
        {
            store.Enqueue(new PendingLogEntry(now, entry.Level, $"app/{app}", Cut(entry.Message, MaxMessageLength), System.Diagnostics.Activity.Current?.TraceId.ToString(), user, Fields(entry.Data)));
        }
        return new AppLogResult(allowed, batch.Entries.Count - allowed);
    }

    private static Dictionary<string, string>? Fields(IReadOnlyDictionary<string, string>? data) =>
        data is null or { Count: 0 }
            ? null
            : data.Take(MaxDataFields).ToDictionary(pair => Cut(pair.Key, 100), pair => Cut(pair.Value, MaxDataLength), StringComparer.Ordinal);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();

    private static class ExportJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
    }
}
