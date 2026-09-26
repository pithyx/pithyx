namespace Vyxis.Daemon.Monitoring;

/// <param name="Hostname">The host name of the box.</param>
/// <param name="UptimeSeconds">Seconds since boot.</param>
/// <param name="LoadAverage">Over 1, 5 and 15 minutes.</param>
/// <param name="CpuPercent">Usage since the previous sample; null for the very first one.</param>
/// <param name="MemoryTotalBytes">Physical memory.</param>
/// <param name="MemoryAvailableBytes">Memory available for new work without swapping, including reclaimable caches.</param>
public sealed record SystemInfo(
    string Hostname,
    double UptimeSeconds,
    IReadOnlyList<double> LoadAverage,
    double? CpuPercent,
    long MemoryTotalBytes,
    long MemoryAvailableBytes);
