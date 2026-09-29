namespace Pithyx.Daemon.Monitoring;

/// <summary>Where the resource monitor reads: the real <c>/proc</c>, <c>/sys</c> and <c>/etc/passwd</c>, or fixtures in tests.</summary>
public sealed record ResourceFiles(string Proc = "/proc", string Sys = "/sys", string Passwd = "/etc/passwd");

/// <summary>
/// Reads CPU, memory, ARC, network, disk and temperature counters and turns two readings into a
/// <see cref="ResourceSample"/>. Only files are read; no process is started and no disk is touched: disk counters
/// come from <c>/proc/diskstats</c>, and hwmon chips of the <c>drivetemp</c> driver, which ask the disk itself, are
/// skipped.
/// </summary>
public sealed class ResourceSampler(ResourceFiles files, TimeProvider time)
{
    private readonly Lock _lock = new();
    private Reading? _previous;
    private IReadOnlyList<SensorFiles>? _sensors;

    /// <summary>How many samples were taken; the tests check that nobody watching costs nothing.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Starts over: the next <see cref="Sample"/> only reads the counters it compares the one after with.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _previous = null;
            // Sensors are looked up again whenever someone starts watching; they rarely come and go.
            _sensors = null;
        }
    }

    /// <summary>The rates since the previous call, or null on the first call after <see cref="Reset"/>.</summary>
    public ResourceSample? Sample()
    {
        lock (_lock)
        {
            SampleCount++;
            var current = Read();
            var previous = _previous;
            _previous = current;
            return previous is null ? null : Compare(previous, current);
        }
    }

    /// <summary>Number of cores and installed memory, for the snapshot.</summary>
    public (int CpuCount, long MemoryTotalBytes) Describe()
    {
        var cores = ResourceParsers.ParseCpuTimes(File.ReadAllText(Path.Combine(files.Proc, "stat"))).Count - 1;
        return (cores, ResourceParsers.ParseMemory(File.ReadAllText(Path.Combine(files.Proc, "meminfo"))).TotalBytes);
    }

    private sealed record Reading(
        long Timestamp,
        DateTimeOffset At,
        IReadOnlyList<CpuTimes> Cpu,
        MemoryCounters Memory,
        ArcCounters? Arc,
        IReadOnlyList<NetworkCounters> Network,
        IReadOnlyList<DiskCounters> Disks,
        double UptimeSeconds,
        IReadOnlyList<double> LoadAverage);

    /// <summary>The files of one temperature sensor, found once per <see cref="Reset"/>.</summary>
    private sealed record SensorFiles(string Chip, string Input, string? Label, string? High, string? Critical);

    private Reading Read()
    {
        var arcPath = Path.Combine(files.Proc, "spl", "kstat", "zfs", "arcstats");
        return new Reading(
            time.GetTimestamp(),
            time.GetUtcNow(),
            ResourceParsers.ParseCpuTimes(ReadProc("stat")),
            ResourceParsers.ParseMemory(ReadProc("meminfo")),
            File.Exists(arcPath) ? ResourceParsers.ParseArc(File.ReadAllText(arcPath)) : null,
            [.. ResourceParsers.ParseNetworkDevices(ReadProc(Path.Combine("net", "dev"))).Where(device => ResourceParsers.IsShownInterface(device.Name))],
            [.. ResourceParsers.ParseDiskStats(ReadProc("diskstats")).Where(disk => ResourceParsers.IsWholeDisk(disk.Name))],
            ProcParsers.ParseUptime(ReadProc("uptime")),
            ProcParsers.ParseLoadAverage(ReadProc("loadavg")));
    }

    private ResourceSample Compare(Reading previous, Reading current)
    {
        var seconds = Math.Max(time.GetElapsedTime(previous.Timestamp, current.Timestamp).TotalSeconds, 0.001);
        long PerSecond(long before, long after) => after < before ? 0 : (long)Math.Round((after - before) / seconds);

        var cores = current.Cpu.Skip(1)
            .Select((core, index) => index + 1 < previous.Cpu.Count ? ResourceParsers.CpuPercent(previous.Cpu[index + 1], core) ?? 0 : 0)
            .ToList();
        var memory = current.Memory;
        var cache = memory.BuffersBytes + memory.CachedBytes + memory.ReclaimableBytes;
        var arc = current.Arc?.SizeBytes;
        var used = Math.Max(0, memory.TotalBytes - memory.FreeBytes - cache - (arc ?? 0));

        var network = current.Network.Select(device =>
            previous.Network.FirstOrDefault(before => before.Name == device.Name) is { } before
                ? new NetworkRate(device.Name, PerSecond(before.ReceivedBytes, device.ReceivedBytes), PerSecond(before.SentBytes, device.SentBytes))
                : new NetworkRate(device.Name, 0, 0));
        var disks = current.Disks.Select(disk =>
            previous.Disks.FirstOrDefault(before => before.Name == disk.Name) is { } before
                ? new DiskRate(
                    disk.Name,
                    PerSecond(before.SectorsRead * 512, disk.SectorsRead * 512),
                    PerSecond(before.SectorsWritten * 512, disk.SectorsWritten * 512),
                    Math.Round(Math.Clamp((disk.IoMilliseconds - before.IoMilliseconds) / (seconds * 10), 0, 100), 1))
                : new DiskRate(disk.Name, 0, 0, 0));

        return new ResourceSample(
            current.At,
            current.UptimeSeconds,
            current.LoadAverage,
            new CpuUsage(current.Cpu.Count > 0 && previous.Cpu.Count > 0 ? ResourceParsers.CpuPercent(previous.Cpu[0], current.Cpu[0]) ?? 0 : 0, cores),
            new MemoryUsage(
                memory.TotalBytes,
                used,
                arc,
                current.Arc?.MaxBytes,
                cache,
                memory.FreeBytes,
                memory.AvailableBytes,
                memory.SwapTotalBytes,
                memory.SwapTotalBytes - memory.SwapFreeBytes),
            [.. network],
            [.. disks],
            ReadTemperatures());
    }

    private List<TemperatureReading> ReadTemperatures()
    {
        _sensors ??= FindSensors();
        var readings = new List<TemperatureReading>();
        foreach (var sensor in _sensors)
        {
            if (Millidegrees(sensor.Input) is not { } celsius)
            {
                continue;
            }
            readings.Add(new TemperatureReading(
                sensor.Chip,
                sensor.Label is null ? null : TryRead(sensor.Label)?.Trim(),
                celsius,
                sensor.High is null ? null : Millidegrees(sensor.High),
                sensor.Critical is null ? null : Millidegrees(sensor.Critical)));
        }
        return readings;
    }

    /// <summary>
    /// Every <c>temp*_input</c> of every hwmon chip except <c>drivetemp</c>, then the thermal zones whose type no
    /// hwmon chip already reports (for example <c>x86_pkg_temp</c>).
    /// </summary>
    private List<SensorFiles> FindSensors()
    {
        var sensors = new List<SensorFiles>();
        var hwmon = Path.Combine(files.Sys, "class", "hwmon");
        foreach (var chip in Directories(hwmon))
        {
            var name = TryRead(Path.Combine(chip, "name"))?.Trim();
            if (name is null or "drivetemp")
            {
                continue;
            }
            foreach (var input in Directory.EnumerateFiles(chip, "temp*_input").Order(StringComparer.Ordinal))
            {
                var prefix = input[..^"_input".Length];
                sensors.Add(new SensorFiles(name, input, Existing(prefix + "_label"), Existing(prefix + "_max"), Existing(prefix + "_crit")));
            }
        }
        var chips = sensors.Select(sensor => sensor.Chip).ToHashSet(StringComparer.Ordinal);
        foreach (var zone in Directories(Path.Combine(files.Sys, "class", "thermal")).Where(path => Path.GetFileName(path).StartsWith("thermal_zone", StringComparison.Ordinal)))
        {
            var type = TryRead(Path.Combine(zone, "type"))?.Trim();
            if (type is not null && !chips.Contains(type) && Existing(Path.Combine(zone, "temp")) is { } temp)
            {
                sensors.Add(new SensorFiles(type, temp, null, null, null));
            }
        }
        return sensors;
    }

    private static IEnumerable<string> Directories(string path) =>
        Directory.Exists(path) ? Directory.EnumerateDirectories(path).Order(StringComparer.Ordinal) : [];

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static double? Millidegrees(string path) => TryRead(path) is { } content ? ResourceParsers.ParseMillidegrees(content) : null;

    /// <summary>Sensor files can vanish or fail to read (a driver unloaded, a device asleep); that sensor is left out.</summary>
    private static string? TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string ReadProc(string name) => File.ReadAllText(Path.Combine(files.Proc, name));
}
