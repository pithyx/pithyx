using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>
/// Destroying a snapshot and rolling back to one as command plans, planned and run against a played ZFS: what may go,
/// what the preview lists, and that a rollback never discards a snapshot the user was not shown.
/// </summary>
public sealed class SnapshotPlanTests : IDisposable
{
    private const string Data = "tank/data";
    private static readonly DateTimeOffset _start = new(2026, 9, 27, 21, 30, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-snapshot-plans-").FullName;
    private readonly FakeTimeProvider _time = new(_start);
    private readonly FakeZfsSnapshots _zfs;
    private readonly PoolInventory _pools = new();
    private readonly SnapshotService _service;
    private readonly SystemStateService _state;
    private readonly StoragePlanner _planner;
    private readonly CommandPlanRunner _runner;

    public SnapshotPlanTests()
    {
        _zfs = new FakeZfsSnapshots(_time);
        var connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
        var disks = new DiskInventory();
        var snapshots = new SnapshotInventory();
        var facts = new StorageFacts(disks, _pools, DeploymentEnvironment.Prod, SystemFiles.Host);
        var hub = new TopicHub(new TopicRegistry([disks.Definition(), _pools.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        // The played ZFS knows only snapshots: pool refreshes fail quietly and the datasets stay as set here.
        _state = new SystemStateService(_zfs, disks, _pools, new ZfsBackend(_zfs), new DiskProbe(_zfs), facts, hub, _time, NullLogger<SystemStateService>.Instance);
        _service = new SnapshotService(_zfs, snapshots, _pools, connections, _time, NullLogger<SnapshotService>.Instance);
        var datasets = new DatasetPlanner(_state, _pools, snapshots, _service, new ShareService(connections, _time), _time, NullLogger<DatasetPlanner>.Instance);
        _planner = new StoragePlanner(_state, facts, _pools, new PoolImportScanner(_zfs, _state, facts, _pools), datasets, _zfs, _time);
        _runner = new CommandPlanRunner(_zfs, _state, facts, _time);
        _pools.Replace(new PoolList([], [Dataset("tank"), Dataset(Data)]));
    }

    public void Dispose()
    {
        _service.Dispose();
        _state.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dataset Dataset(string name) => new(name, "tank", "filesystem", 0, 0, 0, $"/{name}", true, "off", null, null, null, null, "lz4", 131072, null, null, 0);

    private Task<StoragePlanner.Result> PlanAsync(string action, string name, string? confirmName = null, string dataset = Data) =>
        _planner.PlanAsync(new StoragePlanRequest(action, "tank", Name: name, Dataset: dataset, ConfirmName: confirmName), Token);

    private async Task<string> RefusedAsync(string action, string name, string? confirmName = null, string dataset = Data) =>
        (await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(action, name, confirmName, dataset))).Code;

    /// <summary>The zfs calls that change something; pool refreshes run zpool, which the played ZFS refuses.</summary>
    private IEnumerable<string> Changes => _zfs.Changes.Where(change => change.StartsWith("zfs ", StringComparison.Ordinal));

    private string[] Names() =>
        [.. _zfs.Snapshots.Where(snapshot => snapshot.Dataset == Data).OrderBy(snapshot => snapshot.CreateTxg).Select(snapshot => snapshot.Name)];

    [Fact]
    public async Task OnlyPithyxSnapshotsWithoutHoldsCanBeDestroyed()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-1));
        _zfs.Add(Data, "pithyx-manual-20260926T000000Z", _start.AddDays(-1), holds: 1);
        _zfs.Add(Data, "pithyx-manual-20260927T000000Z", _start.AddHours(-1));

        Assert.Equal(ErrorCodes.StorageSnapshotNotPithyx, await RefusedAsync("snapshot.destroy", "before-upgrade"));
        Assert.Equal(ErrorCodes.StorageSnapshotHeld, await RefusedAsync("snapshot.destroy", "pithyx-manual-20260926T000000Z"));
        Assert.Equal(ErrorCodes.StorageSnapshotNotFound, await RefusedAsync("snapshot.destroy", "pithyx-manual-2026"));
        Assert.Equal(ErrorCodes.StorageDatasetNotFound, await RefusedAsync("snapshot.destroy", "pithyx-manual-20260927T000000Z", dataset: "tank/gone"));
        var result = await PlanAsync("snapshot.destroy", "pithyx-manual-20260927T000000Z");
        Assert.Equal(["tank/data@pithyx-manual-20260927T000000Z"], result.Datasets);
        Assert.False(result.RequiresConfirmName);
        Assert.Empty(Changes);

        await _runner.RunAsync(result.Plan!, null, Token);

        Assert.Equal(["zfs destroy tank/data@pithyx-manual-20260927T000000Z"], Changes);
        Assert.Equal(["before-upgrade", "pithyx-manual-20260926T000000Z"], Names());
    }

    [Fact]
    public async Task RollbackToTheNewestSnapshotNeedsTheTypedNameAndNoMinusR()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-1));

        var awaiting = await PlanAsync("snapshot.rollback", "before-upgrade");
        Assert.Null(awaiting.Plan);
        Assert.True(awaiting.RequiresConfirmName);
        Assert.Equal(["changes_lost"], awaiting.Warnings);
        Assert.Equal(ErrorCodes.StorageSnapshotConfirmationMismatch, await RefusedAsync("snapshot.rollback", "before-upgrade", "tank/Data"));
        var result = await PlanAsync("snapshot.rollback", "before-upgrade", Data);

        await _runner.RunAsync(result.Plan!, null, Token);

        Assert.Equal(["zfs rollback tank/data@before-upgrade"], Changes);
    }

    [Fact]
    public async Task RollbackPastNewerSnapshotsListsThemAndDestroysThemWithMinusR()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-3));
        _zfs.Add(Data, "pithyx-daily-20260926T000000Z", _start.AddDays(-2));
        _zfs.Add(Data, "someone-elses", _start.AddDays(-1));

        var result = await PlanAsync("snapshot.rollback", "before-upgrade", Data);

        Assert.Equal(["tank/data@pithyx-daily-20260926T000000Z", "tank/data@someone-elses"], result.Datasets);
        Assert.Equal(["changes_lost", "newer_snapshots"], result.Warnings);
        Assert.Empty(Changes);
        await _runner.RunAsync(result.Plan!, null, Token);
        Assert.Equal(["zfs rollback -r tank/data@before-upgrade"], Changes);
        Assert.Equal(["before-upgrade"], Names());
    }

    [Fact]
    public async Task ARollbackRefusesASnapshotTakenAfterThePreview()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-3));
        _zfs.Add(Data, "pithyx-daily-20260926T000000Z", _start.AddDays(-2));
        var result = await PlanAsync("snapshot.rollback", "before-upgrade", Data);
        _zfs.Add(Data, "pithyx-hourly-20260927T213000Z", _start);

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(result.Plan!, null, Token));

        Assert.Equal(ErrorCodes.StorageSnapshotNewerExist, error.Code);
        Assert.Equal(2, error.Parameters["count"]);
        Assert.Empty(Changes);
        Assert.Equal(3, Names().Length);
    }

    [Fact]
    public async Task ARollbackToTheNewestRefusesASnapshotTakenAfterThePreview()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-1));
        var result = await PlanAsync("snapshot.rollback", "before-upgrade", Data);
        _zfs.Add(Data, "pithyx-hourly-20260927T213000Z", _start);

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(result.Plan!, null, Token));

        Assert.Equal(ErrorCodes.StorageSnapshotNewerExist, error.Code);
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task RollbackPastAHeldSnapshotIsRefused()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-3));
        _zfs.Add(Data, "pithyx-daily-20260926T000000Z", _start.AddDays(-2), holds: 1);

        Assert.Equal(ErrorCodes.StorageSnapshotHeld, await RefusedAsync("snapshot.rollback", "before-upgrade", Data));
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task AHoldPlacedAfterThePreviewStopsTheRollback()
    {
        _zfs.Add(Data, "before-upgrade", _start.AddDays(-3));
        _zfs.Add(Data, "pithyx-daily-20260926T000000Z", _start.AddDays(-2));
        var result = await PlanAsync("snapshot.rollback", "before-upgrade", Data);
        await _service.HoldAsync(Data, "pithyx-daily-20260926T000000Z", Token);

        var error = await Assert.ThrowsAsync<PithyxException>(() => _runner.RunAsync(result.Plan!, null, Token));

        Assert.Equal(ErrorCodes.StorageSnapshotHeld, error.Code);
        Assert.DoesNotContain(Changes, change => change.StartsWith("zfs rollback", StringComparison.Ordinal));
    }
}
