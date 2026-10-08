using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// Follows the journal of the kernel and the services the Log Center shows (<see cref="JournalParser.Units"/>) with
/// one long-running <c>journalctl --follow</c>, never a process per request. After a restart it continues at the
/// cursor stored with the last written batch, the first time with the current boot. Real mode only.
/// SMB and SFTP activity is handed to <see cref="FileServices.FileServiceActivity"/> on the way. App containers' output
/// (source <c>app/&lt;id&gt;</c>) follows <see cref="Keeps(PendingLogEntry, Apps.AppRegistry?, AppLogLimiter?)"/>.
/// </summary>
public sealed partial class JournalReader(
    ICommandRunner commands,
    LogStore store,
    TimeProvider time,
    ILogger<JournalReader> logger,
    FileServices.FileServiceActivity? activity = null,
    Apps.AppRegistry? apps = null,
    AppLogLimiter? limiter = null) : BackgroundService
{
    private static readonly TimeSpan _minDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _maxDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = _minDelay;
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
                    if (JournalParser.ParseWithIdentifier(line) is ({ } entry, var identifier))
                    {
                        cursor = entry.JournalCursor ?? cursor;
                        if (activity?.Observe(entry, identifier) != false && Keeps(entry))
                        {
                            store.Enqueue(entry);
                        }
                    }
                }
                // journalctl ended (rotated journal, missing binary): try again, slower each time it ends quickly.
                delay = time.GetUtcNow() - started > _maxDelay ? _minDelay : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxDelay.Ticks));
                LogJournalEnded(delay.TotalSeconds);
                await Task.Delay(delay, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the cursor of the last written batch is where the next run continues.
        }
    }

    private bool Keeps(PendingLogEntry entry) => Keeps(entry, apps, limiter);

    /// <summary>
    /// Whether an entry goes to the Log Center: every entry that is not an app's; an app's only while the app is
    /// installed or being installed (its backend starts before the registry names it, and that output is what an
    /// administrator needs when the install fails), its debug lines only while the app's debug switch is on, and all of
    /// it within the app's rate limit.
    /// </summary>
    internal static bool Keeps(PendingLogEntry entry, Apps.AppRegistry? apps, AppLogLimiter? limiter)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.Source.StartsWith(LogSources.AppPrefix, StringComparison.Ordinal))
        {
            return true;
        }
        var appId = entry.Source[LogSources.AppPrefix.Length..];
        var app = apps?.Find(appId);
        if (app is null && apps?.IsBusy(appId) != true)
        {
            return false;
        }
        if (entry.Level == LogLevelName.Debug && app?.LogLevel != Apps.AppLogLevel.Debug)
        {
            return false;
        }
        return limiter is null || limiter.Take(appId, 1) == 1;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "journalctl stopped; following the journal again in {Seconds} seconds")]
    private partial void LogJournalEnded(double seconds);
}
