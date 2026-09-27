using System.Globalization;

namespace Pithyx.Daemon.Monitoring;

/// <summary>Parsers for the <c>/proc</c> files behind <c>system/info</c>. Reading them spawns no process.</summary>
public static class ProcParsers
{
    /// <summary><c>/proc/uptime</c>: seconds since boot.</summary>
    public static double ParseUptime(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return double.Parse(content.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
    }

    /// <summary><c>/proc/loadavg</c>: load average over 1, 5 and 15 minutes.</summary>
    public static IReadOnlyList<double> ParseLoadAverage(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return [.. content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3).Select(v => double.Parse(v, CultureInfo.InvariantCulture))];
    }

    /// <summary><c>/proc/meminfo</c>: total and available memory in bytes.</summary>
    public static (long TotalBytes, long AvailableBytes) ParseMemInfo(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        long total = 0, available = 0;
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }
            var kib = long.Parse(parts[1], CultureInfo.InvariantCulture);
            switch (parts[0])
            {
                case "MemTotal:":
                    total = kib * 1024;
                    break;
                case "MemAvailable:":
                    available = kib * 1024;
                    break;
                default:
                    break;
            }
        }
        return (total, available);
    }

    /// <summary>The aggregate <c>cpu</c> line of <c>/proc/stat</c>: idle (including iowait) and total jiffies.</summary>
    public static (long Idle, long Total) ParseCpuTimes(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var line = content.Split('\n').First(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        // user nice system idle iowait irq softirq steal guest guest_nice; guest time is already part of user.
        var total = values.Take(8).Sum();
        return (values[3] + values[4], total);
    }

    /// <summary>CPU usage in percent between two samples, or null if no time passed.</summary>
    public static double? CpuPercent((long Idle, long Total) previous, (long Idle, long Total) current)
    {
        var total = current.Total - previous.Total;
        return total <= 0 ? null : Math.Round(100.0 * (total - (current.Idle - previous.Idle)) / total, 1);
    }
}
