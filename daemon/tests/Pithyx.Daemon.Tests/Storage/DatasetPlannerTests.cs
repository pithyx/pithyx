using Dapper;
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
/// Which datasets may be renamed, changed and destroyed, and what the plans list and do afterwards, against a pool with
/// the D100 layout and a real database for shares and snapshot schedules.
/// </summary>
public sealed class DatasetPlannerTests : IAsyncLifetime
{
    private const long GiB = 1024L * 1024 * 1024;

    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-datasets-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly ScriptedCommandRunner _commands = new();
    private readonly PoolInventory _pools = new();
    private readonly SnapshotInventory _snapshots = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly SnapshotService _snapshotService;
    private readonly SystemStateService _state;
    private readonly StoragePlanner _planner;

    public DatasetPlannerTests()
    {
        _connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
        var disks = new DiskInventory();
        var facts = new StorageFacts(disks, _pools, DeploymentEnvironment.Prod, SystemFiles.Host);
        var hub = new TopicHub(new TopicRegistry([disks.Definition(), _pools.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        // No ZFS answers: the planner's refreshes fail quietly and the datasets stay as the test set them.
        _state = new SystemStateService(_commands, disks, _pools, new ZfsBackend(_commands), new DiskProbe(_commands), facts, hub, _time, NullLogger<SystemStateService>.Instance);
        _snapshotService = new SnapshotService(_commands, _snapshots, _pools, _connections, _time, NullLogger<SnapshotService>.Instance);
        var datasets = new DatasetPlanner(_state, _pools, _snapshots, _snapshotService, new ShareService(_connections, _time), _time, NullLogger<DatasetPlanner>.Instance);
        _planner = new StoragePlanner(_state, facts, _pools, new PoolImportScanner(_commands, _state, facts, _pools), datasets, _commands, _time);
        _commands.Handler = SnapshotListing.Answer(_snapshots);
        _pools.Replace(new PoolList([Pool("tank"), Pool("fresh"), Pool("bare")], Layout()));
    }

    public async ValueTask InitializeAsync() =>
        await new MigrationRunner(_connections, _time, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);

    public ValueTask DisposeAsync()
    {
        _snapshotService.Dispose();
        _state.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Pool Pool(string name) => new(name, "1", "ONLINE", 200 * GiB, 10 * GiB, 190 * GiB, 5, 0, false, 0, null, []);

    private static Dataset Filesystem(string name, string? mountPoint, long used = GiB, long available = 100 * GiB, long? quota = null) =>
        new(name, name.Split('/')[0], "filesystem", used, available, used, mountPoint, mountPoint is not null, "off", null, null, quota, null, "lz4", 128 * 1024, null, null, 0);

    private static Dataset Volume(string name) =>
        new(name, name.Split('/')[0], "volume", 10 * GiB, 100 * GiB, GiB, null, false, "off", null, null, null, null, "lz4", null, 10 * GiB, 16 * 1024, 0);

    private static List<Dataset> Layout() =>
    [
        Filesystem("tank", null),
        Filesystem("tank/pithyx", null),
        Filesystem("tank/pithyx/shares", "/tank"),
        Filesystem("tank/pithyx/shares/media", "/tank/media", used: 5 * GiB, quota: 20 * GiB),
        Filesystem("tank/pithyx/shares/media/photos", "/tank/media/photos"),
        Filesystem("tank/pithyx/shares/docs", "/tank/docs"),
        Filesystem("tank/pithyx/homes", "/tank/homes"),
        Filesystem("tank/pithyx/homes/anna", "/tank/homes/anna"),
        Filesystem("tank/pithyx/apps", "/tank/.pithyx/apps"),
        Filesystem("tank/pithyx/apps/immich", "/tank/.pithyx/apps/immich"),
        Filesystem("tank/pithyx/system", "/tank/.pithyx/system"),
        Filesystem("tank/pithyx/volumes", null),
        Volume("tank/pithyx/volumes/vm1"),
        Filesystem("tank/data", "/tank/data"),
        Filesystem("fresh", null),
        Filesystem("fresh/pithyx", null, available: 50 * GiB),
        Filesystem("bare", "/bare"),
        Filesystem("bare/stuff", "/bare/stuff"),
    ];

    private Task<StoragePlanner.Result> PlanAsync(StoragePlanRequest request) => _planner.PlanAsync(request, Token);

    private async Task<string> RefusedAsync(StoragePlanRequest request) =>
        (await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(request))).Code;

    private static StoragePlanRequest Rename(string dataset, string newName) => new("dataset.rename", dataset.Split('/')[0], Dataset: dataset, NewName: newName);

    private static StoragePlanRequest Destroy(string dataset, string? confirm = null) => new("dataset.destroy", dataset.Split('/')[0], ConfirmName: confirm ?? dataset, Dataset: dataset);

    private static StoragePlanRequest Set(string dataset, long? quota = null, long? reservation = null, string? compression = null, long? recordSize = null) =>
        new("dataset.set", dataset.Split('/')[0], Dataset: dataset, QuotaBytes: quota, ReservationBytes: reservation, Compression: compression, RecordSizeBytes: recordSize);

    private static StoragePlanRequest NewVolume(string pool, string name, long size, bool sparse = false) => new("volume.create", pool, Name: name, SizeBytes: size, Sparse: sparse);

    private static string[] Line(PlanStep step) => [step.Command.FileName, .. step.Command.Arguments];

    private async Task AddShareAsync(string name, string path)
    {
        await using var connection = await _connections.OpenAsync(Token);
        await connection.ExecuteAsync(
            "INSERT INTO shares (name, path, created_at, updated_at) VALUES (@name, @path, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')",
            new { name, path });
    }

    private async Task<string[]> SharePathsAsync()
    {
        await using var connection = await _connections.OpenAsync(Token);
        return [.. await connection.QueryAsync<string>("SELECT path FROM shares ORDER BY name")];
    }

    private async Task<string[]> ScheduledDatasetsAsync()
    {
        await using var connection = await _connections.OpenAsync(Token);
        return [.. await connection.QueryAsync<string>("SELECT DISTINCT dataset FROM snapshot_schedules ORDER BY dataset")];
    }

    // Straight into the table: schedules of datasets that are gone stay too.
    private async Task ScheduleAsync(string dataset)
    {
        await using var connection = await _connections.OpenAsync(Token);
        await connection.ExecuteAsync(
            "INSERT INTO snapshot_schedules (dataset, kind, enabled, keep, updated_at) VALUES (@dataset, 'daily', 1, 7, '2026-01-01T00:00:00Z')",
            new { dataset });
    }

    [Theory]
    [InlineData("tank", DatasetRole.Root)]
    [InlineData("tank/pithyx", DatasetRole.Structure)]
    [InlineData("tank/pithyx/shares", DatasetRole.Structure)]
    [InlineData("tank/pithyx/volumes", DatasetRole.Structure)]
    [InlineData("tank/pithyx/later", DatasetRole.Structure)]
    [InlineData("tank/pithyx/homes/anna", DatasetRole.Managed)]
    [InlineData("tank/pithyx/apps/immich/db", DatasetRole.Managed)]
    [InlineData("tank/pithyx/system/logs", DatasetRole.Managed)]
    [InlineData("tank/pithyx/later/x", DatasetRole.Managed)]
    [InlineData("tank/pithyx/shares/media", DatasetRole.Share)]
    [InlineData("tank/pithyx/shares/media/photos", DatasetRole.Share)]
    [InlineData("tank/pithyx/volumes/vm1", DatasetRole.Volume)]
    [InlineData("tank/data", DatasetRole.Other)]
    [InlineData("tank/pithyxdata", DatasetRole.Other)]
    public void RolesFollowTheLayout(string dataset, DatasetRole role) => Assert.Equal(role, DatasetPlanner.RoleOf("tank", dataset));

    [Fact]
    public async Task ARenamedShareDatasetTakesItsShareFoldersAndSchedulesAlong()
    {
        await AddShareAsync("Media", "/tank/media");
        await AddShareAsync("Photos 2024", "/tank/media/photos/2024");
        await AddShareAsync("Mediathek", "/tank/mediathek");
        await ScheduleAsync("tank/pithyx/shares/media");
        await ScheduleAsync("tank/pithyx/shares/media/photos");
        await ScheduleAsync("tank/pithyx/shares/mediathek");
        // Left behind by a dataset of the new name that was destroyed outside Pithyx.
        await ScheduleAsync("tank/pithyx/shares/movies");

        var (_, plan, datasets) = await PlanAsync(Rename("tank/pithyx/shares/media", "movies"));

        Assert.Equal([["zfs", "rename", "tank/pithyx/shares/media", "tank/pithyx/shares/movies"]], plan!.Steps.Select(Line));
        Assert.Equal(["tank/pithyx/shares/media", "tank/pithyx/shares/media/photos"], datasets);
        // What the runner read again after the rename.
        _pools.Replace(new PoolList(
            _pools.Current.Pools,
            [.. Layout().Select(dataset => dataset.Name.StartsWith("tank/pithyx/shares/media", StringComparison.Ordinal)
                ? dataset with { Name = dataset.Name.Replace("/media", "/movies", StringComparison.Ordinal), MountPoint = dataset.MountPoint!.Replace("/media", "/movies", StringComparison.Ordinal) }
                : dataset)]));
        await plan.AfterRun!(Token);

        Assert.Equal(["/tank/movies", "/tank/mediathek", "/tank/movies/photos/2024"], await SharePathsAsync());
        Assert.Equal(["tank/pithyx/shares/mediathek", "tank/pithyx/shares/movies", "tank/pithyx/shares/movies/photos"], await ScheduledDatasetsAsync());
    }

    [Fact]
    public async Task VolumesAndDatasetsOutsideTheLayoutCanBeRenamed()
    {
        Assert.Equal(
            ["zfs", "rename", "tank/pithyx/volumes/vm1", "tank/pithyx/volumes/vm9"],
            Line(Assert.Single((await PlanAsync(Rename("tank/pithyx/volumes/vm1", "vm9"))).Plan!.Steps)));
        Assert.Equal(["zfs", "rename", "bare/stuff", "bare/things"], Line(Assert.Single((await PlanAsync(Rename("bare/stuff", "things"))).Plan!.Steps)));
    }

    [Theory]
    [InlineData("tank", ErrorCodes.StorageDatasetProtected)]
    [InlineData("tank/pithyx", ErrorCodes.StorageDatasetProtected)]
    [InlineData("tank/pithyx/shares", ErrorCodes.StorageDatasetProtected)]
    [InlineData("tank/pithyx/homes", ErrorCodes.StorageDatasetProtected)]
    [InlineData("tank/pithyx/apps", ErrorCodes.StorageDatasetProtected)]
    [InlineData("tank/pithyx/system", ErrorCodes.StorageDatasetProtected)]
    [InlineData("tank/pithyx/volumes", ErrorCodes.StorageDatasetProtected)]
    [InlineData("tank/pithyx/homes/anna", ErrorCodes.StorageDatasetManaged)]
    [InlineData("tank/pithyx/apps/immich", ErrorCodes.StorageDatasetManaged)]
    [InlineData("tank/nothing", ErrorCodes.StorageDatasetNotFound)]
    public async Task TheLayoutAndWhatPithyxFindsByNameKeepTheirNamesAndStay(string dataset, string code)
    {
        Assert.Equal(code, await RefusedAsync(Rename(dataset, "other")));
        Assert.Equal(code, await RefusedAsync(Destroy(dataset)));
    }

    [Fact]
    public async Task ARenameNeedsAFreeValidNameOnTheSamePool()
    {
        var taken = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(Rename("tank/pithyx/shares/media", "docs")));
        Assert.Equal((ErrorCodes.StorageDatasetExists, "tank/pithyx/shares/docs"), (taken.Code, taken.Parameters["name"]));
        Assert.Equal(ErrorCodes.StorageDatasetExists, await RefusedAsync(Rename("tank/pithyx/shares/media", "media")));
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, await RefusedAsync(Rename("tank/pithyx/shares/media", "../docs")));
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, await RefusedAsync(Rename("tank/pithyx/shares/media", "x/y")));
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, await RefusedAsync(new StoragePlanRequest("dataset.rename", "tank", Dataset: "tank/data")));
        Assert.Equal(ErrorCodes.StorageDatasetNotFound, await RefusedAsync(new StoragePlanRequest("dataset.rename", "bare", Dataset: "tank/data", NewName: "x")));
    }

    [Fact]
    public async Task SetChangesOnlyWhatDiffersInOneCommand()
    {
        var (_, plan, _) = await PlanAsync(Set("tank/pithyx/shares/media", quota: 50 * GiB, reservation: 2 * GiB, compression: "zstd-3", recordSize: 1024 * 1024));

        Assert.Equal(
            [["zfs", "set", "quota=53687091200", "reservation=2147483648", "compression=zstd-3", "recordsize=1048576", "tank/pithyx/shares/media"]],
            plan!.Steps.Select(Line));
        Assert.Null(plan.AfterRun);
        Assert.Equal(
            ["zfs", "set", "quota=none", "tank/pithyx/shares/media"],
            Line(Assert.Single((await PlanAsync(Set("tank/pithyx/shares/media", quota: 0, reservation: 0, compression: "lz4", recordSize: 128 * 1024))).Plan!.Steps)));
        Assert.Equal(ErrorCodes.StorageDatasetUnchanged, await RefusedAsync(Set("tank/pithyx/shares/media", compression: "lz4", quota: 20 * GiB)));
        Assert.Equal(ErrorCodes.StorageDatasetUnchanged, await RefusedAsync(Set("tank/pithyx/shares/media")));
    }

    [Fact]
    public async Task LimitsMustFitWhatTheDatasetUsesAndWhatThePoolHas()
    {
        var below = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(Set("tank/pithyx/shares/media", quota: 4 * GiB)));
        Assert.Equal((ErrorCodes.StorageDatasetQuotaBelowUsed, 5 * GiB), (below.Code, below.Parameters["usedBytes"]));
        var tooLarge = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(Set("tank/data", reservation: 102 * GiB)));
        Assert.Equal((ErrorCodes.StorageDatasetReservationTooLarge, 101 * GiB), (tooLarge.Code, tooLarge.Parameters["maxBytes"]));
        Assert.NotNull((await PlanAsync(Set("tank/data", reservation: 101 * GiB))).Plan);
        // The dataset's quota caps what a reservation may be, also a quota set in the same request.
        Assert.Equal(ErrorCodes.StorageDatasetReservationTooLarge, await RefusedAsync(Set("tank/pithyx/shares/media", reservation: 21 * GiB)));
        Assert.Equal(ErrorCodes.StorageDatasetReservationTooLarge, await RefusedAsync(Set("tank/data", quota: 10 * GiB, reservation: 11 * GiB)));
        Assert.Equal(ErrorCodes.StorageDatasetLimitInvalid, await RefusedAsync(Set("tank/data", quota: -1)));
        Assert.Equal(ErrorCodes.StorageDatasetLimitInvalid, await RefusedAsync(Set("tank/data", reservation: -1)));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("lzjb")]
    [InlineData("zstd-20")]
    [InlineData("gzip-10")]
    [InlineData("zstd-fast-1")]
    [InlineData("LZ4")]
    [InlineData("")]
    public async Task CompressionComesFromTheList(string compression) =>
        Assert.Equal(ErrorCodes.StorageDatasetCompressionInvalid, await RefusedAsync(Set("tank/data", compression: compression)));

    [Theory]
    [InlineData(2048)]
    [InlineData(12288)]
    [InlineData(2 * 1024 * 1024)]
    [InlineData(0)]
    public async Task RecordSizesArePowersOfTwoFrom4KiBTo1MiB(long recordSize) =>
        Assert.Equal(ErrorCodes.StorageDatasetRecordSizeInvalid, await RefusedAsync(Set("tank/data", recordSize: recordSize)));

    [Fact]
    public async Task ThePoolRootTheLayoutAndVolumesTakeOnlyCompression()
    {
        Assert.Equal(["zfs", "set", "compression=zstd", "tank/pithyx"], Line(Assert.Single((await PlanAsync(Set("tank/pithyx", compression: "zstd"))).Plan!.Steps)));
        Assert.Equal(["zfs", "set", "compression=off", "tank/pithyx/volumes/vm1"], Line(Assert.Single((await PlanAsync(Set("tank/pithyx/volumes/vm1", compression: "off"))).Plan!.Steps)));
        var unsupported = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(Set("tank/pithyx/volumes/vm1", recordSize: 4096)));
        Assert.Equal((ErrorCodes.StorageDatasetPropertyUnsupported, "recordsize"), (unsupported.Code, unsupported.Parameters["property"]));
        Assert.Equal(ErrorCodes.StorageDatasetPropertyUnsupported, await RefusedAsync(Set("tank/pithyx/volumes/vm1", quota: GiB)));
        Assert.Equal(ErrorCodes.StorageDatasetPropertyUnsupported, await RefusedAsync(Set("tank/pithyx/homes", quota: GiB)));
        Assert.Equal(ErrorCodes.StorageDatasetPropertyUnsupported, await RefusedAsync(Set("tank/pithyx/shares", reservation: GiB)));
        Assert.Equal(["zfs", "set", "compression=zstd", "tank"], Line(Assert.Single((await PlanAsync(Set("tank", compression: "zstd"))).Plan!.Steps)));
        Assert.Equal(ErrorCodes.StorageDatasetPropertyUnsupported, await RefusedAsync(Set("tank", quota: GiB)));
        // Apps (M4) get quotas on their datasets through the same action.
        Assert.Equal(["zfs", "set", "quota=10737418240", "tank/pithyx/apps/immich"], Line(Assert.Single((await PlanAsync(Set("tank/pithyx/apps/immich", quota: 10 * GiB))).Plan!.Steps)));
    }

    [Fact]
    public async Task DestroyListsChildrenAndSnapshotsAndTakesThemWithR()
    {
        _snapshots.Replace(
        [
            new Snapshot("tank/pithyx/shares/media", "pithyx-daily-20260927T000000Z", _time.GetUtcNow(), 0, 0, 0, 20),
            new Snapshot("tank/pithyx/shares/media", "before-cleanup", _time.GetUtcNow(), 0, 0, 0, 10),
            new Snapshot("tank/pithyx/shares/media/photos", "pithyx-manual-20260927T000000Z", _time.GetUtcNow(), 0, 0, 0, 30),
            new Snapshot("tank/pithyx/shares/docs", "keep", _time.GetUtcNow(), 0, 0, 1, 40),
        ]);
        await ScheduleAsync("tank/pithyx/shares/media");
        await ScheduleAsync("tank/pithyx/shares/media/photos");
        await ScheduleAsync("tank/pithyx/shares/docs");

        var (_, plan, datasets) = await PlanAsync(Destroy("tank/pithyx/shares/media"));

        Assert.Equal([["zfs", "destroy", "-r", "tank/pithyx/shares/media"]], plan!.Steps.Select(Line));
        Assert.Equal(
            [
                "tank/pithyx/shares/media",
                "tank/pithyx/shares/media@before-cleanup",
                "tank/pithyx/shares/media@pithyx-daily-20260927T000000Z",
                "tank/pithyx/shares/media/photos",
                "tank/pithyx/shares/media/photos@pithyx-manual-20260927T000000Z",
            ],
            datasets);
        // Live folder views below it are closed before zfs destroy, so the unmount is not busy.
        Assert.Contains("/tank/media", plan.Releases!);
        await plan.AfterRun!(Token);
        Assert.Equal(["tank/pithyx/shares/docs"], await ScheduledDatasetsAsync());
        var held = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(Destroy("tank/pithyx/shares/docs")));
        Assert.Equal((ErrorCodes.StorageSnapshotHeld, "tank/pithyx/shares/docs@keep"), (held.Code, held.Parameters["name"]));
    }

    [Fact]
    public async Task ADatasetWithoutChildrenOrSnapshotsIsDestroyedWithoutR()
    {
        Assert.Equal(["zfs", "destroy", "tank/data"], Line(Assert.Single((await PlanAsync(Destroy("tank/data"))).Plan!.Steps)));
        var volume = await PlanAsync(Destroy("tank/pithyx/volumes/vm1"));
        Assert.Equal(["zfs", "destroy", "tank/pithyx/volumes/vm1"], Line(Assert.Single(volume.Plan!.Steps)));
        Assert.Equal(["tank/pithyx/volumes/vm1"], volume.Datasets);
    }

    [Fact]
    public async Task DestroyNeedsTheFullNameTyped()
    {
        var mismatch = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(Destroy("tank/data", confirm: "data")));
        Assert.Equal((ErrorCodes.StorageDatasetNameMismatch, "tank/data"), (mismatch.Code, mismatch.Parameters["name"]));
        Assert.Equal(ErrorCodes.StorageDatasetNameMismatch, await RefusedAsync(new StoragePlanRequest("dataset.destroy", "tank", Dataset: "tank/data")));
    }

    [Fact]
    public async Task ADatasetHoldingAShareStays()
    {
        await AddShareAsync("Photos", "/tank/media/photos/2024");

        var inUse = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(Destroy("tank/pithyx/shares/media")));
        Assert.Equal((ErrorCodes.StorageDatasetInUseByShare, "tank/pithyx/shares/media", "Photos"), (inUse.Code, inUse.Parameters["dataset"], inUse.Parameters["share"]));
        Assert.Equal(ErrorCodes.StorageDatasetInUseByShare, await RefusedAsync(Destroy("tank/pithyx/shares/media/photos")));
        // A folder next to it that only starts with the same letters is not inside.
        await AddShareAsync("Docs old", "/tank/docsold");
        Assert.NotNull((await PlanAsync(Destroy("tank/pithyx/shares/docs"))).Plan);
    }

    [Fact]
    public async Task AVolumeIsCreatedBelowTheVolumesDatasetWhichComesFirstWhenMissing()
    {
        Assert.Equal(
            [
                ["zfs", "create", "-o", "canmount=off", "-o", "mountpoint=none", "fresh/pithyx/volumes"],
                ["zfs", "create", "-V", "10737418240", "-o", "volblocksize=16K", "fresh/pithyx/volumes/vm1"],
            ],
            (await PlanAsync(NewVolume("fresh", "vm1", 10 * GiB))).Plan!.Steps.Select(Line));
        Assert.Equal(
            [["zfs", "create", "-V", "1064960", "-s", "-o", "volblocksize=16K", "tank/pithyx/volumes/vm2"]],
            (await PlanAsync(NewVolume("tank", "vm2", (1024 * 1024) + 1, sparse: true))).Plan!.Steps.Select(Line));
    }

    [Fact]
    public async Task AVolumeNeedsAPreparedPoolAFreeNameAndSpaceUnlessSparse()
    {
        var tooLarge = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(NewVolume("fresh", "big", 51 * GiB)));
        Assert.Equal((ErrorCodes.StorageVolumeTooLarge, 50 * GiB), (tooLarge.Code, tooLarge.Parameters["availableBytes"]));
        Assert.NotNull((await PlanAsync(NewVolume("fresh", "big", 51 * GiB, sparse: true))).Plan);
        var bare = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(NewVolume("bare", "vm1", GiB)));
        Assert.Equal((ErrorCodes.StoragePoolNotPrepared, "bare"), (bare.Code, bare.Parameters["pool"]));
        Assert.Equal(ErrorCodes.StoragePoolNotFound, await RefusedAsync(NewVolume("nope", "vm1", GiB)));
        Assert.Equal(ErrorCodes.StorageDatasetExists, await RefusedAsync(NewVolume("tank", "vm1", GiB)));
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, await RefusedAsync(NewVolume("tank", "-V", GiB)));
        Assert.Equal(ErrorCodes.StorageVolumeSizeInvalid, await RefusedAsync(NewVolume("tank", "tiny", 1024)));
        Assert.Equal(ErrorCodes.StorageVolumeSizeInvalid, await RefusedAsync(new StoragePlanRequest("volume.create", "tank", Name: "none")));
    }
}
