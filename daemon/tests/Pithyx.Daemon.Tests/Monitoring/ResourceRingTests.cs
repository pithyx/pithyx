using Pithyx.Daemon.Monitoring;

namespace Pithyx.Daemon.Tests.Monitoring;

public sealed class ResourceRingTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 29, 1, 0, 0, TimeSpan.Zero);

    private static ResourceSample Sample(DateTimeOffset at, double cpu, long used = 100, long? arc = 10, params (string Name, long In, long Out)[] disks) =>
        new(
            at,
            1,
            [0, 0, 0],
            new CpuUsage(cpu, [cpu]),
            new MemoryUsage(1000, used, arc, arc is null ? null : 500, 0, 0, 0, 0, 0),
            [new NetworkRate("eth0", (long)cpu, 0)],
            [.. disks.Select(disk => new DiskRate(disk.Name, disk.In, disk.Out, 0))],
            []);

    [Fact]
    public void KeepsOnlyTheLastHour()
    {
        var ring = new ResourceRing();
        for (var second = 0; second <= 3700; second++)
        {
            ring.Add(Sample(_start.AddSeconds(second), 1));
        }

        var history = ring.History();

        Assert.Equal(_start.AddSeconds(3700), ring.Latest()!.At);
        Assert.InRange(history.Times.Count, 720, 721);
        Assert.True(history.Times[0] > _start.AddSeconds(100));
    }

    [Fact]
    public void AveragesFiveSecondBuckets()
    {
        var ring = new ResourceRing();
        for (var second = 0; second < 10; second++)
        {
            ring.Add(Sample(_start.AddSeconds(second), second < 5 ? 10 : 30, used: second < 5 ? 100 : 300));
        }

        var history = ring.History();

        Assert.Equal(5, history.BucketSeconds);
        Assert.Equal([_start.AddSeconds(5), _start.AddSeconds(10)], history.Times);
        Assert.Equal([10.0, 30.0], history.CpuPercent);
        Assert.Equal([100L, 300L], history.MemoryUsedBytes);
        Assert.Equal([10L, 30L], Assert.Single(history.Network).In);
    }

    [Fact]
    public void ADeviceMissingInABucketIsNullThere()
    {
        var ring = new ResourceRing();
        ring.Add(Sample(_start, 1, disks: [("sda", 100, 200)]));
        ring.Add(Sample(_start.AddSeconds(5), 1, arc: null, disks: [("sda", 300, 400), ("sdb", 7, 8)]));

        var history = ring.History();

        Assert.Equal(["sda", "sdb"], history.Disks.Select(disk => disk.Name));
        Assert.Equal([100L, 300L], history.Disks[0].In);
        Assert.Equal([null, 8L], history.Disks[1].Out);
        Assert.Equal([10L, null], history.ArcBytes);
    }

    [Fact]
    public void AnEmptyRingHasNoHistory()
    {
        var ring = new ResourceRing();

        Assert.Null(ring.Latest());
        Assert.Empty(ring.History().Times);
    }
}
