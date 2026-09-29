using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Shares;

/// <summary>
/// Whether a shared folder's dataset can be reached now, from the pools as last read. A share on a dataset whose pool is
/// gone, stopped or not mounted is unavailable: the file service then refuses it instead of listing, or writing into,
/// the empty mount point folder on the system disk. Shares without a dataset (plain folders on a development machine)
/// are always available. Pure.
/// </summary>
public static class ShareAvailability
{
    public const string PoolMissing = "pool_missing";
    public const string PoolUnavailable = "pool_unavailable";
    public const string NotMounted = "not_mounted";

    private static readonly HashSet<string> _stopped = new(StringComparer.Ordinal) { "UNAVAIL", "FAULTED", "SUSPENDED", "REMOVED" };

    /// <summary>Why the share cannot be reached, or null when it can (or when the pools were never read).</summary>
    public static string? Reason(string? dataset, PoolList pools, bool poolsRead)
    {
        ArgumentNullException.ThrowIfNull(pools);
        if (dataset is null || !poolsRead)
        {
            return null;
        }
        var found = pools.Datasets.FirstOrDefault(candidate => candidate.Name == dataset);
        var pool = pools.Pools.FirstOrDefault(candidate => candidate.Name == dataset.Split('/')[0]);
        if (found is null || pool is null)
        {
            return PoolMissing;
        }
        if (_stopped.Contains(pool.State))
        {
            return PoolUnavailable;
        }
        return found.Mounted ? null : NotMounted;
    }

    /// <summary>What decides the availability of every share dataset; a change means the share lists may differ.</summary>
    public static string Fingerprint(PoolList pools)
    {
        ArgumentNullException.ThrowIfNull(pools);
        var datasets = pools.Datasets.Where(dataset => dataset.Name.Contains("/pithyx/shares/", StringComparison.Ordinal)).Select(dataset => $"{dataset.Name}={dataset.Mounted}");
        var states = pools.Pools.Select(pool => $"{pool.Name}:{_stopped.Contains(pool.State)}");
        return string.Join('|', states.Concat(datasets).Order(StringComparer.Ordinal));
    }
}
