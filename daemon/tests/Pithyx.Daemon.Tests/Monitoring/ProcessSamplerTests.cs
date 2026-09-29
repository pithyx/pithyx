using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Monitoring;

namespace Pithyx.Daemon.Tests.Monitoring;

public sealed class ProcessSamplerTests : IDisposable
{
    private static readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private readonly string _proc = Directory.CreateTempSubdirectory("pithyx-processes-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_proc, recursive: true);

    private ProcessSampler Sampler(string proc) => new(new ResourceFiles(proc, Passwd: Path.Combine(_fixtures, "etc", "passwd")), _time);

    /// <summary>A process whose stat has the given CPU ticks (utime) and resident pages, owned by <paramref name="uid"/>.</summary>
    private void Process(int pid, string name, long ticks, int uid = 0, long flags = 4194560)
    {
        var directory = Path.Combine(_proc, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stat"), $"{pid} ({name}) S 1 1 1 0 -1 {flags} 0 0 0 0 {ticks} 0 0 0 20 0 1 0 500 1000 10 18446744073709551615 0 0 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0 0\n");
        File.WriteAllText(Path.Combine(directory, "status"), $"Name:\t{name}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\n");
    }

    private void Total(long jiffies) => File.WriteAllText(Path.Combine(_proc, "stat"), $"cpu  {jiffies} 0 0 0 0 0 0 0 0 0\ncpu0 {jiffies} 0 0 0 0 0 0 0 0 0\n");

    [Fact]
    public void ListsProcessesOfTheDevVmWithoutKernelThreads()
    {
        var list = Sampler(Path.Combine(_fixtures, "proc")).Sample();

        Assert.Equal(
            [
                new ProcessInfo(1, "systemd", "root", null, 2718L * Environment.SystemPageSize),
                new ProcessInfo(2565, "(sd-pam)", "owner", null, 164L * Environment.SystemPageSize),
            ],
            list.Processes.OrderBy(process => process.Pid));
        Assert.Equal(_time.GetUtcNow(), list.At);
    }

    [Fact]
    public void CpuIsTheShareOfAllCoresSinceThePreviousList()
    {
        Total(1_000);
        Process(10, "busy", ticks: 100);
        Process(11, "idle", ticks: 100, uid: 4242);
        var sampler = Sampler(_proc);
        sampler.Sample();
        Total(1_400);
        Process(10, "busy", ticks: 200);
        Process(12, "new", ticks: 50);

        var list = sampler.Sample().Processes.OrderBy(process => process.Pid).ToList();

        Assert.Equal([(10, 25.0), (11, 0.0), (12, null)], list.Select(process => (process.Pid, process.CpuPercent)));
        Assert.Equal("4242", list[1].User);
    }

    [Fact]
    public void ResetForgetsThePreviousList()
    {
        Total(1_000);
        Process(10, "busy", ticks: 100);
        var sampler = Sampler(_proc);
        sampler.Sample();
        Total(1_400);
        Process(10, "busy", ticks: 200);
        sampler.Reset();

        Assert.Null(Assert.Single(sampler.Sample().Processes).CpuPercent);
    }

    [Fact]
    public void AReusedPidIsANewProcess()
    {
        Total(1_000);
        Process(10, "first", ticks: 100);
        var sampler = Sampler(_proc);
        sampler.Sample();
        Total(1_400);
        // Same pid, other start time: a different process whose ticks must not be compared with the first one's.
        File.WriteAllText(Path.Combine(_proc, "10", "stat"), "10 (second) S 1 1 1 0 -1 4194560 0 0 0 0 5 0 0 0 20 0 1 0 900 1000 10 0 0 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0 0\n");

        var process = Assert.Single(sampler.Sample().Processes);

        Assert.Equal(("second", (double?)null), (process.Name, process.CpuPercent));
    }
}
