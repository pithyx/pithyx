using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Backups;

/// <summary>
/// Starts the enabled jobs' runs and checks on their schedules (cron in the box's time zone) and tells administrators
/// about overdue jobs (D415). It sleeps until the next time something is due or a job changes; a time missed while
/// the box was off is not caught up, the overdue warning covers it.
/// </summary>
public sealed partial class BackupScheduler(BackupJobService jobs, TimeProvider time, ILogger<BackupScheduler> logger) : BackgroundService
{
    /// <summary>The longest sleep, so a changed time zone or clock is seen within an hour.</summary>
    public static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    private readonly Dictionary<(long Id, BackupRunKind Kind), (string Cron, DateTimeOffset Next)> _next = [];
    private readonly Lock _wakeLock = new();
    private readonly SemaphoreSlim _ticking = new(1, 1);
    private CancellationTokenSource _wake = new();

    public override void Dispose()
    {
        _wake.Dispose();
        _ticking.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        jobs.Changed += Wake;
        try
        {
            try
            {
                await jobs.RecoverAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogTickFailed(exception);
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                CancellationToken wake;
                lock (_wakeLock)
                {
                    wake = _wake.Token;
                }
                var due = await TickAsync(stoppingToken);
                var sleep = due - time.GetUtcNow();
                sleep = sleep < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : sleep > MaxSleep ? MaxSleep : sleep;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, wake);
                try
                {
                    await Task.Delay(sleep, time, linked.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    lock (_wakeLock)
                    {
                        _wake.Dispose();
                        _wake = new CancellationTokenSource();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            jobs.Changed -= Wake;
        }
    }

    /// <summary>Starts what is due now and returns when the next thing is due.</summary>
    public async Task<DateTimeOffset> TickAsync(CancellationToken cancellationToken)
    {
        await _ticking.WaitAsync(cancellationToken);
        var now = time.GetUtcNow();
        var due = now + MaxSleep;
        try
        {
            var schedules = await jobs.SchedulesAsync(cancellationToken);
            var seen = new HashSet<(long, BackupRunKind)>();
            foreach (var (id, schedule, checkSchedule) in schedules)
            {
                foreach (var (kind, cron) in new[] { (BackupRunKind.Backup, schedule), (BackupRunKind.Check, checkSchedule) })
                {
                    if (cron is null)
                    {
                        continue;
                    }
                    var key = (id, kind);
                    seen.Add(key);
                    if (!_next.TryGetValue(key, out var entry) || entry.Cron != cron)
                    {
                        entry = (cron, CronSchedule.Parse(cron).Next(now, TimeZoneInfo.Local) ?? DateTimeOffset.MaxValue);
                    }
                    else if (entry.Next <= now)
                    {
                        await StartAsync(id, kind, cancellationToken);
                        entry = (cron, CronSchedule.Parse(cron).Next(now, TimeZoneInfo.Local) ?? DateTimeOffset.MaxValue);
                    }
                    _next[key] = entry;
                    due = entry.Next < due ? entry.Next : due;
                }
            }
            foreach (var key in _next.Keys.Where(key => !seen.Contains(key)).ToList())
            {
                _next.Remove(key);
            }
            if (await jobs.NotifyOverdueAsync(cancellationToken) is { } overdue && overdue < due)
            {
                due = overdue;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTickFailed(exception);
            due = now + TimeSpan.FromMinutes(1);
        }
        finally
        {
            _ticking.Release();
        }
        return due;
    }

    private async Task StartAsync(long id, BackupRunKind kind, CancellationToken cancellationToken)
    {
        try
        {
            await jobs.StartAsync(id, kind, BackupRunTrigger.Schedule, cancellationToken);
        }
        catch (PithyxException exception) when (exception.Code is ErrorCodes.BackupJobBusy or ErrorCodes.BackupJobNotFound)
        {
            LogSkipped(id, kind, exception.Code);
        }
    }

    private void Wake()
    {
        lock (_wakeLock)
        {
            _wake.Cancel();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled {Kind} of backup job {JobId} skipped: {Reason}")]
    private partial void LogSkipped(long jobId, BackupRunKind kind, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The backup scheduler failed; it tries again in a minute")]
    private partial void LogTickFailed(Exception exception);
}
