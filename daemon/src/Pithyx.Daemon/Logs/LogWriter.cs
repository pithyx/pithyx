using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// Writes the queued entries in batches (at most one write a second, or one per <see cref="MaxBatch"/> entries), and
/// keeps the current logs and the archive within their retention. Before the current logs drop entries the archive has
/// not copied yet, the archive gets them first. Problems writing the logs go to stderr (the journal), never back into
/// the queue.
/// </summary>
public sealed class LogWriter(LogStore store, LogArchive archive, LogService logs, AppLogLimiter limiter, TimeProvider time) : BackgroundService
{
    public const int MaxBatch = 1000;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(10);

    private DateTimeOffset _maintained = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = store.Pending;
        var batch = new List<PendingLogEntry>(MaxBatch);
        try
        {
            while (true)
            {
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    idle.CancelAfter(MaintenanceInterval);
                    try
                    {
                        await reader.WaitToReadAsync(idle.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Nothing was logged for a while; maintenance still runs.
                    }
                }
                // Real time, not the daemon's clock: batching is about disk writes, not about what time it is.
                await Task.Delay(FlushInterval, stoppingToken);
                await FlushAsync(reader, batch, stoppingToken);
                if (time.GetUtcNow() - _maintained >= MaintenanceInterval)
                {
                    await MaintainAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            store.Complete();
            await FlushAsync(reader, batch, CancellationToken.None);
        }
    }

    private async Task FlushAsync(System.Threading.Channels.ChannelReader<PendingLogEntry> reader, List<PendingLogEntry> batch, CancellationToken cancellationToken)
    {
        foreach (var (app, dropped) in limiter.TakeReports())
        {
            store.Enqueue(Warning(FormattableString.Invariant($"The app {app} logged faster than its limit; {dropped} entries were dropped."), new() { ["app"] = app, ["dropped"] = dropped.ToString(CultureInfo.InvariantCulture) }));
        }
        if (store.TakeDropped() is > 0 and var lost)
        {
            store.Enqueue(Warning(FormattableString.Invariant($"The log queue was full; {lost} entries were dropped."), new() { ["dropped"] = lost.ToString(CultureInfo.InvariantCulture) }));
        }
        while (true)
        {
            batch.Clear();
            while (batch.Count < MaxBatch && reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }
            if (batch.Count == 0)
            {
                return;
            }
            try
            {
                await store.WriteAsync(batch, cancellationToken);
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                await Console.Error.WriteLineAsync($"pithyxd: could not write {batch.Count} log entries to {store.Path}: {exception.Message}");
                return;
            }
        }
    }

    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        _maintained = time.GetUtcNow();
        try
        {
            var settings = await logs.SettingsAsync(cancellationToken);
            var notBefore = _maintained - TimeSpan.FromDays(settings.RetentionDays);
            await using var connection = await store.OpenAsync(cancellationToken);
            var (bytes, oldest) = await LogDatabase.StatusAsync(connection);
            var pruneDue = bytes > settings.MaxBytes || oldest < notBefore;
            // Copy first what pruning would take; otherwise once a day.
            await archive.SyncAsync(settings, force: pruneDue, cancellationToken);
            if (pruneDue)
            {
                await LogDatabase.PruneAsync(connection, notBefore, settings.MaxBytes);
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"pithyxd: log maintenance failed: {exception.Message}");
        }
    }

    private static PendingLogEntry Warning(string message, Dictionary<string, string> data) =>
        new(DateTimeOffset.UtcNow, LogLevelName.Warn, LogSources.Daemon, message, Data: data);
}
