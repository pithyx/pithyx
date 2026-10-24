using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Health;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Storage.Health.SmartParserTests;
using static Pithyx.Daemon.Tests.Storage.Health.StorageAlertsTests;

namespace Pithyx.Daemon.Tests.Storage.Health;

/// <summary>SMART reads, scheduled scrubs and self-tests on a fake clock, with every command scripted.</summary>
public sealed class StorageHealthMonitorTests : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-health-").FullName;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-01T08:00:00Z", CultureInfo.InvariantCulture));
    private readonly ScriptedCommandRunner _commands = new();
    private readonly DiskInventory _disks = new();
    private readonly PoolInventory _pools = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly StorageSchedules _schedules;
    private readonly StorageAlertService _alerts;
    private readonly SmartInventory _smart;
    private readonly StorageHealthMonitor _monitor;
    private readonly NotificationStore _store;
    private readonly NotificationService _notifications;
    private readonly StorageAlertNotifier _notifier;
    private readonly DateTimeOffset _start;

    /// <summary>What <c>smartctl -a</c> prints, and whether the disk sleeps (then smartctl -n standby does nothing).</summary>
    private string _smartOutput = Fixture("ata-healthy");
    private bool _asleep;

    public StorageHealthMonitorTests()
    {
        _start = _time.GetUtcNow();
        _connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
        new MigrationRunner(_connections, _time, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), CancellationToken.None).GetAwaiter().GetResult();
        _disks.Replace([Sdb]);
        _pools.Replace(new PoolList([Pool()], []));
        _smart = new SmartInventory(_commands, _time, NullLogger<SmartInventory>.Instance);
        _schedules = new StorageSchedules(_connections, _time);
        _alerts = new StorageAlertService(_connections, _pools, _disks, _smart, _time, NullLogger<StorageAlertService>.Instance);
        _store = new NotificationStore(_connections);
        var hub = new TopicHub(new TopicRegistry([_alerts.Definition(), _store.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        _notifications = new NotificationService(_store, _connections, hub, _time, Options.Create(new JsonOptions()), NullLogger<NotificationService>.Instance);
        _notifier = new StorageAlertNotifier(_connections, _notifications, _time, NullLogger<StorageAlertNotifier>.Instance);
        _monitor = new StorageHealthMonitor(_disks, _pools, _smart, _alerts, _notifier, _schedules, _commands, hub, _time, NullLogger<StorageHealthMonitor>.Instance);
        _commands.Handler = request => request.FileName switch
        {
            "smartctl" when _asleep => new CommandResult(2, Fixture("ata-standby"), "", TimeSpan.Zero),
            "smartctl" when request.Arguments.Contains("-a") => new CommandResult(0, _smartOutput, "", TimeSpan.Zero),
            "smartctl" => new CommandResult(0, "{}", "", TimeSpan.Zero),
            "zpool" => new CommandResult(0, "", "", TimeSpan.Zero),
            _ => null,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _monitor.StopAsync(CancellationToken.None);
        _monitor.Dispose();
        _alerts.Dispose();
        _notifier.Dispose();
        _notifications.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private List<CommandRequest> Calls(string program, string argument) =>
        [.. _commands.Calls.Where(call => call.FileName == program && call.Arguments.Contains(argument))];

    /// <summary>
    /// Moves the clock and wakes the monitor. It reads the clock before it arms its sleep, so a move in between would
    /// otherwise leave it sleeping up to <see cref="StorageHealthMonitor.MaxSleep"/> past the new time.
    /// </summary>
    private void Advance(TimeSpan by)
    {
        _time.Advance(by);
        _monitor.Wake();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }

    /// <summary>The monitor writes a run after its command; tests that saw the command wait for the row.</summary>
    private async Task UntilRunAsync(Func<Dictionary<(string Kind, string Target), DateTimeOffset>, bool> condition)
    {
        for (var i = 0; i < 500 && !condition(await RunsAsync()); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(await RunsAsync()));
    }

    private async Task<Dictionary<(string Kind, string Target), DateTimeOffset>> RunsAsync()
    {
        await using var connection = await _connections.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<(string Kind, string Target, string At)>("SELECT kind, target, last_run_at FROM storage_schedule_runs");
        return rows.ToDictionary(row => (row.Kind, row.Target), row => DateTimeOffset.Parse(row.At, CultureInfo.InvariantCulture));
    }

    /// <summary>Starts the monitor and waits until it has seen the pool and the disk for the first time.</summary>
    private async Task StartAsync()
    {
        await _monitor.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 500 && (await RunsAsync()).Count < 3; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Equal(3, (await RunsAsync()).Count);
    }

    [Fact]
    public async Task SmartIsReadAtStartupAndAFailingDiskRaisesAnAlert()
    {
        _smartOutput = Fixture("ata-failing");

        await StartAsync();

        Assert.Equal("failed", _smart.Of(Sdb.Id)?.State);
        Assert.Equal([$"disk.smart_failed:{Sdb.Id}"], _alerts.Current.Alerts.Select(alert => alert.Key));
        Assert.True(_alerts.Settled);
    }

    [Fact]
    public async Task AFailingDiskNotifiesEveryAdministratorOnceAndARestartDoesNotNotifyAgain()
    {
        long admin, user;
        await using (var connection = await _connections.OpenAsync(TestContext.Current.CancellationToken))
        {
            admin = await TestUsers.InsertAsync(connection, "ada", admin: true);
            user = await TestUsers.InsertAsync(connection, "bob");
        }
        _smartOutput = Fixture("ata-failing");

        await StartAsync();
        await UntilAsync(() => _store.Of(admin).Count > 0);
        // A restart evaluates the same alert from scratch; the notice in the database keeps it from notifying again.
        await _notifier.SyncAsync([], _pools.Current, settled: false, TestContext.Current.CancellationToken);
        await _notifier.SyncAsync(_alerts.Current.Alerts, _pools.Current, _alerts.Settled, TestContext.Current.CancellationToken);

        var notification = Assert.Single(_store.Of(admin));
        Assert.Equal("storage.alert.smartFailed", notification.Code);
        Assert.Equal(NotificationTone.Critical, notification.Tone);
        Assert.Equal($"disk.smart_failed:{Sdb.Id}", notification.Params["alertKey"].GetString());
        Assert.Equal("sdb", notification.Params["disk"].GetString());
        Assert.Empty(_store.Of(user));
    }

    [Fact]
    public async Task ADiskChangeReadsSmartAgainButNeverWakesASleepingDisk()
    {
        await StartAsync();
        var before = _smart.Of(Sdb.Id)!;

        _asleep = true;
        _disks.Replace([Sdb with { Partitions = [new Partition("sdb1", "/dev/sdb1", 1, "zfs_member", null, [])] }]);
        await UntilAsync(() => _smart.Of(Sdb.Id)!.Asleep);

        Assert.Equal(before with { Asleep = true, CheckedAt = _smart.Of(Sdb.Id)!.CheckedAt }, _smart.Of(Sdb.Id));
        Assert.All(Calls("smartctl", "-j"), call => Assert.Equal(["-n", "standby"], call.Arguments.Take(2)));
    }

    [Fact]
    public async Task APoolIsScrubbedAMonthAfterItWasFirstSeen()
    {
        await StartAsync();

        Advance(_start.AddMonths(1) - _start - TimeSpan.FromMinutes(1));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(Calls("zpool", "scrub"));

        Advance(TimeSpan.FromMinutes(2));
        await UntilAsync(() => Calls("zpool", "scrub").Count == 1);
        Assert.Equal(["scrub", "tank"], Calls("zpool", "scrub")[0].Arguments);
        await UntilRunAsync(runs => runs.TryGetValue(("scrub", "tank"), out var run) && run > _start);
    }

    [Fact]
    public async Task ARunningScanOrAFaultedPoolPostponesTheScrub()
    {
        await StartAsync();
        _pools.Replace(new PoolList([Pool(scan: new PoolScan("resilver", "scanning", false, _start, null, 100, 1, 0))], []));

        Advance(TimeSpan.FromDays(40));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(Calls("zpool", "scrub"));

        _pools.Replace(new PoolList([Pool(scan: new PoolScan("resilver", "finished", false, _start, _start.AddHours(1), 100, 100, 0))], []));
        Advance(StorageHealthMonitor.RetryDelay);
        await UntilAsync(() => Calls("zpool", "scrub").Count == 1);
    }

    [Fact]
    public async Task AWeeklyShortSelfTestIsSkippedWhileTheDiskSleepsAndRetriedLater()
    {
        await StartAsync();
        _asleep = true;

        Advance(TimeSpan.FromDays(7));
        await UntilAsync(() => Calls("smartctl", "-t").Count == 1);
        Assert.Equal(["-n", "standby", "-j", "-t", "short", "/dev/sdb"], Calls("smartctl", "-t")[0].Arguments);
        Assert.Equal(_start, (await RunsAsync())[("shortSelfTest", Sdb.Id)]);

        _asleep = false;
        Advance(StorageHealthMonitor.RetryDelay);
        await UntilAsync(() => Calls("smartctl", "-t").Count == 2);
        await UntilRunAsync(runs => runs[("shortSelfTest", Sdb.Id)] == _start.AddDays(7) + StorageHealthMonitor.RetryDelay);

        // SMART is read again once the test had time to finish.
        var reads = Calls("smartctl", "-a").Count;
        Advance(StorageHealthMonitor.ShortTestReadDelay);
        await UntilAsync(() => Calls("smartctl", "-a").Count == reads + 1);
        Assert.All(Calls("smartctl", "-j"), call => Assert.Equal(["-n", "standby"], call.Arguments.Take(2)));
    }

    [Fact]
    public async Task ALongSelfTestWinsWhenBothAreDue()
    {
        await _schedules.SetAsync(new StorageScheduleUpdate(StorageSchedules.ShortSelfTest, "", "monthly"), TestContext.Current.CancellationToken);
        await StartAsync();

        Advance(_start.AddMonths(1) - _start);
        await UntilAsync(() => Calls("smartctl", "-t").Count == 1);
        await UntilRunAsync(runs => runs[("longSelfTest", Sdb.Id)] > _start && runs[("longSelfTest", Sdb.Id)] == runs[("shortSelfTest", Sdb.Id)]);

        Assert.Equal("long", Assert.Single(Calls("smartctl", "-t")).Arguments[4]);
    }

    [Fact]
    public async Task DisksWithoutSmartGetNoSelfTestsAndNothingRunsWhenOff()
    {
        _smartOutput = Fixture("qemu-scsi");
        await _schedules.SetAsync(new StorageScheduleUpdate(StorageSchedules.Scrub, "tank", "off"), TestContext.Current.CancellationToken);
        await _monitor.StartAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => _smart.Of(Sdb.Id) is not null);

        Advance(TimeSpan.FromDays(70));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(("unsupported", "virtual"), (_smart.Of(Sdb.Id)!.State, _smart.Of(Sdb.Id)!.Reason));
        Assert.Empty(Calls("smartctl", "-t"));
        Assert.Empty(Calls("zpool", "scrub"));
    }
}
