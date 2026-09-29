namespace Pithyx.Daemon.Monitoring;

/// <summary>
/// The samples of the last hour in memory, never in the database. It only holds the seconds someone watched: the
/// monitor samples only while <c>system/resources</c> has a subscriber.
/// </summary>
public sealed class ResourceRing
{
    public static readonly TimeSpan Span = TimeSpan.FromHours(1);

    /// <summary>The snapshot averages the ring into buckets of this many seconds: 720 per hour instead of 3600.</summary>
    public const int BucketSeconds = 5;

    private readonly Lock _lock = new();
    private readonly Queue<ResourceSample> _samples = new();

    public void Add(ResourceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_lock)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > 0 && _samples.Peek().At <= sample.At - Span)
            {
                _samples.Dequeue();
            }
        }
    }

    public ResourceSample? Latest()
    {
        lock (_lock)
        {
            return _samples.Count == 0 ? null : _samples.Last();
        }
    }

    /// <summary>
    /// The ring averaged into <see cref="BucketSeconds"/> buckets by UTC time, oldest first. A device appears in the
    /// series from its first sample on; buckets without it have null.
    /// </summary>
    public ResourceHistory History()
    {
        List<ResourceSample> samples;
        lock (_lock)
        {
            samples = [.. _samples];
        }
        var buckets = samples
            .GroupBy(sample => sample.At.ToUnixTimeSeconds() / BucketSeconds)
            .OrderBy(bucket => bucket.Key)
            .Select(bucket => bucket.ToList())
            .ToList();
        var interfaces = samples.SelectMany(sample => sample.Network).Select(rate => rate.Name).Distinct(StringComparer.Ordinal).ToList();
        var disks = samples.SelectMany(sample => sample.Disks).Select(rate => rate.Name).Distinct(StringComparer.Ordinal).ToList();

        return new ResourceHistory(
            BucketSeconds,
            [.. buckets.Select(bucket => DateTimeOffset.FromUnixTimeSeconds((bucket[0].At.ToUnixTimeSeconds() / BucketSeconds + 1) * BucketSeconds))],
            [.. buckets.Select(bucket => Math.Round(bucket.Average(sample => sample.Cpu.Percent), 1))],
            [.. buckets.Select(bucket => (long)bucket.Average(sample => sample.Memory.UsedBytes))],
            [.. buckets.Select(bucket => Average(bucket.Select(sample => sample.Memory.ArcBytes)))],
            [.. interfaces.Select(name => Series(
                name,
                buckets,
                sample => sample.Network.FirstOrDefault(rate => rate.Name == name) is { } rate ? (rate.ReceiveBytesPerSecond, rate.SendBytesPerSecond) : null))],
            [.. disks.Select(name => Series(
                name,
                buckets,
                sample => sample.Disks.FirstOrDefault(rate => rate.Name == name) is { } rate ? (rate.ReadBytesPerSecond, rate.WriteBytesPerSecond) : null))]);
    }

    private static DeviceSeries Series(string name, List<List<ResourceSample>> buckets, Func<ResourceSample, (long In, long Out)?> rate)
    {
        var values = buckets.Select(bucket => bucket.Select(rate).OfType<(long In, long Out)>().ToList()).ToList();
        return new DeviceSeries(
            name,
            [.. values.Select(found => found.Count == 0 ? (long?)null : (long)found.Average(value => value.In))],
            [.. values.Select(found => found.Count == 0 ? (long?)null : (long)found.Average(value => value.Out))]);
    }

    private static long? Average(IEnumerable<long?> values)
    {
        var present = values.OfType<long>().ToList();
        return present.Count == 0 ? null : (long)present.Average();
    }
}
