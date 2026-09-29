using Pithyx.Daemon.Monitoring;

namespace Pithyx.Daemon.Tests.Monitoring;

/// <summary>
/// Fixtures are real files from the Debian 13 dev VM (fixtures/proc), except fixtures/sys-handwritten and
/// fixtures/etc/passwd: the VM has no temperature sensors, so their layout follows the kernel's hwmon and thermal
/// sysfs documentation.
/// </summary>
public sealed class ResourceParsersTests
{
    private static string Fixture(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "fixtures", .. path]));

    [Fact]
    public void ReadsTheAggregateAndEveryCore()
    {
        var times = ResourceParsers.ParseCpuTimes(Fixture("proc", "stat"));

        Assert.Equal(5, times.Count);
        Assert.InRange(times.Skip(1).Sum(core => core.Total), times[0].Total - 50, times[0].Total + 50);
        Assert.All(times, core => Assert.InRange(core.Idle, 1, core.Total));
    }

    [Theory]
    [InlineData(100, 400, 150, 500, 50.0)]
    [InlineData(100, 400, 200, 500, 0.0)]
    [InlineData(100, 400, 100, 500, 100.0)]
    public void CpuPercentIsTheBusyShareBetweenTwoReadings(long idleBefore, long totalBefore, long idleAfter, long totalAfter, double percent) =>
        Assert.Equal(percent, ResourceParsers.CpuPercent(new CpuTimes(idleBefore, totalBefore), new CpuTimes(idleAfter, totalAfter)));

    [Fact]
    public void CpuPercentIsUnknownWhenTheCountersDidNotMoveOrWentBack()
    {
        Assert.Null(ResourceParsers.CpuPercent(new CpuTimes(1, 10), new CpuTimes(1, 10)));
        Assert.Null(ResourceParsers.CpuPercent(new CpuTimes(5, 10), new CpuTimes(1, 20)));
    }

    [Fact]
    public void ReadsMemoryInBytes()
    {
        var memory = ResourceParsers.ParseMemory(Fixture("proc", "meminfo"));

        Assert.True(memory.TotalBytes > 1L << 30);
        Assert.InRange(memory.FreeBytes, 1, memory.AvailableBytes);
        Assert.InRange(memory.CachedBytes, 1, memory.TotalBytes);
        Assert.True(memory.ReclaimableBytes > 0);
        Assert.Equal(0, memory.TotalBytes % 1024);
        Assert.InRange(memory.SwapFreeBytes, 0, memory.SwapTotalBytes);
    }

    [Fact]
    public void ReadsTheArcSizeAndItsLimit()
    {
        var arc = ResourceParsers.ParseArc(Fixture("proc", "spl", "kstat", "zfs", "arcstats"));

        Assert.True(arc.SizeBytes > 0);
        Assert.True(arc.MaxBytes >= arc.SizeBytes);
    }

    [Fact]
    public void ReadsEveryInterfaceAndShowsNeitherLoopbackNorContainerLinks()
    {
        var devices = ResourceParsers.ParseNetworkDevices(Fixture("proc", "net", "dev"));

        Assert.Equal(["lo", "ens18", "tailscale0", "docker0"], devices.Select(device => device.Name));
        Assert.Equal((3345293248L, 4392740168L), (devices[1].ReceivedBytes, devices[1].SentBytes));
        Assert.Equal(["ens18", "tailscale0", "docker0"], devices.Where(device => ResourceParsers.IsShownInterface(device.Name)).Select(device => device.Name));
        Assert.False(ResourceParsers.IsShownInterface("veth1a2b3c"));
    }

    [Fact]
    public void ReadsWholeDisksFromDiskstats()
    {
        var disks = ResourceParsers.ParseDiskStats(Fixture("proc", "diskstats")).Where(disk => ResourceParsers.IsWholeDisk(disk.Name)).ToList();

        Assert.Equal(["sda", "sdb", "sdd", "sde", "sdf", "sdg", "sdc"], disks.Select(disk => disk.Name));
        Assert.Equal((17755942L, 60378130L, 383488L), (disks[0].SectorsRead, disks[0].SectorsWritten, disks[0].IoMilliseconds));
    }

    [Theory]
    [InlineData("sda", true)]
    [InlineData("sdaa", true)]
    [InlineData("vdb", true)]
    [InlineData("nvme0n1", true)]
    [InlineData("mmcblk0", true)]
    [InlineData("md127", true)]
    [InlineData("sda1", false)]
    [InlineData("nvme0n1p2", false)]
    [InlineData("mmcblk0p1", false)]
    [InlineData("loop0", false)]
    [InlineData("sr0", false)]
    [InlineData("zd0", false)]
    [InlineData("dm-0", false)]
    public void TellsWholeDisksFromPartitionsAndVirtualDevices(string name, bool whole) =>
        Assert.Equal(whole, ResourceParsers.IsWholeDisk(name));

    [Fact]
    public void ReadsAProcessStatLine()
    {
        var stat = ResourceParsers.ParseProcessStat(Fixture("proc", "1", "stat"));

        Assert.Equal(new ProcessStat(1, "systemd", 4194560, 1764 + 828, 2718, 8), stat);
    }

    [Fact]
    public void AProcessNameMayContainParentheses()
    {
        var stat = ResourceParsers.ParseProcessStat(Fixture("proc", "2565", "stat"));

        Assert.Equal((2565, "(sd-pam)", 164L), (stat.Pid, stat.Name, stat.RssPages));
    }

    [Fact]
    public void RecognizesKernelThreads()
    {
        Assert.NotEqual(0, ResourceParsers.ParseProcessStat(Fixture("proc", "2", "stat")).Flags & ResourceParsers.KernelThreadFlag);
        Assert.Equal(0, ResourceParsers.ParseProcessStat(Fixture("proc", "1", "stat")).Flags & ResourceParsers.KernelThreadFlag);
    }

    [Fact]
    public void ReadsTheRealUserId()
    {
        Assert.Equal(1000, ResourceParsers.ParseUid(Fixture("proc", "2565", "status")));
        Assert.Equal(0, ResourceParsers.ParseUid(Fixture("proc", "1", "status")));
        Assert.Null(ResourceParsers.ParseUid("Name:\tgone\n"));
    }

    [Fact]
    public void ReadsUserNamesAndSkipsBrokenLines()
    {
        var names = ResourceParsers.ParsePasswd(Fixture("etc", "passwd"));

        Assert.Equal(new Dictionary<int, string> { [0] = "root", [1] = "daemon", [1000] = "owner" }, names);
    }

    [Theory]
    [InlineData("47000\n", 47.0)]
    [InlineData("38850", 38.9)]
    [InlineData("-5000\n", -5.0)]
    [InlineData("", null)]
    [InlineData("N/A\n", null)]
    public void ReadsMillidegrees(string content, double? celsius) => Assert.Equal(celsius, ResourceParsers.ParseMillidegrees(content));
}
