using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage.Health;

/// <summary>
/// Keeps SMART and the alerts current and runs scheduled scrubs and self-tests. SMART is read at startup, for disks
/// that udev reports as added or changed, and after a self-test; alerts are evaluated after every such read and every
/// pool change, and administrators are notified of alerts that appeared or went away (<see cref="StorageAlertNotifier"/>).
/// Between events the service sleeps until the next task is due; it never polls.
/// </summary>
public sealed partial class StorageHealthMonitor : BackgroundService
{
    /// <summary>A task that could not run (disk asleep, scrub or resilver running) is tried again after this.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    /// <summary>SMART is read again this long after a self-test started, and every <see cref="RetryDelay"/> while it runs.</summary>
    public static readonly TimeSpan ShortTestReadDelay = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan LongTestReadDelay = TimeSpan.FromHours(2);

    /// <summary>The longest sleep, so a changed wall clock (NTP, a box that was off) is noticed within hours.</summary>
    public static readonly TimeSpan MaxSleep = TimeSpan.FromHours(6);

    private static readonly HashSet<string> _scrubbableStates = new(StringComparer.Ordinal) { "ONLINE", "DEGRADED" };

    private readonly DiskInventory _disks;
    private readonly PoolInventory _pools;
    private readonly SmartInventory _smart;
    private readonly StorageAlertService _alerts;
    private readonly StorageAlertNotifier _notifier;
    private readonly StorageSchedules _schedules;
    private readonly ICommandRunner _commands;
    private readonly TopicHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<StorageHealthMonitor> _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentQueue<DisksChanged> _diskChanges = new();
    private readonly Dictionary<(string Kind, string Target), DateTimeOffset> _retryAt = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _readAt = new(StringComparer.Ordinal);
    private int _poolsChanged;
    private bool _smartRead;

    public StorageHealthMonitor(
        DiskInventory disks,
        PoolInventory pools,
        SmartInventory smart,
        StorageAlertService alerts,
        StorageAlertNotifier notifier,
        StorageSchedules schedules,
        ICommandRunner commands,
        TopicHub hub,
        TimeProvider time,
        ILogger<StorageHealthMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(pools);
        _disks = disks;
        _pools = pools;
        _smart = smart;
        _alerts = alerts;
        _notifier = notifier;
        _schedules = schedules;
        _commands = commands;
        _hub = hub;
        _time = time;
        _logger = logger;
        disks.Changed += OnDisksChanged;
        pools.Changed += OnPoolsChanged;
    }

    /// <summary>Makes the service look at the schedules again, for example after one was edited.</summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Pool alerts are known before the daemon accepts requests; SMART follows in the background.
        await _alerts.LoadMutesAsync(cancellationToken);
        await RefreshAlertsAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _disks.Changed -= OnDisksChanged;
        _pools.Changed -= OnPoolsChanged;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await TracedAsync("storage.smart.read", async () =>
            {
                foreach (var disk in _disks.Current.Disks)
                {
                    await _smart.ReadAsync(disk, stoppingToken);
                }
                _smartRead = true;
                _alerts.Settled = _pools.Available;
                await RefreshAlertsAsync(stoppingToken);
            });
            while (!stoppingToken.IsCancellationRequested)
            {
                DateTimeOffset? next = null;
                await TracedAsync("storage.health.check", async () =>
                {
                    await ProcessChangesAsync(stoppingToken);
                    next = await RunDueAsync(stoppingToken);
                });
                await WaitAsync(next, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>The schedules as the Storage page shows them, with their next runs. Reads SQLite and memory only.</summary>
    public async Task<StorageScheduleList> ListAsync(CancellationToken cancellationToken)
    {
        var frequencies = await _schedules.FrequenciesAsync(cancellationToken);
        var lastRuns = await _schedules.LastRunsAsync(cancellationToken);
        var list = new List<StorageSchedule>();
        foreach (var pool in _pools.Current.Pools)
        {
            var frequency = Frequency(frequencies, StorageSchedules.Scrub, pool.Name);
            var last = LastScrub(lastRuns, pool);
            list.Add(new StorageSchedule(StorageSchedules.Scrub, pool.Name, frequency, last, last is { } at ? StorageSchedules.NextRun(frequency, at) : null));
        }
        foreach (var kind in new[] { StorageSchedules.ShortSelfTest, StorageSchedules.LongSelfTest })
        {
            var frequency = Frequency(frequencies, kind, "");
            var runs = TestableDisks().Select(disk => lastRuns.TryGetValue((kind, disk.Id), out var at) ? at : (DateTimeOffset?)null).OfType<DateTimeOffset>().ToList();
            var last = runs.Count > 0 ? runs.Min() : (DateTimeOffset?)null;
            list.Add(new StorageSchedule(kind, "", frequency, last, last is { } at ? StorageSchedules.NextRun(frequency, at) : null));
        }
        return new StorageScheduleList(list);
    }

    private async Task RefreshAlertsAsync(CancellationToken cancellationToken)
    {
        if (await _alerts.RefreshAsync(cancellationToken))
        {
            _hub.Publish(StorageAlertService.Topic, _alerts.Current);
        }
        await _notifier.SyncAsync(_alerts.Current.Alerts, _alerts.Settled, cancellationToken);
    }

    private void OnDisksChanged(DisksChanged changes)
    {
        _diskChanges.Enqueue(changes);
        Wake();
    }

    private void OnPoolsChanged()
    {
        Interlocked.Exchange(ref _poolsChanged, 1);
        Wake();
    }

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        var changed = Interlocked.Exchange(ref _poolsChanged, 0) == 1;
        while (_diskChanges.TryDequeue(out var change))
        {
            _smart.Remove(change.Removals);
            foreach (var disk in change.Upserts)
            {
                await _smart.ReadAsync(disk, cancellationToken);
            }
            changed = true;
        }
        if (_smartRead && _pools.Available && !_alerts.Settled)
        {
            // Evaluated again, so mutes and notices of alerts that went away while unsettled end now.
            _alerts.Settled = true;
            changed = true;
        }
        if (changed)
        {
            await RefreshAlertsAsync(cancellationToken);
        }
    }

    /// <summary>Runs every task that is due and returns when the next one is.</summary>
    private async Task<DateTimeOffset?> RunDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset? at)
        {
            if (at is { } value && (next is null || value < next))
            {
                next = value;
            }
        }

        var smartChanged = false;
        foreach (var (diskId, at) in _readAt)
        {
            if (at > now)
            {
                Consider(at);
                continue;
            }
            _readAt.TryRemove(diskId, out _);
            if (_disks.Current.Disks.FirstOrDefault(disk => disk.Id == diskId) is { } disk)
            {
                smartChanged |= await _smart.ReadAsync(disk, cancellationToken);
                if (_smart.Of(diskId)?.LastSelfTest == "running")
                {
                    _readAt[diskId] = now + RetryDelay;
                    Consider(now + RetryDelay);
                }
            }
        }
        if (smartChanged)
        {
            await RefreshAlertsAsync(cancellationToken);
        }

        var frequencies = await _schedules.FrequenciesAsync(cancellationToken);
        var lastRuns = await _schedules.LastRunsAsync(cancellationToken);
        foreach (var pool in _pools.Current.Pools)
        {
            Consider(await ScrubIfDueAsync(pool, Frequency(frequencies, StorageSchedules.Scrub, pool.Name), lastRuns, now, cancellationToken));
        }
        var shortFrequency = Frequency(frequencies, StorageSchedules.ShortSelfTest, "");
        var longFrequency = Frequency(frequencies, StorageSchedules.LongSelfTest, "");
        foreach (var disk in TestableDisks())
        {
            Consider(await SelfTestIfDueAsync(disk, shortFrequency, longFrequency, lastRuns, now, cancellationToken));
        }
        return next;
    }

    private async Task<DateTimeOffset?> ScrubIfDueAsync(
        Pool pool,
        string frequency,
        Dictionary<(string Kind, string Target), DateTimeOffset> lastRuns,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var key = (StorageSchedules.Scrub, pool.Name);
        if (LastScrub(lastRuns, pool) is not { } last)
        {
            // A pool seen for the first time is scrubbed one interval later.
            await _schedules.SetLastRunAsync(StorageSchedules.Scrub, pool.Name, now, cancellationToken);
            last = now;
        }
        if (Due(key, StorageSchedules.NextRun(frequency, last)) is not { } due || due > now)
        {
            return Due(key, StorageSchedules.NextRun(frequency, last));
        }
        if (pool.Scan is { State: "scanning" } || !_scrubbableStates.Contains(pool.State))
        {
            return Retry(key, now);
        }
        var result = await _commands.RunAsync(ZfsScrubPlans.Start(pool.Name), cancellationToken);
        if (result.ExitCode != 0)
        {
            LogScrubFailed(pool.Name, result.ExitCode, result.StandardError);
            return Retry(key, now);
        }
        LogScrubStarted(pool.Name);
        _retryAt.Remove(key);
        await _schedules.SetLastRunAsync(StorageSchedules.Scrub, pool.Name, now, cancellationToken);
        return StorageSchedules.NextRun(frequency, now);
    }

    private async Task<DateTimeOffset?> SelfTestIfDueAsync(
        Disk disk,
        string shortFrequency,
        string longFrequency,
        Dictionary<(string Kind, string Target), DateTimeOffset> lastRuns,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? DueOf(string kind, string frequency)
        {
            if (!lastRuns.TryGetValue((kind, disk.Id), out var last))
            {
                return null;
            }
            return Due((kind, disk.Id), StorageSchedules.NextRun(frequency, last));
        }

        foreach (var kind in new[] { StorageSchedules.ShortSelfTest, StorageSchedules.LongSelfTest })
        {
            if (!lastRuns.ContainsKey((kind, disk.Id)))
            {
                // A disk seen for the first time is tested one interval later.
                await _schedules.SetLastRunAsync(kind, disk.Id, now, cancellationToken);
                lastRuns[(kind, disk.Id)] = now;
            }
        }
        var longDue = DueOf(StorageSchedules.LongSelfTest, longFrequency);
        var shortDue = DueOf(StorageSchedules.ShortSelfTest, shortFrequency);
        // A long test covers what a short one checks, so it wins when both are due.
        var kindDue = longDue <= now ? StorageSchedules.LongSelfTest : shortDue <= now ? StorageSchedules.ShortSelfTest : null;
        if (kindDue is null)
        {
            return longDue is null ? shortDue : shortDue is null ? longDue : longDue < shortDue ? longDue : shortDue;
        }
        if (_smart.Of(disk.Id)?.LastSelfTest == "running")
        {
            return Retry((kindDue, disk.Id), now);
        }

        var result = await _commands.RunAsync(SmartCommands.SelfTest(disk.Path, kindDue == StorageSchedules.LongSelfTest ? "long" : "short"), cancellationToken);
        if ((result.ExitCode & 0b10) != 0)
        {
            // With -n standby smartctl did nothing: the disk sleeps and stays asleep.
            LogSelfTestSkipped(disk.Path);
            return Retry((kindDue, disk.Id), now);
        }
        if ((result.ExitCode & 0b111) != 0)
        {
            LogSelfTestFailed(disk.Path, result.ExitCode);
            return Retry((kindDue, disk.Id), now);
        }
        LogSelfTestStarted(kindDue, disk.Path);
        _retryAt.Remove((kindDue, disk.Id));
        await _schedules.SetLastRunAsync(kindDue, disk.Id, now, cancellationToken);
        lastRuns[(kindDue, disk.Id)] = now;
        if (kindDue == StorageSchedules.LongSelfTest)
        {
            _retryAt.Remove((StorageSchedules.ShortSelfTest, disk.Id));
            await _schedules.SetLastRunAsync(StorageSchedules.ShortSelfTest, disk.Id, now, cancellationToken);
            lastRuns[(StorageSchedules.ShortSelfTest, disk.Id)] = now;
        }
        var readAt = now + (kindDue == StorageSchedules.LongSelfTest ? LongTestReadDelay : ShortTestReadDelay);
        _readAt[disk.Id] = readAt;
        var nextShort = DueOf(StorageSchedules.ShortSelfTest, shortFrequency);
        return nextShort is { } at && at < readAt ? at : readAt;
    }

    /// <summary>The due time, moved later while a retry is pending.</summary>
    private DateTimeOffset? Due((string Kind, string Target) key, DateTimeOffset? scheduled) =>
        scheduled is { } at && _retryAt.TryGetValue(key, out var retry) && retry > at ? retry : scheduled;

    private DateTimeOffset Retry((string Kind, string Target) key, DateTimeOffset now)
    {
        _retryAt[key] = now + RetryDelay;
        return now + RetryDelay;
    }

    /// <summary>Disks with SMART that could be read or were asleep; virtual disks and failed reads are left alone.</summary>
    private IEnumerable<Disk> TestableDisks() =>
        _disks.Current.Disks.Where(disk => _smart.Of(disk.Id) is { State: not "unsupported" } report && report.Reason != "read_failed");

    private static string Frequency(Dictionary<(string Kind, string Target), string> frequencies, string kind, string target) =>
        frequencies.GetValueOrDefault((kind, target)) ?? StorageSchedules.DefaultFrequency(kind);

    /// <summary>The later of the last scheduled start and the start of the pool's last scrub (manual ones count).</summary>
    private static DateTimeOffset? LastScrub(Dictionary<(string Kind, string Target), DateTimeOffset> lastRuns, Pool pool)
    {
        DateTimeOffset? recorded = lastRuns.TryGetValue((StorageSchedules.Scrub, pool.Name), out var at) ? at : null;
        var scanned = pool.Scan is { Kind: "scrub" } scan ? scan.StartedAt : null;
        return recorded is null ? scanned : scanned is null ? recorded : recorded > scanned ? recorded : scanned;
    }

    private async Task WaitAsync(DateTimeOffset? next, CancellationToken stoppingToken)
    {
        var delay = next is { } at ? at - _time.GetUtcNow() : MaxSleep;
        delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxSleep ? MaxSleep : delay;
        if (delay == TimeSpan.Zero)
        {
            return;
        }
        using var wakeOrStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sleep = Task.Delay(delay, _time, wakeOrStop.Token);
        var wake = _wake.Reader.WaitToReadAsync(wakeOrStop.Token).AsTask();
        await Task.WhenAny(sleep, wake);
        await wakeOrStop.CancelAsync();
        _wake.Reader.TryRead(out _);
        stoppingToken.ThrowIfCancellationRequested();
    }

    private async Task TracedAsync(string name, Func<Task> work)
    {
        // Each round is its own trace, so its commands and the resulting event share one request id.
        var parent = Activity.Current;
        Activity.Current = null;
        using var activity = Tracing.Source.StartActivity(name);
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCheckFailed(exception);
        }
        finally
        {
            Activity.Current = parent;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled scrub of {Pool} started")]
    private partial void LogScrubStarted(string pool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled scrub of {Pool} failed with {ExitCode}: {StandardError}")]
    private partial void LogScrubFailed(string pool, int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled {Kind} of {Path} started")]
    private partial void LogSelfTestStarted(string kind, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled self-test of {Path} skipped: the disk is asleep")]
    private partial void LogSelfTestSkipped(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled self-test of {Path} did not start: smartctl exited with {ExitCode}")]
    private partial void LogSelfTestFailed(string path, int exitCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Storage health check failed")]
    private partial void LogCheckFailed(Exception exception);
}
