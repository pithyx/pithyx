using System.Text.Json;

namespace Pithyx.Daemon.Storage.Health;

/// <summary>A storage problem that exists now. Alerts are derived from the current state, never stored.</summary>
/// <param name="Key">Stable key of the cause, for example <c>pool.degraded:tank</c> or <c>disk.smart_failed:wwn-0x5000c500a1b2c3d4</c>.</param>
/// <param name="Severity"><c>warning</c> or <c>critical</c>.</param>
/// <param name="Code">Message code in the <c>notifications</c> namespace (<c>&lt;code&gt;.title</c>, <c>.body</c>, <c>.action</c>).</param>
/// <param name="Params">Message parameters: strings and numbers.</param>
/// <param name="Since">When the daemon first saw the cause (UTC); a restart starts it again.</param>
/// <param name="Muted">True when the user muted this alert at this severity or higher.</param>
public sealed record StorageAlert(
    string Key,
    string Severity,
    string Code,
    IReadOnlyDictionary<string, JsonElement> Params,
    DateTimeOffset Since,
    bool Muted);

/// <summary>Snapshot and event of <c>storage/alerts</c>, and response of <c>GET /api/storage/alerts</c>.</summary>
/// <param name="Alerts">Current alerts, critical first, with the user's mute flags.</param>
/// <param name="Smart">SMART state of every disk.</param>
public sealed record StorageHealth(IReadOnlyList<StorageAlert> Alerts, IReadOnlyList<SmartReport> Smart);

/// <summary>Derives the current alerts from pools and SMART reports. Pure: the same state gives the same alerts.</summary>
public static class StorageAlerts
{
    public const string Warning = "warning";
    public const string Critical = "critical";

    public const int CapacityWarningPercent = 80;
    public const int CapacityCriticalPercent = 90;

    private static readonly HashSet<string> _faultedStates = new(StringComparer.Ordinal) { "FAULTED", "UNAVAIL", "SUSPENDED", "REMOVED", "OFFLINE" };

    public static IReadOnlyList<StorageAlert> Evaluate(PoolList pools, IReadOnlyList<SmartReport> smart, IReadOnlyList<Disk> disks, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(smart);
        ArgumentNullException.ThrowIfNull(disks);
        var alerts = new List<StorageAlert>();
        void Add(string key, string severity, string code, Dictionary<string, object> parameters) =>
            alerts.Add(new StorageAlert(
                key,
                severity,
                code,
                parameters.ToDictionary(entry => entry.Key, entry => JsonSerializer.SerializeToElement(entry.Value), StringComparer.Ordinal),
                now,
                false));

        foreach (var pool in pools.Pools)
        {
            var name = new Dictionary<string, object> { ["pool"] = pool.Name };
            if (pool.State == "DEGRADED")
            {
                Add($"pool.degraded:{pool.Name}", Critical, "storage.alert.poolDegraded", name);
            }
            else if (_faultedStates.Contains(pool.State))
            {
                Add($"pool.faulted:{pool.Name}", Critical, "storage.alert.poolFaulted", new() { ["pool"] = pool.Name, ["state"] = pool.State });
            }
            if (pool.DataErrors > 0)
            {
                Add($"pool.data_errors:{pool.Name}", Critical, "storage.alert.dataErrors", new() { ["pool"] = pool.Name, ["errors"] = pool.DataErrors });
            }
            if (pool.Scan is { Kind: "scrub", State: "finished", Errors: > 0 } scan)
            {
                Add($"pool.scrub_errors:{pool.Name}", Warning, "storage.alert.scrubErrors", new() { ["pool"] = pool.Name, ["errors"] = scan.Errors });
            }
            if (pool.CapacityPercent >= CapacityWarningPercent)
            {
                var critical = pool.CapacityPercent >= CapacityCriticalPercent;
                Add(
                    $"pool.capacity:{pool.Name}",
                    critical ? Critical : Warning,
                    critical ? "storage.alert.capacityCritical" : "storage.alert.capacityWarning",
                    new() { ["pool"] = pool.Name, ["percent"] = pool.CapacityPercent });
            }
        }

        var byId = disks.ToDictionary(disk => disk.Id, StringComparer.Ordinal);
        foreach (var report in smart)
        {
            if (!byId.TryGetValue(report.DiskId, out var disk))
            {
                continue;
            }
            var identity = new Dictionary<string, object> { ["disk"] = disk.Name, ["serial"] = disk.Serial ?? "", ["model"] = disk.Model ?? "" };
            if (report.State == "failed")
            {
                Add($"disk.smart_failed:{disk.Id}", Critical, "storage.alert.smartFailed", identity);
            }
            else if (report.State == "warning")
            {
                identity["reallocated"] = report.ReallocatedSectors ?? 0;
                identity["pending"] = report.PendingSectors ?? 0;
                identity["uncorrectable"] = report.UncorrectableSectors ?? 0;
                identity["mediaErrors"] = report.MediaErrors ?? 0;
                identity["selfTest"] = report.LastSelfTest == "failed" ? "failed" : "ok";
                Add($"disk.smart_warning:{disk.Id}", Warning, "storage.alert.smartWarning", identity);
            }
        }

        return [.. alerts.OrderBy(alert => alert.Severity == Critical ? 0 : 1).ThenBy(alert => alert.Key, StringComparer.Ordinal)];
    }

    /// <summary>Critical outranks warning.</summary>
    public static int Rank(string severity) => severity == Critical ? 2 : 1;
}
