namespace Pithyx.Daemon.Monitoring;

/// <summary>Reads host name, uptime, load, CPU usage and memory from <c>/proc</c>.</summary>
public sealed class SystemInfoSampler
{
    private readonly Lock _lock = new();
    private (long Idle, long Total)? _previousCpu;

    public SystemInfo Sample()
    {
        var cpu = ProcParsers.ParseCpuTimes(File.ReadAllText("/proc/stat"));
        double? cpuPercent;
        lock (_lock)
        {
            cpuPercent = _previousCpu is { } previous ? ProcParsers.CpuPercent(previous, cpu) : null;
            _previousCpu = cpu;
        }
        var (total, available) = ProcParsers.ParseMemInfo(File.ReadAllText("/proc/meminfo"));
        return new SystemInfo(
            Environment.MachineName,
            ProcParsers.ParseUptime(File.ReadAllText("/proc/uptime")),
            ProcParsers.ParseLoadAverage(File.ReadAllText("/proc/loadavg")),
            cpuPercent,
            total,
            available);
    }
}
