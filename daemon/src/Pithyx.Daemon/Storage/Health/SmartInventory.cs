using System.Collections.Concurrent;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Health;

/// <summary>
/// The SMART state of every disk as last read. <see cref="StorageHealthMonitor"/> reads it at startup, after udev
/// reports a disk change and around scheduled self-tests; requests only read this memory.
/// </summary>
public sealed partial class SmartInventory(ICommandRunner commands, TimeProvider time, ILogger<SmartInventory> logger)
{
    private readonly ConcurrentDictionary<string, SmartReport> _reports = new(StringComparer.Ordinal);

    public IReadOnlyList<SmartReport> Current => [.. _reports.Values.OrderBy(report => report.DiskId, StringComparer.Ordinal)];

    public SmartReport? Of(string diskId) => _reports.GetValueOrDefault(diskId);

    public void Remove(IEnumerable<string> diskIds)
    {
        ArgumentNullException.ThrowIfNull(diskIds);
        foreach (var id in diskIds)
        {
            _reports.TryRemove(id, out _);
        }
    }

    /// <summary>Reads one disk; true when its report changed. A disk that is asleep keeps its last values.</summary>
    public async Task<bool> ReadAsync(Disk disk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var previous = Of(disk.Id);
        var now = time.GetUtcNow();
        SmartReading reading;
        if (SmartCommands.IsVirtio(disk))
        {
            reading = new SmartReading(SmartOutcome.Unsupported, Reason: "virtual");
        }
        else
        {
            try
            {
                var result = await commands.RunAsync(SmartCommands.Read(disk.Path), cancellationToken);
                reading = SmartParser.Parse(result.StandardOutput, result.ExitCode);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // smartctl is missing or hung; the disk is reported as unknown instead of failing the others.
                LogReadFailed(disk.Path, exception);
                reading = new SmartReading(SmartOutcome.Failed);
            }
        }
        var report = Merge(disk.Id, previous, reading, now);
        _reports[disk.Id] = report;
        // CheckedAt changes on every read; only a change of what the page shows counts.
        return previous is null || previous with { CheckedAt = now } != report;
    }

    /// <summary>Turns a reading into a report, keeping the last values of a disk that was found asleep.</summary>
    public static SmartReport Merge(string diskId, SmartReport? previous, SmartReading reading, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Outcome switch
        {
            SmartOutcome.Asleep when previous is { ReadAt: not null } => previous with { Asleep = true, CheckedAt = now },
            SmartOutcome.Asleep => Empty(diskId, "unknown", "asleep", now) with { Asleep = true },
            SmartOutcome.Unsupported => Empty(diskId, "unsupported", reading.Reason ?? "not_available", now),
            SmartOutcome.Failed => Empty(diskId, "unknown", "read_failed", now),
            _ => new SmartReport(
                diskId,
                reading.State ?? "passed",
                null,
                false,
                reading.TemperatureCelsius,
                reading.PowerOnHours,
                reading.ReallocatedSectors,
                reading.PendingSectors,
                reading.UncorrectableSectors,
                reading.MediaErrors,
                reading.CriticalWarning,
                reading.LastSelfTest,
                now,
                now),
        };
    }

    private static SmartReport Empty(string diskId, string state, string reason, DateTimeOffset now) =>
        new(diskId, state, reason, false, null, null, null, null, null, null, null, null, null, now);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading SMART of {Path} failed")]
    private partial void LogReadFailed(string path, Exception exception);
}
