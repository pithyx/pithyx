using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// Follows the journal of the kernel and the services the Log Center shows (<see cref="JournalParser.Units"/>) with
/// one long-running <c>journalctl --follow</c>, never a process per request. After a restart it continues at the
/// cursor stored with the last written batch, the first time with the current boot. Real mode only.
/// </summary>
public sealed partial class JournalReader(ICommandRunner commands, LogStore store, TimeProvider time, ILogger<JournalReader> logger) : BackgroundService
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = MinDelay;
        string? cursor = null;
        try
        {
            cursor = await store.JournalCursorAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var started = time.GetUtcNow();
                var request = new CommandRequest("journalctl", JournalParser.FollowArguments(cursor));
                await foreach (var line in commands.StreamLinesAsync(request, stoppingToken))
                {
                    if (JournalParser.Parse(line) is { } entry)
                    {
                        cursor = entry.JournalCursor ?? cursor;
                        store.Enqueue(entry);
                    }
                }
                // journalctl ended (rotated journal, missing binary): try again, slower each time it ends quickly.
                delay = time.GetUtcNow() - started > MaxDelay ? MinDelay : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
                LogJournalEnded(delay.TotalSeconds);
                await Task.Delay(delay, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the cursor of the last written batch is where the next run continues.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "journalctl stopped; following the journal again in {Seconds} seconds")]
    private partial void LogJournalEnded(double seconds);
}
