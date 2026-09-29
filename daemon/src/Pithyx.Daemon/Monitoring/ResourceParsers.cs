using System.Globalization;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Monitoring;

/// <summary>Idle and total jiffies of one CPU line of <c>/proc/stat</c>.</summary>
public readonly record struct CpuTimes(long Idle, long Total);

/// <summary>What <c>/proc/meminfo</c> says, in bytes.</summary>
public sealed record MemoryCounters(
    long TotalBytes,
    long FreeBytes,
    long AvailableBytes,
    long BuffersBytes,
    long CachedBytes,
    long ReclaimableBytes,
    long SwapTotalBytes,
    long SwapFreeBytes);

/// <summary>The ZFS ARC from <c>/proc/spl/kstat/zfs/arcstats</c>: its size now and the most it may grow to.</summary>
public sealed record ArcCounters(long SizeBytes, long MaxBytes);

/// <summary>Byte counters of one interface since boot, from <c>/proc/net/dev</c>.</summary>
public sealed record NetworkCounters(string Name, long ReceivedBytes, long SentBytes);

/// <summary>Counters of one block device since boot, from <c>/proc/diskstats</c>.</summary>
/// <param name="Name">Kernel name, for example <c>sda</c>.</param>
/// <param name="SectorsRead">512-byte sectors read.</param>
/// <param name="SectorsWritten">512-byte sectors written.</param>
/// <param name="IoMilliseconds">Milliseconds with at least one request in flight.</param>
public sealed record DiskCounters(string Name, long SectorsRead, long SectorsWritten, long IoMilliseconds);

/// <summary>The fields of <c>/proc/&lt;pid&gt;/stat</c> the process list needs.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">The command name (<c>comm</c>, at most 15 characters), without the parentheses.</param>
/// <param name="Flags">Kernel flags; <see cref="ResourceParsers.KernelThreadFlag"/> marks kernel threads.</param>
/// <param name="CpuTicks">User and system time in clock ticks.</param>
/// <param name="RssPages">Resident pages.</param>
/// <param name="StartTicks">Start time after boot in clock ticks; with the pid it identifies a process.</param>
public sealed record ProcessStat(int Pid, string Name, long Flags, long CpuTicks, long RssPages, long StartTicks);

/// <summary>
/// Pure parsers for the <c>/proc</c> and <c>/sys</c> files the resource monitor reads. None of them touches a disk:
/// <c>/proc/diskstats</c> holds kernel counters, and reading it never wakes a sleeping disk.
/// </summary>
public static partial class ResourceParsers
{
    /// <summary>PF_KTHREAD in <c>include/linux/sched.h</c>.</summary>
    public const long KernelThreadFlag = 0x00200000;

    /// <summary>
    /// The CPU lines of <c>/proc/stat</c>: the aggregate first, then every core in the order the kernel lists them.
    /// Idle includes iowait; guest time is already part of user time and is not counted twice.
    /// </summary>
    public static IReadOnlyList<CpuTimes> ParseCpuTimes(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new List<CpuTimes>();
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
            {
                continue;
            }
            var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(Long).ToArray();
            result.Add(new CpuTimes(values[3] + (values.Length > 4 ? values[4] : 0), values.Take(8).Sum()));
        }
        return result;
    }

    /// <summary>Usage in percent between two readings, or null when no time passed (or the counters went back).</summary>
    public static double? CpuPercent(CpuTimes previous, CpuTimes current)
    {
        var total = current.Total - previous.Total;
        var idle = current.Idle - previous.Idle;
        return total <= 0 || idle < 0 ? null : Math.Round(Math.Clamp(100.0 * (total - idle) / total, 0, 100), 1);
    }

    public static MemoryCounters ParseMemory(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].EndsWith(':'))
            {
                // Every value in meminfo is in KiB (the unit column says kB).
                values[parts[0][..^1]] = Long(parts[1]) * 1024;
            }
        }
        long Value(string key) => values.GetValueOrDefault(key);
        return new MemoryCounters(
            Value("MemTotal"),
            Value("MemFree"),
            Value("MemAvailable"),
            Value("Buffers"),
            Value("Cached"),
            Value("SReclaimable"),
            Value("SwapTotal"),
            Value("SwapFree"));
    }

    /// <summary><c>arcstats</c>: <c>size</c> and <c>c_max</c> in bytes.</summary>
    public static ArcCounters ParseArc(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        long size = 0, max = 0;
        // name type data; the first two lines are the kstat header.
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                continue;
            }
            switch (parts[0])
            {
                case "size":
                    size = Long(parts[2]);
                    break;
                case "c_max":
                    max = Long(parts[2]);
                    break;
                default:
                    break;
            }
        }
        return new ArcCounters(size, max);
    }

    /// <summary><c>/proc/net/dev</c>: received and sent bytes per interface.</summary>
    public static IReadOnlyList<NetworkCounters> ParseNetworkDevices(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new List<NetworkCounters>();
        foreach (var line in content.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || line.Contains('|', StringComparison.Ordinal))
            {
                continue;
            }
            var values = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Receive: bytes packets errs drop fifo frame compressed multicast, then transmit starting with bytes.
            result.Add(new NetworkCounters(line[..colon].Trim(), Long(values[0]), Long(values[8])));
        }
        return result;
    }

    /// <summary>Interfaces the monitor shows: not the loopback and not the host ends of container links.</summary>
    public static bool IsShownInterface(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name != "lo" && !name.StartsWith("veth", StringComparison.Ordinal);
    }

    /// <summary><c>/proc/diskstats</c>: every block device with its sector and busy-time counters.</summary>
    public static IReadOnlyList<DiskCounters> ParseDiskStats(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new List<DiskCounters>();
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 14)
            {
                continue;
            }
            // major minor name reads merged sectors ms writes merged sectors ms in-flight io-ms weighted-ms ...
            result.Add(new DiskCounters(parts[2], Long(parts[5]), Long(parts[9]), Long(parts[12])));
        }
        return result;
    }

    /// <summary>
    /// Whole disks: SATA, SAS, virtio, Xen, NVMe namespaces, MMC and md arrays. Partitions, loop devices, optical
    /// drives, zvols (<c>zd</c>) and device-mapper targets are left out.
    /// </summary>
    public static bool IsWholeDisk(string name) => WholeDisk().IsMatch(name);

    /// <summary>One <c>/proc/&lt;pid&gt;/stat</c> line. The name may contain spaces and parentheses; it ends at the last <c>)</c>.</summary>
    public static ProcessStat ParseProcessStat(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var open = content.IndexOf('(', StringComparison.Ordinal);
        var close = content.LastIndexOf(')');
        var pid = int.Parse(content[..open].Trim(), CultureInfo.InvariantCulture);
        // The fields after the name start with the state (field 3); flags is field 9, utime 14, stime 15, starttime 22, rss 24.
        var fields = content[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long Field(int number) => Long(fields[number - 3]);
        return new ProcessStat(pid, content[(open + 1)..close], Field(9), Field(14) + Field(15), Field(24), Field(22));
    }

    /// <summary>The real user id from the <c>Uid:</c> line of <c>/proc/&lt;pid&gt;/status</c>.</summary>
    public static int? ParseUid(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var line = content.Split('\n').FirstOrDefault(candidate => candidate.StartsWith("Uid:", StringComparison.Ordinal));
        return line is null ? null : int.Parse(line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
    }

    /// <summary><c>/etc/passwd</c>: user names by id. Lines that do not parse are skipped.</summary>
    public static IReadOnlyDictionary<int, string> ParsePasswd(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new Dictionary<int, string>();
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(':');
            if (parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
            {
                result.TryAdd(uid, parts[0]);
            }
        }
        return result;
    }

    /// <summary>A hwmon or thermal zone temperature file: millidegrees Celsius; null when it holds no number.</summary>
    public static double? ParseMillidegrees(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return long.TryParse(content.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value / 1000.0, 1, MidpointRounding.AwayFromZero)
            : null;
    }

    private static long Long(string value) => long.Parse(value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?:(?:sd|vd|xvd|hd)[a-z]+|nvme\d+n\d+|mmcblk\d+|md\d+)$")]
    private static partial Regex WholeDisk();
}
