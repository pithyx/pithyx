using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Notifications;

namespace Pithyx.Daemon.Storage.Health;

/// <summary>
/// Turns storage alerts into notifications for every administrator: one when an alert appears or becomes more
/// severe, and an info one when it goes away. The alerts notified about are kept in <c>storage_alert_notices</c>,
/// so a restart with the same alerts notifies nobody again, and an alert that appeared while the daemon was off is
/// notified once it is seen. Alerts that go away are only resolved once the state is settled (pools and SMART read
/// after startup), so an alert whose source was not read yet is not reported as resolved. A capacity alert is
/// resolved and calmed down only once the fill level is <see cref="CapacityHysteresisPercent"/> points below its
/// threshold, so a pool hovering around a threshold notifies once. An alert of a pool that is no longer imported
/// (exported or destroyed) is dropped without a notification: nothing was resolved.
/// </summary>
public sealed partial class StorageAlertNotifier(
    SqliteConnectionFactory connections,
    NotificationService notifications,
    TimeProvider time,
    ILogger<StorageAlertNotifier> logger) : IDisposable
{
    /// <summary>
    /// The code of an alert that went away. <c>params.alert</c> is the alert's code, <c>params.alertKey</c> its key,
    /// and the alert's own parameters follow.
    /// </summary>
    public const string ResolvedCode = "storage.alert.resolved";

    /// <summary>How far below a capacity threshold the fill level must fall before the alert counts as gone.</summary>
    public const int CapacityHysteresisPercent = 5;

    private const string CapacityKeyPrefix = "pool.capacity:";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Compares the current alerts with those notified before and notifies the differences. Never throws.</summary>
    /// <param name="alerts">The current alerts.</param>
    /// <param name="pools">The current pools, for the capacity hysteresis and to tell a pool that is gone.</param>
    /// <param name="settled">True once pools and SMART have been read after startup.</param>
    /// <param name="cancellationToken">Cancels the sync.</param>
    public async Task SyncAsync(IReadOnlyList<StorageAlert> alerts, PoolList pools, bool settled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(pools);
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await SyncLockedAsync(alerts, pools, settled, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Alerts stay visible in the panel either way; the next change tries again.
            LogFailed(exception);
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task SyncLockedAsync(IReadOnlyList<StorageAlert> alerts, PoolList pools, bool settled, CancellationToken cancellationToken)
    {
        Dictionary<string, Notice> notices;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            notices = (await connection.QueryAsync<Notice>("SELECT alert_key AS Key, severity AS Severity, code AS Code, params AS Params FROM storage_alert_notices"))
                .ToDictionary(notice => notice.Key, StringComparer.Ordinal);
        }

        var capacities = pools.Pools.ToDictionary(pool => pool.Name, pool => pool.CapacityPercent, StringComparer.Ordinal);
        foreach (var alert in alerts)
        {
            var known = notices.GetValueOrDefault(alert.Key);
            if (known is not null && StorageAlerts.Rank(alert.Severity) <= StorageAlerts.Rank(known.Severity))
            {
                if (StorageAlerts.Rank(alert.Severity) < StorageAlerts.Rank(known.Severity) && !StillNear(known, capacities, StorageAlerts.CapacityCriticalPercent))
                {
                    // Less severe now: remember that, so a later rise is notified again.
                    await SaveAsync(alert, cancellationToken);
                }
                continue;
            }
            // Notified first, remembered then: a crash in between notifies twice rather than never.
            var parameters = alert.Params.ToDictionary(entry => entry.Key, entry => (object?)entry.Value, StringComparer.Ordinal);
            parameters["alertKey"] = alert.Key;
            var tone = alert.Severity == StorageAlerts.Critical ? NotificationTone.Critical : NotificationTone.Warning;
            if (await notifications.CreateForAdministratorsAsync(new NotificationDraft(alert.Code, parameters, tone), cancellationToken) == 0)
            {
                // Before setup there is nobody to tell; the alert is notified with the next evaluation after it.
                continue;
            }
            await SaveAsync(alert, cancellationToken);
            LogRaised(alert.Key, alert.Severity);
        }

        if (!settled)
        {
            return;
        }
        var current = alerts.Select(alert => alert.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var notice in notices.Values.Where(notice => !current.Contains(notice.Key)))
        {
            var parameters = ParamsOf(notice.Params);
            var pool = parameters.GetValueOrDefault("pool") is JsonElement { ValueKind: JsonValueKind.String } name ? name.GetString() : null;
            var gone = pool is not null && !capacities.ContainsKey(pool);
            if (!gone)
            {
                if (StillNear(notice, capacities, StorageAlerts.CapacityWarningPercent))
                {
                    continue;
                }
                parameters["alert"] = notice.Code;
                parameters["alertKey"] = notice.Key;
                await notifications.CreateForAdministratorsAsync(new NotificationDraft(ResolvedCode, parameters, NotificationTone.Info), cancellationToken);
            }
            await using var connection = await connections.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("DELETE FROM storage_alert_notices WHERE alert_key = @Key", new { notice.Key });
            if (gone)
            {
                LogDropped(notice.Key);
            }
            else
            {
                LogResolved(notice.Key);
            }
        }
    }

    private async Task SaveAsync(StorageAlert alert, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO storage_alert_notices (alert_key, severity, code, params, noticed_at) VALUES (@Key, @Severity, @Code, @Params, @Now)
            ON CONFLICT (alert_key) DO UPDATE SET severity = excluded.severity, code = excluded.code, params = excluded.params, noticed_at = excluded.noticed_at
            """,
            new { alert.Key, alert.Severity, alert.Code, Params = JsonSerializer.Serialize(alert.Params), Now = SessionService.Format(time.GetUtcNow()) });
    }

    /// <summary>True for a capacity notice whose pool is still within the hysteresis below the threshold.</summary>
    private static bool StillNear(Notice notice, Dictionary<string, int> capacities, int threshold) =>
        notice.Key.StartsWith(CapacityKeyPrefix, StringComparison.Ordinal)
        && capacities.TryGetValue(notice.Key[CapacityKeyPrefix.Length..], out var percent)
        && percent >= threshold - CapacityHysteresisPercent;

    private static Dictionary<string, object?> ParamsOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notified administrators of storage alert {AlertKey} ({Severity})")]
    private partial void LogRaised(string alertKey, string severity);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notified administrators that storage alert {AlertKey} was resolved")]
    private partial void LogResolved(string alertKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped storage alert {AlertKey} because its pool is no longer imported")]
    private partial void LogDropped(string alertKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifying administrators of storage alerts failed")]
    private partial void LogFailed(Exception exception);

    private sealed class Notice
    {
        public string Key { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Code { get; init; } = "";
        public string Params { get; init; } = "{}";
    }
}
