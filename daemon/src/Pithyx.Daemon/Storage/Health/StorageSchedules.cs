using System.Globalization;
using Dapper;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Storage.Health;

/// <param name="Kind"><c>scrub</c>, <c>shortSelfTest</c> or <c>longSelfTest</c>.</param>
/// <param name="Target">The pool name for scrubs; empty for self-tests, which cover every disk with SMART.</param>
/// <param name="Frequency"><c>off</c>, <c>weekly</c> or <c>monthly</c>.</param>
/// <param name="LastRunAt">When it last ran (UTC); for self-tests the oldest of all disks. Null if never.</param>
/// <param name="NextRunAt">When it runs next (UTC); null when it is off or has nothing to run on.</param>
public sealed record StorageSchedule(string Kind, string Target, string Frequency, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt);

/// <summary>Response of <c>GET /api/storage/schedules</c>.</summary>
public sealed record StorageScheduleList(IReadOnlyList<StorageSchedule> Schedules);

/// <summary>Body of <c>PUT /api/storage/schedules</c>.</summary>
/// <param name="Kind"><c>scrub</c>, <c>shortSelfTest</c> or <c>longSelfTest</c>.</param>
/// <param name="Target">The pool name for scrubs; empty for self-tests.</param>
/// <param name="Frequency"><c>off</c>, <c>weekly</c> or <c>monthly</c>.</param>
public sealed record StorageScheduleUpdate(string Kind, string Target, string Frequency);

/// <summary>Schedules and last runs of scrubs and self-tests in SQLite, and the pure rules for when they are due.</summary>
public sealed class StorageSchedules(SqliteConnectionFactory connections, TimeProvider time)
{
    public const string Scrub = "scrub";
    public const string ShortSelfTest = "shortSelfTest";
    public const string LongSelfTest = "longSelfTest";

    public static readonly IReadOnlyList<string> Kinds = [Scrub, ShortSelfTest, LongSelfTest];
    public static readonly IReadOnlyList<string> Frequencies = ["off", "weekly", "monthly"];

    public static string DefaultFrequency(string kind) => kind == ShortSelfTest ? "weekly" : "monthly";

    /// <summary>When a task with this frequency is due again after <paramref name="lastRun"/>; null when it is off.</summary>
    public static DateTimeOffset? NextRun(string frequency, DateTimeOffset lastRun) => frequency switch
    {
        "weekly" => lastRun.AddDays(7),
        "monthly" => lastRun.AddMonths(1),
        _ => null,
    };

    public static void Validate(StorageScheduleUpdate update, IEnumerable<string> pools)
    {
        ArgumentNullException.ThrowIfNull(update);
        var valid = Kinds.Contains(update.Kind)
            && Frequencies.Contains(update.Frequency)
            && (update.Kind == Scrub ? pools.Contains(update.Target, StringComparer.Ordinal) : update.Target.Length == 0);
        if (!valid)
        {
            throw new PithyxException(
                ErrorCodes.StorageScheduleInvalid,
                new Dictionary<string, object?> { ["kind"] = update.Kind, ["frequency"] = update.Frequency });
        }
    }

    /// <summary>Frequencies by (kind, target), for rows that exist.</summary>
    public async Task<Dictionary<(string Kind, string Target), string>> FrequenciesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string Kind, string Target, string Frequency)>("SELECT kind, target, frequency FROM storage_schedules");
        return rows.ToDictionary(row => (row.Kind, row.Target), row => row.Frequency);
    }

    public async Task SetAsync(StorageScheduleUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO storage_schedules (kind, target, frequency, updated_at) VALUES (@Kind, @Target, @Frequency, @now)
            ON CONFLICT (kind, target) DO UPDATE SET frequency = excluded.frequency, updated_at = excluded.updated_at
            """,
            new { update.Kind, update.Target, update.Frequency, now = Format(time.GetUtcNow()) });
    }

    public async Task<Dictionary<(string Kind, string Target), DateTimeOffset>> LastRunsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string Kind, string Target, string LastRunAt)>("SELECT kind, target, last_run_at FROM storage_schedule_runs");
        return rows.ToDictionary(row => (row.Kind, row.Target), row => DateTimeOffset.Parse(row.LastRunAt, CultureInfo.InvariantCulture));
    }

    public async Task SetLastRunAsync(string kind, string target, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO storage_schedule_runs (kind, target, last_run_at) VALUES (@kind, @target, @at)
            ON CONFLICT (kind, target) DO UPDATE SET last_run_at = excluded.last_run_at
            """,
            new { kind, target, at = Format(at) });
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
