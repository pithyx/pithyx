using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>Schedules, retention, holds and refreshes against a played ZFS, with a fake clock.</summary>
public sealed class SnapshotSchedulerTests : IAsyncLifetime
{
    private const string Data = "tank/data";
    private static readonly DateTimeOffset _start = new(2026, 9, 27, 21, 30, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-snapshots-").FullName;
    private readonly JumpingClock _time = new(new FakeTimeProvider(_start));
    private readonly FakeZfsSnapshots _zfs;
    private readonly SnapshotInventory _inventory = new();
    private readonly PoolInventory _pools = new();
    private readonly SnapshotService _service;
    private readonly SnapshotScheduler _scheduler;

    public SnapshotSchedulerTests()
    {
        _zfs = new FakeZfsSnapshots(_time);
        var connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
        _pools.Replace(new PoolList([], [Dataset(Data), Dataset("tank/other")]));
        _service = new SnapshotService(_zfs, _inventory, _pools, connections, _time, NullLogger<SnapshotService>.Instance);
        _scheduler = new SnapshotScheduler(_service, _inventory, _time, NullLogger<SnapshotScheduler>.Instance);
        _migrations = new MigrationRunner(connections, _time, NullLogger<MigrationRunner>.Instance);
    }

    private readonly MigrationRunner _migrations;

    public async ValueTask InitializeAsync() => await _migrations.RunAsync(Migration.Embedded(), Token);

    public async ValueTask DisposeAsync()
    {
        await _scheduler.StopAsync(CancellationToken.None);
        _scheduler.Dispose();
        _service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static Dataset Dataset(string name) => new(name, name.Split('/')[0], "filesystem", 0, 0, 0, $"/{name}", true, "off", null, null, null, null, "lz4", 131072, null, null, 0);

    private Task<SnapshotScheduleList> EnableAsync(string kind, int keep, string dataset = Data) =>
        _service.SetSchedulesAsync(new SnapshotScheduleList(dataset, [new SnapshotSchedule(kind, true, keep)]), Token);

    private string[] Names(string? kind = null) =>
        [.. _zfs.Snapshots.Where(snapshot => snapshot.Dataset == Data && (kind is null || snapshot.PithyxKind == kind)).OrderBy(snapshot => snapshot.CreateTxg).Select(snapshot => snapshot.Name)];

    private async Task PassAsync()
    {
        await _service.RefreshAsync(Token);
        await _scheduler.PassAsync(Token);
    }

    [Fact]
    public async Task AnHourlyScheduleTakesOneSnapshotPerHourAndSleepsUntilTheNext()
    {
        await EnableAsync("hourly", 24);

        var sleep = await _scheduler.PassAsync(Token);

        Assert.Equal(["zfs snapshot tank/data@pithyx-hourly-20260927T213000Z"], _zfs.Changes);
        Assert.Equal(TimeSpan.FromMinutes(30), sleep);
        Assert.Equal(TimeSpan.FromMinutes(30), await _scheduler.PassAsync(Token));
        Assert.Single(_zfs.Changes);
    }

    [Fact]
    public async Task SchedulesStartOffWithTheDefaultCounts()
    {
        var schedules = await _service.GetSchedulesAsync(Data, Token);

        Assert.Equal([("hourly", false, 24), ("daily", false, 7), ("weekly", false, 4), ("monthly", false, 6)], schedules.Schedules.Select(s => (s.Kind, s.Enabled, s.Keep)));
        Assert.Null(await _scheduler.PassAsync(Token));
        Assert.Empty(_zfs.Calls);
    }

    [Theory]
    [InlineData("hourly", 0)]
    [InlineData("hourly", 1001)]
    [InlineData("yearly", 5)]
    [InlineData("manual", 5)]
    public async Task InvalidSchedulesAreRefused(string kind, int keep)
    {
        var error = await Assert.ThrowsAsync<PithyxException>(() => EnableAsync(kind, keep));

        Assert.Equal(ErrorCodes.StorageSnapshotScheduleInvalid, error.Code);
    }

    [Fact]
    public async Task RetentionDestroysOnlyTheOldestPithyxSnapshotsOneByOne()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-30));
        _zfs.Add(Data, "autosnap_2026-09-01_00:00:00_hourly", _start.AddDays(-26));
        _zfs.Add(Data, "pithyx-manual-20260901T000000Z", _start.AddDays(-26));
        _zfs.Add(Data, "pithyx-daily-20260901T000000Z", _start.AddDays(-26));
        _zfs.Add("tank/other", "pithyx-hourly-20260901T000000Z", _start.AddDays(-26));
        await EnableAsync("hourly", 3);

        for (var hour = 0; hour < 10; hour++)
        {
            await PassAsync();
            _time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(
            ["before-upgrade", "autosnap_2026-09-01_00:00:00_hourly", "pithyx-manual-20260901T000000Z", "pithyx-daily-20260901T000000Z", "pithyx-hourly-20260928T043000Z", "pithyx-hourly-20260928T053000Z", "pithyx-hourly-20260928T063000Z"],
            Names());
        Assert.Contains(_zfs.Snapshots, snapshot => snapshot.Dataset == "tank/other");
        var destroys = _zfs.Calls.Where(call => call.Arguments[0] == "destroy").ToList();
        Assert.Equal(7, destroys.Count);
        Assert.Equal(["destroy", "tank/data@pithyx-hourly-20260927T213000Z"], destroys[0].Arguments);
        Assert.All(destroys, call => Assert.Matches("^tank/data@pithyx-hourly-[0-9]{8}T[0-9]{6}Z$", Assert.Single(call.Arguments.Skip(1))));
        Assert.DoesNotContain(_zfs.Calls, call => call.Arguments.Contains("-r") || call.Arguments.Contains("-R"));
    }

    [Fact]
    public async Task HeldSnapshotsAreNeverPruned()
    {
        var held = _zfs.Add(Data, "pithyx-hourly-20260927T100000Z", _start.AddHours(-11), holds: 1);
        await EnableAsync("hourly", 1);

        for (var hour = 0; hour < 4; hour++)
        {
            await PassAsync();
            _time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal([held.Name, "pithyx-hourly-20260928T003000Z"], Names());
    }

    [Fact]
    public async Task ABoxThatWasOffForMonthsTakesOneSnapshotPerScheduleAndKeepsTheOldOnes()
    {
        await EnableAsync("hourly", 24);
        await EnableAsync("daily", 7);
        await EnableAsync("monthly", 6);
        await PassAsync();
        Assert.Equal(3, Names().Length);

        _time.Advance(TimeSpan.FromDays(120));
        await PassAsync();

        Assert.Equal(
            ["pithyx-daily-20260927T213000Z", "pithyx-hourly-20260927T213000Z", "pithyx-monthly-20260927T213000Z", "pithyx-daily-20270125T213000Z", "pithyx-hourly-20270125T213000Z", "pithyx-monthly-20270125T213000Z"],
            Names());
        Assert.DoesNotContain(_zfs.Calls, call => call.Arguments[0] == "destroy");
    }

    [Fact]
    public async Task ABackwardsJumpKeepsTheFutureSnapshotsAndStillTakesNewOnes()
    {
        await EnableAsync("hourly", 2);
        for (var hour = 0; hour < 3; hour++)
        {
            await PassAsync();
            _time.Advance(TimeSpan.FromHours(1));
        }
        var before = Names();

        // NTP moves the clock back by a year: every snapshot is now in the future.
        _time.Jump(TimeSpan.FromDays(-365));
        for (var hour = 0; hour < 5; hour++)
        {
            await PassAsync();
            _time.Advance(TimeSpan.FromHours(1));
        }

        var after = Names();
        Assert.Equal(before, after.Take(before.Length));
        Assert.Equal(["pithyx-hourly-20250928T033000Z", "pithyx-hourly-20250928T043000Z"], after.Skip(before.Length));
    }

    [Fact]
    public async Task AClockThatReturnsToAnHourWithASnapshotDoesNotTakeASecondOne()
    {
        await EnableAsync("hourly", 24);
        await PassAsync();
        _time.Advance(TimeSpan.FromMinutes(20));
        _time.Jump(TimeSpan.FromMinutes(-10));

        await PassAsync();

        Assert.Single(Names());
    }

    [Fact]
    public async Task SchedulesOfMissingDatasetsAreSkipped()
    {
        await EnableAsync("daily", 7);
        _pools.Replace(new PoolList([], [Dataset("tank/other")]));

        await _scheduler.PassAsync(Token);

        Assert.Empty(_zfs.Changes);
    }

    [Fact]
    public async Task TheRunningSchedulerWakesAtTheNextHour()
    {
        await EnableAsync("hourly", 24);
        await _scheduler.StartAsync(Token);
        await WaitForAsync(() => Names().Length == 1);
        // Let the scheduler start its timer before the clock moves.
        await Task.Delay(100, Token);

        _time.Advance(TimeSpan.FromMinutes(29));
        await Task.Delay(50, Token);
        Assert.Single(Names());
        _time.Advance(TimeSpan.FromMinutes(1));

        await WaitForAsync(() => Names().Length == 2);
        Assert.Equal("pithyx-hourly-20260927T220000Z", Names()[1]);
    }

    [Fact]
    public async Task SavingASchedulesWakesTheScheduler()
    {
        await _scheduler.StartAsync(Token);
        await Task.Delay(50, Token);
        Assert.Empty(Names());

        await EnableAsync("weekly", 4);

        await WaitForAsync(() => Names().Length == 1);
        Assert.Equal("pithyx-weekly-20260927T213000Z", Names()[0]);
    }

    [Fact]
    public async Task ManualSnapshotsAreTakenAndNeverPruned()
    {
        await _service.TakeAsync(Data, SnapshotKinds.Manual, Token);
        var again = await Assert.ThrowsAsync<PithyxException>(() => _service.TakeAsync(Data, SnapshotKinds.Manual, Token));
        Assert.Equal(ErrorCodes.StorageSnapshotExists, again.Code);
        await EnableAsync("hourly", 1);
        for (var hour = 0; hour < 3; hour++)
        {
            _time.Advance(TimeSpan.FromHours(1));
            await PassAsync();
        }

        Assert.Equal(["pithyx-manual-20260927T213000Z", "pithyx-hourly-20260928T003000Z"], Names());
    }

    [Fact]
    public async Task HoldAndReleaseUseThePithyxTag()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-1));

        await _service.HoldAsync(Data, "before-upgrade", Token);
        Assert.Equal(1, _inventory.Find(Data, "before-upgrade")!.Holds);
        await _service.ReleaseAsync(Data, "before-upgrade", Token);

        Assert.Equal(["zfs hold pithyx tank/data@before-upgrade", "zfs release pithyx tank/data@before-upgrade"], _zfs.Changes);
        Assert.Equal(0, _inventory.Find(Data, "before-upgrade")!.Holds);
    }

    [Fact]
    public async Task APoolRefreshReadsTheSnapshotsAgain()
    {
        await _scheduler.StartAsync(Token);
        _zfs.Add(Data, "made-on-the-command-line", _start);

        _pools.Replace(_pools.Current);

        await WaitForAsync(() => _inventory.Find(Data, "made-on-the-command-line") is not null);
    }

    /// <summary>A fake clock whose wall time can also jump back, as NTP may move it; timers follow the fake clock.</summary>
    private sealed class JumpingClock(FakeTimeProvider inner) : TimeProvider
    {
        private TimeSpan _offset;

        public void Advance(TimeSpan by) => inner.Advance(by);

        public void Jump(TimeSpan by) => _offset += by;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + _offset;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Timed out.");
            await Task.Delay(10, Token);
        }
    }
}
