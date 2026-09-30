using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Storage.Health;

/// <summary>
/// The current storage alerts and the users' mutes, for the <c>storage/alerts</c> topic. Alerts are evaluated again on
/// every change of the pools or the SMART state; callers publish when a method says the state changed (the topic
/// registry depends on this class, so it cannot depend on the hub). A mute hides an alert from one user at its severity; it ends when the
/// alert goes away, so a cause that comes back is shown again, and a higher severity shows through it.
/// </summary>
public sealed partial class StorageAlertService(
    SqliteConnectionFactory connections,
    PoolInventory pools,
    DiskInventory disks,
    SmartInventory smart,
    TimeProvider time,
    ILogger<StorageAlertService> logger) : IDisposable
{
    public const string Topic = "storage/alerts";

    private readonly Lock _lock = new();
    // One refresh at a time: the monitor and a request can refresh together, and a caller returns only once the mutes it
    // ended are gone from the database too.
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<(long User, string Key), string> _mutes = [];
    private StorageHealth _current = new([], []);
    private string _fingerprint = "";

    /// <summary>
    /// Set once pools and SMART have been read after startup. Before that an alert may be missing only because its
    /// source was not read yet, so no mute is ended.
    /// </summary>
    public bool Settled { get; set; }

    public StorageHealth Current => Volatile.Read(ref _current);

    public async Task LoadMutesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(long UserId, string AlertKey, string Severity)>("SELECT user_id, alert_key, severity FROM storage_alert_mutes");
        lock (_lock)
        {
            _mutes.Clear();
            foreach (var row in rows)
            {
                _mutes[(row.UserId, row.AlertKey)] = row.Severity;
            }
        }
    }

    /// <summary>Evaluates the alerts again and ends mutes of alerts that went away; true when the topic must be published.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            return await RefreshOnceAsync(cancellationToken);
        }
        finally
        {
            _refresh.Release();
        }
    }

    public void Dispose() => _refresh.Dispose();

    private async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var evaluated = StorageAlerts.Evaluate(pools.Current, smart.Current, disks.Current.Disks, now);
        List<(long User, string Key)> ended;
        StorageHealth next;
        bool changed;
        lock (_lock)
        {
            var since = _current.Alerts.ToDictionary(alert => alert.Key, alert => alert.Since, StringComparer.Ordinal);
            next = new StorageHealth(
                [.. evaluated.Select(alert => since.TryGetValue(alert.Key, out var first) ? alert with { Since = first } : alert)],
                smart.Current);
            var keys = next.Alerts.Select(alert => alert.Key).ToHashSet(StringComparer.Ordinal);
            ended = Settled ? [.. _mutes.Keys.Where(mute => !keys.Contains(mute.Key))] : [];
            ended.ForEach(mute => _mutes.Remove(mute));
            var fingerprint = JsonSerializer.Serialize(next);
            changed = fingerprint != _fingerprint;
            _fingerprint = fingerprint;
            Volatile.Write(ref _current, next);
        }
        if (ended.Count > 0)
        {
            await using var connection = await connections.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "DELETE FROM storage_alert_mutes WHERE user_id = @User AND alert_key = @Key",
                ended.Select(mute => new { mute.User, mute.Key }));
            LogMutesEnded(ended.Count);
        }
        return changed || ended.Count > 0;
    }

    /// <summary>Mutes a current alert for the user at its current severity.</summary>
    public async Task MuteAsync(ClaimsPrincipal user, string key, CancellationToken cancellationToken)
    {
        var userId = CurrentUser.Id(user);
        var alert = Current.Alerts.FirstOrDefault(candidate => candidate.Key == key)
            ?? throw new PithyxException(ErrorCodes.StorageAlertNotFound, new Dictionary<string, object?> { ["key"] = key });
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO storage_alert_mutes (user_id, alert_key, severity, muted_at) VALUES (@userId, @key, @severity, @now)
                ON CONFLICT (user_id, alert_key) DO UPDATE SET severity = excluded.severity, muted_at = excluded.muted_at
                """,
                new { userId, key, severity = alert.Severity, now = time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture) });
        }
        lock (_lock)
        {
            _mutes[(userId, key)] = alert.Severity;
        }
    }

    public async Task UnmuteAsync(ClaimsPrincipal user, string key, CancellationToken cancellationToken)
    {
        var userId = CurrentUser.Id(user);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("DELETE FROM storage_alert_mutes WHERE user_id = @userId AND alert_key = @key", new { userId, key });
        }
        lock (_lock)
        {
            _mutes.Remove((userId, key));
        }
    }

    /// <summary>The state as one user sees it: alerts carry that user's mute flags.</summary>
    public StorageHealth For(ClaimsPrincipal user, StorageHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (user?.FindFirstValue(ClaimTypes.NameIdentifier) is not { } id)
        {
            return health;
        }
        var userId = long.Parse(id, CultureInfo.InvariantCulture);
        lock (_lock)
        {
            return health with
            {
                Alerts = [.. health.Alerts.Select(alert => alert with
                {
                    Muted = _mutes.TryGetValue((userId, alert.Key), out var severity) && StorageAlerts.Rank(severity) >= StorageAlerts.Rank(alert.Severity),
                })],
            };
        }
    }

    public TopicDefinition Definition() => new(
        Topic,
        typeof(StorageHealth),
        typeof(StorageHealth),
        (_, _) => ValueTask.FromResult<object>(Current),
        PoolInventory.IsAllowed,
        ForUser: (user, data) => For(user, (StorageHealth)data));

    [LoggerMessage(Level = LogLevel.Information, Message = "Ended {Count} alert mutes whose alerts went away")]
    private partial void LogMutesEnded(int count);
}
