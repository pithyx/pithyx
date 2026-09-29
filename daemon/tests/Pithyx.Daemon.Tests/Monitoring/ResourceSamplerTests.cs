using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Monitoring;

namespace Pithyx.Daemon.Tests.Monitoring;

/// <summary>Two readings of a /proc copy that the test moves forward, with a fake clock.</summary>
public sealed class ResourceSamplerTests : IDisposable
{
    private static readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private readonly string _proc = Directory.CreateTempSubdirectory("pithyx-proc-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero));
    private readonly ResourceSampler _sampler;

    public ResourceSamplerTests()
    {
        foreach (var file in new[] { "meminfo", "uptime", "loadavg" })
        {
            File.Copy(Path.Combine(_fixtures, "proc", file), Path.Combine(_proc, file));
        }
        Directory.CreateDirectory(Path.Combine(_proc, "net"));
        Directory.CreateDirectory(Path.Combine(_proc, "spl", "kstat", "zfs"));
        File.Copy(Path.Combine(_fixtures, "proc", "spl", "kstat", "zfs", "arcstats"), Path.Combine(_proc, "spl", "kstat", "zfs", "arcstats"));
        Write(cpu: [(100, 300), (50, 150), (50, 150)], received: 1_000, sent: 5_000, sectorsRead: 1_000, sectorsWritten: 2_000, ioMilliseconds: 100);
        _sampler = new ResourceSampler(new ResourceFiles(_proc, Path.Combine(_fixtures, "sys-handwritten")), _time);
    }

    public void Dispose() => Directory.Delete(_proc, recursive: true);

    /// <summary>Busy and idle jiffies of the aggregate and each core, and the counters of ens18 and sda.</summary>
    private void Write((long Busy, long Idle)[] cpu, long received, long sent, long sectorsRead, long sectorsWritten, long ioMilliseconds)
    {
        File.WriteAllLines(Path.Combine(_proc, "stat"), cpu.Select((times, index) => $"{(index == 0 ? "cpu " : $"cpu{index - 1}")} {times.Busy} 0 0 {times.Idle} 0 0 0 0 0 0"));
        File.WriteAllText(Path.Combine(_proc, "net", "dev"), $"""
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo: 9999 1 0 0 0 0 0 0 9999 1 0 0 0 0 0 0
             ens18: {received} 1 0 0 0 0 0 0 {sent} 1 0 0 0 0 0 0

            """);
        File.WriteAllText(
            Path.Combine(_proc, "diskstats"),
            $"   8       0 sda 1 0 {sectorsRead} 0 1 0 {sectorsWritten} 0 0 {ioMilliseconds} 0 0 0 0 0 0 0\n   8       1 sda1 1 0 {sectorsRead} 0 1 0 {sectorsWritten} 0 0 {ioMilliseconds} 0 0 0 0 0 0 0\n");
    }

    [Fact]
    public void TheFirstReadingOnlySetsTheBaseline() => Assert.Null(_sampler.Sample());

    [Fact]
    public void RatesArePerSecondBetweenTwoReadings()
    {
        _sampler.Sample();
        _time.Advance(TimeSpan.FromSeconds(2));
        Write(cpu: [(200, 400), (150, 150), (50, 250)], received: 201_000, sent: 5_000, sectorsRead: 3_000, sectorsWritten: 2_000, ioMilliseconds: 1_100);

        var sample = _sampler.Sample()!;

        Assert.Equal(50.0, sample.Cpu.Percent);
        Assert.Equal([100.0, 0.0], sample.Cpu.Cores);
        var network = Assert.Single(sample.Network);
        Assert.Equal(new NetworkRate("ens18", 100_000, 0), network);
        var disk = Assert.Single(sample.Disks);
        Assert.Equal(new DiskRate("sda", 512_000, 0, 50.0), disk);
        Assert.Equal(_time.GetUtcNow(), sample.At);
        Assert.Equal(3, sample.LoadAverage.Count);
    }

    [Fact]
    public void CountersThatWentBackGiveZeroInsteadOfANegativeRate()
    {
        _sampler.Sample();
        _time.Advance(TimeSpan.FromSeconds(1));
        Write(cpu: [(200, 400), (150, 150), (50, 250)], received: 10, sent: 5_000, sectorsRead: 10, sectorsWritten: 2_000, ioMilliseconds: 100);

        var sample = _sampler.Sample()!;

        Assert.Equal(0, sample.Network[0].ReceiveBytesPerSecond);
        Assert.Equal(0, sample.Disks[0].ReadBytesPerSecond);
    }

    [Fact]
    public void MemoryLeavesCachesAndTheArcOutOfUsed()
    {
        _sampler.Sample();
        _time.Advance(TimeSpan.FromSeconds(1));

        var memory = _sampler.Sample()!.Memory;

        var counters = ResourceParsers.ParseMemory(File.ReadAllText(Path.Combine(_proc, "meminfo")));
        var arc = ResourceParsers.ParseArc(File.ReadAllText(Path.Combine(_proc, "spl", "kstat", "zfs", "arcstats")));
        Assert.Equal(arc.SizeBytes, memory.ArcBytes);
        Assert.Equal(arc.MaxBytes, memory.ArcMaxBytes);
        Assert.Equal(counters.BuffersBytes + counters.CachedBytes + counters.ReclaimableBytes, memory.CacheBytes);
        Assert.Equal(Math.Max(0, counters.TotalBytes - counters.FreeBytes - memory.CacheBytes - arc.SizeBytes), memory.UsedBytes);
        Assert.Equal(counters.SwapTotalBytes - counters.SwapFreeBytes, memory.SwapUsedBytes);
    }

    [Fact]
    public void WithoutZfsThereIsNoArc()
    {
        File.Delete(Path.Combine(_proc, "spl", "kstat", "zfs", "arcstats"));
        _sampler.Sample();
        _time.Advance(TimeSpan.FromSeconds(1));

        var memory = _sampler.Sample()!.Memory;

        Assert.Null(memory.ArcBytes);
        Assert.Null(memory.ArcMaxBytes);
    }

    [Fact]
    public void TemperaturesComeFromHwmonAndThermalZonesButNeverFromDrivetemp()
    {
        _sampler.Sample();
        _time.Advance(TimeSpan.FromSeconds(1));

        var temperatures = _sampler.Sample()!.Temperatures;

        Assert.Equal(
            [
                new TemperatureReading("coretemp", "Package id 0", 47.0, 80.0, 100.0),
                new TemperatureReading("coretemp", "Core 0", 45.0, 80.0, 100.0),
                new TemperatureReading("nvme", "Composite", 38.9, 81.9, 84.9),
                new TemperatureReading("acpitz", null, 27.8, null, 105.0),
                new TemperatureReading("x86_pkg_temp", null, 48.0, null, null),
            ],
            temperatures);
    }

    [Fact]
    public void AMachineWithoutSensorsHasNoTemperatures()
    {
        var sampler = new ResourceSampler(new ResourceFiles(_proc, Path.Combine(_proc, "no-sys")), _time);
        sampler.Sample();
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(sampler.Sample()!.Temperatures);
    }

    [Fact]
    public void DescribesCoresAndMemory()
    {
        var (cpuCount, memory) = _sampler.Describe();

        Assert.Equal(2, cpuCount);
        Assert.True(memory > 1L << 30);
    }
}
