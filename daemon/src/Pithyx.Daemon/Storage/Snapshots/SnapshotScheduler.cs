using System.Diagnostics;
using System.Threading.Channels;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Storage.Snapshots;

/// <summary>
/// Takes scheduled snapshots and prunes them. A schedule is due when its current period (UTC hour, day, ISO week from
/// Monday, month) has no snapshot of its kind yet, so a box that was off for months takes one snapshot per schedule
/// when it starts, not one per missed period, and a clock that went back never takes a second one for a period that
/// has one. Between passes the service sleeps until the next period starts, or until schedules change; it wakes at
/// least every <see cref="MaxSleep"/> because a wall clock jump does not shorten a running timer. It also reads the
/// snapshot list again after every pool refresh.
/// </summary>
public sealed partial class SnapshotScheduler(SnapshotService snapshots, SnapshotInventory inventory, TimeProvider time, ILogger<SnapshotScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        snapshots.SchedulesChanged += Wake;
        await snapshots.RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        snapshots.SchedulesChanged -= Wake;
        await base.StopAsync(cancellationToken);
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var refreshes = FollowRefreshesAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var sleep = await PassAsync(stoppingToken);
                using var sleeping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var timer = sleep is { } duration ? Task.Delay(duration, time, sleeping.Token) : Task.Delay(Timeout.InfiniteTimeSpan, sleeping.Token);
                var woken = _wake.Reader.WaitToReadAsync(sleeping.Token).AsTask();
                await Task.WhenAny(timer, woken);
                await sleeping.CancelAsync();
                _wake.Reader.TryRead(out _);
                stoppingToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        await refreshes;
    }

    private async Task FollowRefreshesAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in snapshots.Refreshes.ReadAllAsync(stoppingToken))
            {
                await snapshots.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Takes and prunes what is due now. Returns how long to sleep, or null without an enabled schedule.</summary>
    internal async Task<TimeSpan?> PassAsync(CancellationToken cancellationToken)
    {
        var parent = Activity.Current;
        Activity.Current = null;
        using var activity = Tracing.Source.StartActivity("storage.snapshots.schedule");
        try
        {
            IReadOnlyList<(string Dataset, string Kind, int Keep)> schedules;
            try
            {
                schedules = await snapshots.EnabledSchedulesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPassFailed(exception);
                return MaxSleep;
            }
            var now = time.GetUtcNow();
            foreach (var (dataset, kind, keep) in schedules)
            {
                if (!snapshots.HasDataset(dataset))
                {
                    continue;
                }
                try
                {
                    var (start, end) = Period(kind, now);
                    var taken = inventory.Of(dataset).Any(snapshot => snapshot.PithyxKind == kind && snapshot.CreatedAt >= start && snapshot.CreatedAt < end);
                    if (!taken)
                    {
                        await snapshots.TakeAsync(dataset, kind, cancellationToken);
                    }
                    await snapshots.PruneAsync(dataset, kind, keep, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogScheduleFailed(dataset, kind, exception);
                }
            }
            if (schedules.Count == 0)
            {
                return null;
            }
            var next = schedules.Min(schedule => Period(schedule.Kind, now).End);
            var sleep = next - time.GetUtcNow();
            return sleep < TimeSpan.Zero ? TimeSpan.Zero : sleep > MaxSleep ? MaxSleep : sleep;
        }
        finally
        {
            Activity.Current = parent;
        }
    }

    /// <summary>The UTC period of a schedule kind that contains <paramref name="now"/>: start inclusive, end exclusive.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Period(string kind, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var day = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        switch (kind)
        {
            case SnapshotKinds.Hourly:
                var hour = day.AddHours(utc.Hour);
                return (hour, hour.AddHours(1));
            case SnapshotKinds.Daily:
                return (day, day.AddDays(1));
            case SnapshotKinds.Weekly:
                var monday = day.AddDays(-(((int)utc.DayOfWeek + 6) % 7));
                return (monday, monday.AddDays(7));
            case SnapshotKinds.Monthly:
                var first = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
                return (first, first.AddMonths(1));
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Snapshot schedule {Kind} of {Dataset} failed")]
    private partial void LogScheduleFailed(string dataset, string kind, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reading the snapshot schedules failed")]
    private partial void LogPassFailed(Exception exception);
}
