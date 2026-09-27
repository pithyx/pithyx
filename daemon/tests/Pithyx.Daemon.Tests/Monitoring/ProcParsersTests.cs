using Pithyx.Daemon.Monitoring;

namespace Pithyx.Daemon.Tests.Monitoring;

/// <summary>Fixtures are real /proc files from the Debian 13 dev VM.</summary>
public sealed class ProcParsersTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "proc", name));

    [Fact]
    public void ParsesUptime() => Assert.True(ProcParsers.ParseUptime(Fixture("uptime")) > 0);

    [Fact]
    public void ParsesThreeLoadAverages()
    {
        var load = ProcParsers.ParseLoadAverage(Fixture("loadavg"));

        Assert.Equal(3, load.Count);
        Assert.All(load, value => Assert.True(value >= 0));
    }

    [Fact]
    public void ParsesTotalAndAvailableMemory()
    {
        var (total, available) = ProcParsers.ParseMemInfo(Fixture("meminfo"));

        Assert.True(total > 1L << 30);
        Assert.InRange(available, 1, total);
        Assert.Equal(0, total % 1024);
    }

    [Fact]
    public void ParsesAggregateCpuTimes()
    {
        var (idle, total) = ProcParsers.ParseCpuTimes(Fixture("stat"));

        Assert.InRange(idle, 1, total);
    }

    [Theory]
    [InlineData(100, 1000, 150, 1100, 50.0)]
    [InlineData(100, 1000, 200, 1100, 0.0)]
    [InlineData(100, 1000, 100, 1100, 100.0)]
    public void ComputesCpuPercentBetweenSamples(long idle1, long total1, long idle2, long total2, double expected) =>
        Assert.Equal(expected, ProcParsers.CpuPercent((idle1, total1), (idle2, total2)));

    [Fact]
    public void CpuPercentIsNullWithoutElapsedTime() => Assert.Null(ProcParsers.CpuPercent((1, 10), (1, 10)));
}
