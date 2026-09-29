namespace Pithyx.Daemon.Monitoring;

/// <summary>One second of the box, the event of <c>system/resources</c>. Rates are per second since the previous sample.</summary>
/// <param name="At">When it was measured (UTC).</param>
/// <param name="UptimeSeconds">Seconds since boot.</param>
/// <param name="LoadAverage">Over 1, 5 and 15 minutes.</param>
/// <param name="Cpu">Usage of all cores together and of each core.</param>
/// <param name="Memory">Physical memory and swap.</param>
/// <param name="Network">Every interface except the loopback and container links.</param>
/// <param name="Disks">Every whole disk (no partitions, zvols or loop devices).</param>
/// <param name="Temperatures">Every sensor found; empty where the hardware or a virtual machine has none.</param>
public sealed record ResourceSample(
    DateTimeOffset At,
    double UptimeSeconds,
    IReadOnlyList<double> LoadAverage,
    CpuUsage Cpu,
    MemoryUsage Memory,
    IReadOnlyList<NetworkRate> Network,
    IReadOnlyList<DiskRate> Disks,
    IReadOnlyList<TemperatureReading> Temperatures);

/// <param name="Percent">All cores together, 0 to 100.</param>
/// <param name="Cores">Each core, 0 to 100, in the kernel's order.</param>
public sealed record CpuUsage(double Percent, IReadOnlyList<double> Cores);

/// <summary>
/// Physical memory split the way the monitor draws it. On Linux the ZFS ARC is not part of the page cache, so the
/// kernel counts it as used; <paramref name="UsedBytes"/> leaves it out and <paramref name="ArcBytes"/> shows it.
/// </summary>
/// <param name="TotalBytes">Installed memory the kernel manages.</param>
/// <param name="UsedBytes">Used by programs and the kernel, without caches and without the ARC.</param>
/// <param name="ArcBytes">The ZFS ARC; null without ZFS. It shrinks when programs need the memory.</param>
/// <param name="ArcMaxBytes">The most the ARC may grow to; null without ZFS.</param>
/// <param name="CacheBytes">Page cache, buffers and reclaimable kernel memory.</param>
/// <param name="FreeBytes">Not used at all.</param>
/// <param name="AvailableBytes">What the kernel estimates new programs can get without swapping (the ARC not included).</param>
/// <param name="SwapTotalBytes">Swap space.</param>
/// <param name="SwapUsedBytes">Swap in use.</param>
public sealed record MemoryUsage(
    long TotalBytes,
    long UsedBytes,
    long? ArcBytes,
    long? ArcMaxBytes,
    long CacheBytes,
    long FreeBytes,
    long AvailableBytes,
    long SwapTotalBytes,
    long SwapUsedBytes);

/// <param name="Name">Interface name, for example <c>enp1s0</c>.</param>
/// <param name="ReceiveBytesPerSecond">Received.</param>
/// <param name="SendBytesPerSecond">Sent.</param>
public sealed record NetworkRate(string Name, long ReceiveBytesPerSecond, long SendBytesPerSecond);

/// <param name="Name">Kernel name, for example <c>sda</c> or <c>nvme0n1</c>.</param>
/// <param name="ReadBytesPerSecond">Read.</param>
/// <param name="WriteBytesPerSecond">Written.</param>
/// <param name="BusyPercent">Share of the second with at least one request in flight, 0 to 100.</param>
public sealed record DiskRate(string Name, long ReadBytesPerSecond, long WriteBytesPerSecond, double BusyPercent);

/// <param name="Chip">The hwmon chip or thermal zone type, for example <c>coretemp</c>, <c>nvme</c> or <c>acpitz</c>.</param>
/// <param name="Label">The sensor's label (<c>Package id 0</c>, <c>Composite</c>), or null when the driver gives none.</param>
/// <param name="Celsius">Current temperature.</param>
/// <param name="HighCelsius">The driver's high mark, if any.</param>
/// <param name="CriticalCelsius">The driver's critical mark, if any.</param>
public sealed record TemperatureReading(string Chip, string? Label, double Celsius, double? HighCelsius, double? CriticalCelsius);

/// <summary>Snapshot of <c>system/resources</c>: what the box is and the last hour, as far as someone watched it.</summary>
/// <param name="CpuCount">Cores the kernel lists.</param>
/// <param name="MemoryTotalBytes">Installed memory the kernel manages.</param>
/// <param name="History">The last hour in buckets; seconds nobody watched are missing.</param>
/// <param name="Latest">The newest sample, or null while the first one is being measured.</param>
public sealed record ResourceSnapshot(int CpuCount, long MemoryTotalBytes, ResourceHistory History, ResourceSample? Latest);

/// <summary>
/// The ring buffer of the last hour, averaged into buckets so the snapshot stays small. Every series has one entry per
/// time; a device that did not exist at a time has null there.
/// </summary>
/// <param name="BucketSeconds">Seconds per bucket.</param>
/// <param name="Times">End of each bucket (UTC).</param>
/// <param name="CpuPercent">All cores together.</param>
/// <param name="MemoryUsedBytes">Used without caches and ARC.</param>
/// <param name="ArcBytes">ZFS ARC; null without ZFS.</param>
/// <param name="Network">Received and sent bytes per second by interface.</param>
/// <param name="Disks">Read and written bytes per second by disk.</param>
public sealed record ResourceHistory(
    int BucketSeconds,
    IReadOnlyList<DateTimeOffset> Times,
    IReadOnlyList<double> CpuPercent,
    IReadOnlyList<long> MemoryUsedBytes,
    IReadOnlyList<long?> ArcBytes,
    IReadOnlyList<DeviceSeries> Network,
    IReadOnlyList<DeviceSeries> Disks);

/// <param name="Name">Interface or disk.</param>
/// <param name="In">Received or read, per second.</param>
/// <param name="Out">Sent or written, per second.</param>
public sealed record DeviceSeries(string Name, IReadOnlyList<long?> In, IReadOnlyList<long?> Out);

/// <summary>Snapshot and event of <c>system/processes</c>.</summary>
/// <param name="At">When the list was read (UTC).</param>
/// <param name="Processes">Every process except kernel threads.</param>
public sealed record ProcessList(DateTimeOffset At, IReadOnlyList<ProcessInfo> Processes);

/// <param name="Pid">Process id.</param>
/// <param name="Name">Command name as the kernel keeps it (at most 15 characters).</param>
/// <param name="User">Owner's user name, or the numeric id when <c>/etc/passwd</c> does not know it.</param>
/// <param name="CpuPercent">Share of all cores since the previous list, 0 to 100; null in the first list.</param>
/// <param name="MemoryBytes">Resident memory.</param>
public sealed record ProcessInfo(int Pid, string Name, string User, double? CpuPercent, long MemoryBytes);
