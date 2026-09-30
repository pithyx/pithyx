using System.Collections.Concurrent;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// A token bucket per app, so no app can flood the logs: bursts of <see cref="Burst"/> entries, then
/// <see cref="PerSecond"/> a second. What an app loses is counted and reported once a minute as one warning.
/// </summary>
public sealed class AppLogLimiter(TimeProvider time)
{
    public const int Burst = 200;
    public const int PerSecond = 20;
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    private sealed class Bucket(DateTimeOffset now)
    {
        public double Tokens = Burst;
        public DateTimeOffset Refilled = now;
        public long Dropped;
        public DateTimeOffset Reported = now;
    }

    /// <summary>How many of <paramref name="count"/> entries the app may write now.</summary>
    public int Take(string app, int count)
    {
        var now = time.GetUtcNow();
        var bucket = _buckets.GetOrAdd(app, _ => new Bucket(now));
        lock (bucket)
        {
            bucket.Tokens = Math.Min(Burst, bucket.Tokens + ((now - bucket.Refilled).TotalSeconds * PerSecond));
            bucket.Refilled = now;
            var allowed = (int)Math.Min(count, Math.Floor(bucket.Tokens));
            bucket.Tokens -= allowed;
            bucket.Dropped += count - allowed;
            return allowed;
        }
    }

    /// <summary>Apps that lost entries and were not reported within the last minute, with how many they lost.</summary>
    public IReadOnlyList<(string App, long Dropped)> TakeReports()
    {
        var now = time.GetUtcNow();
        var reports = new List<(string, long)>();
        foreach (var (app, bucket) in _buckets)
        {
            lock (bucket)
            {
                if (bucket.Dropped > 0 && now - bucket.Reported >= ReportInterval)
                {
                    reports.Add((app, bucket.Dropped));
                    bucket.Dropped = 0;
                    bucket.Reported = now;
                }
            }
        }
        return reports;
    }
}
