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

namespace Pithyx.Daemon.Tests.Shares;

/// <summary>
/// Creating shared folders as datasets and deleting them, against a pool with the D100 layout and a real database:
/// the dataset name rule, every refusal, the exact commands and the bookkeeping after a run.
/// </summary>
public sealed class SharePlannerTests : IAsyncLifetime
{
    private const long GiB = 1024L * 1024 * 1024;

    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-shares-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly ScriptedCommandRunner _commands = new();
    private readonly PoolInventory _pools = new();
    private readonly SnapshotInventory _snapshots = new();
    private readonly ShareChanges _changes = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly SnapshotService _snapshotService;
    private readonly SystemStateService _state;
    private readonly ShareService _shares;
    private readonly SharePlanner _planner;
    private readonly StoragePlanner _storage;

    public SharePlannerTests()
    {
        _connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
        var disks = new DiskInventory();
        var facts = new StorageFacts(disks, _pools, DeploymentEnvironment.Prod, SystemFiles.Host);
        var hub = new TopicHub(new TopicRegistry([disks.Definition(), _pools.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        // No ZFS answers: the planner's refreshes fail quietly and the datasets stay as the test set them.
        _state = new SystemStateService(_commands, disks, _pools, new ZfsBackend(_commands), new DiskProbe(_commands), facts, hub, _time, NullLogger<SystemStateService>.Instance);
        _snapshotService = new SnapshotService(_commands, _snapshots, _pools, _connections, _time, NullLogger<SnapshotService>.Instance);
        _shares = new ShareService(_connections, _time);
        var datasets = new DatasetPlanner(_state, _pools, _snapshots, _snapshotService, _shares, _time, NullLogger<DatasetPlanner>.Instance);
        _planner = new SharePlanner(_state, _pools, _shares, _changes, datasets, _snapshotService, _time, NullLogger<SharePlanner>.Instance);
        _storage = new StoragePlanner(_state, facts, _pools, new PoolImportScanner(_commands, _state, facts, _pools), datasets, _commands, _time);
        _commands.Handler = SnapshotListing.Answer(_snapshots);
        _pools.Replace(new PoolList([Pool("tank"), Pool("fresh"), Pool("bare")], Layout()));
    }

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_connections, _time, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
        await using var connection = await _connections.OpenAsync(Token);
        await connection.ExecuteAsync(
            """
            INSERT INTO users (id, username, password_hash, is_admin, created_at, updated_at)
            VALUES (1, 'admin', 'x', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                (2, 'anna', 'x', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
            """);
    }

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

    private static Dataset Filesystem(string name, string? mountPoint, long used = GiB, long? quota = null) =>
        new(name, name.Split('/')[0], "filesystem", used, 100 * GiB, used, mountPoint, mountPoint is not null, "off", null, null, quota, null, "lz4", 128 * 1024, null, null, 0);

    private static List<Dataset> Layout() =>
    [
        Filesystem("tank", null),
        Filesystem("tank/pithyx", null),
        Filesystem("tank/pithyx/shares", "/tank"),
        Filesystem("tank/pithyx/shares/media", "/tank/media", used: 5 * GiB, quota: 20 * GiB),
        Filesystem("tank/pithyx/shares/media/photos", "/tank/media/photos"),
        Filesystem("tank/pithyx/homes", "/tank/homes"),
        Filesystem("tank/pithyx/system", "/tank/.pithyx/system"),
        Filesystem("tank/scratch", "/tank/scratch"),
        Filesystem("tank/data", "/tank/data"),
        Filesystem("fresh", null),
        Filesystem("fresh/pithyx", null),
        Filesystem("bare", "/bare"),
    ];

    private static SharePlanRequest Create(string name, string pool = "tank", string? description = null) =>
        new("share.create", Name: name, Pool: pool, Description: description);

    private static SharePlanRequest Delete(long shareId, string confirm, ShareDataChoice? data) =>
        new("share.delete", ShareId: shareId, ConfirmName: confirm, Data: data);

    private Task<StoragePlanner.Result> PlanAsync(SharePlanRequest request) => _planner.PlanAsync(request, Token);

    private async Task<string> RefusedAsync(SharePlanRequest request) =>
        (await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(request))).Code;

    private static string[] Line(PlanStep step) => [step.Command.FileName, .. step.Command.Arguments];

    private async Task<long> AddShareAsync(string name, string path, string? dataset = null)
    {
        await using var connection = await _connections.OpenAsync(Token);
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO shares (name, path, dataset, created_at, updated_at) VALUES (@name, @path, @dataset, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z') RETURNING id",
            new { name, path, dataset });
    }

    private async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await _connections.OpenAsync(Token);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    [Theory]
    [InlineData("Media", "media")]
    [InlineData("My Photos", "my-photos")]
    [InlineData("Fotos Übersicht", "fotos-ubersicht")]
    [InlineData("Straße und Café", "strasse-und-cafe")]
    [InlineData("Ænima", "aenima")]
    [InlineData("Q3.2026 Reports", "q3.2026-reports")]
    [InlineData("  Team -- Docs  ", "team-docs")]
    [InlineData("_private_", "private_")]
    [InlineData("Scans 2026.", "scans-2026")]
    [InlineData("Ärger & Ö", "arger-o")]
    public void TheDatasetNameIsTheShareNameInLowerCaseWithDashes(string name, string dataset) =>
        Assert.Equal(dataset, SharePlanner.DatasetNameFor(name));

    [Theory]
    [InlineData("日本")]
    [InlineData("...")]
    [InlineData("- _ -")]
    public void NamesWithoutALetterOrDigitHaveNoDatasetName(string name) => Assert.Null(SharePlanner.DatasetNameFor(name));

    [Fact]
    public void LongNamesAreCutTo64CharactersWithoutATrailingDash()
    {
        var name = SharePlanner.DatasetNameFor(new string('a', 63) + " b")!;
        Assert.Equal(new string('a', 63), name);
        Assert.Equal(64, SharePlanner.DatasetNameFor(new string('x', 80))!.Length);
    }

    [Fact]
    public async Task CreateMakesOneDatasetBelowSharesAndRegistersItAfterTheRun()
    {
        var (verdicts, plan, datasets) = await PlanAsync(Create("My Photos", description: "  Holidays and family  "));

        Assert.Empty(verdicts);
        Assert.Equal([["zfs", "create", "tank/pithyx/shares/my-photos"]], plan!.Steps.Select(Line));
        Assert.Equal(["tank/pithyx/shares/my-photos"], datasets);
        Assert.Equal("share.create", plan.Kind);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM shares"));

        // What the runner read again after the command.
        _pools.Replace(new PoolList(_pools.Current.Pools, [.. Layout(), Filesystem("tank/pithyx/shares/my-photos", "/tank/my-photos")]));
        var revision = _changes.Revision;
        await plan.AfterRun!(Token);

        await using var connection = await _connections.OpenAsync(Token);
        var row = await connection.QuerySingleAsync<(long Id, string Name, string Path, string Description, string Dataset)>(
            "SELECT id, name, path, description, dataset FROM shares");
        Assert.Equal(("My Photos", "/tank/my-photos", "Holidays and family", "tank/pithyx/shares/my-photos"), (row.Name, row.Path, row.Description, row.Dataset));
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM share_access"));
        Assert.Equal(revision + 1, _changes.Revision);
        // Restore points are on from the start: daily, kept 7 days.
        var schedules = await _snapshotService.GetSchedulesAsync("tank/pithyx/shares/my-photos", Token);
        Assert.Equal(
            [("hourly", false), ("daily", true), ("weekly", false), ("monthly", false)],
            schedules.Schedules.Select(schedule => (schedule.Kind, schedule.Enabled)));
        Assert.Equal(7, schedules.Schedules.Single(schedule => schedule.Kind == "daily").Keep);
    }

    [Fact]
    public async Task ANameTakenSinceThePlanFailsTheRunAndLeavesTheDataset()
    {
        var plan = (await PlanAsync(Create("Photos"))).Plan!;
        await AddShareAsync("photos", "/srv/photos");
        _pools.Replace(new PoolList(_pools.Current.Pools, [.. Layout(), Filesystem("tank/pithyx/shares/photos", "/tank/photos")]));

        Assert.Equal(ErrorCodes.FsShareExists, (await Assert.ThrowsAsync<PithyxException>(() => plan.AfterRun!(Token))).Code);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM shares"));
    }

    [Fact]
    public async Task CreateRefusesWhatCannotBecomeASharedFolder()
    {
        await AddShareAsync("Archive", "/srv/archive");
        await AddShareAsync("Legacy", "/tank/old");

        Assert.Equal(ErrorCodes.StoragePoolNotFound, await RefusedAsync(Create("Docs", pool: "nope")));
        Assert.Equal(ErrorCodes.FsSharePoolNotPrepared, await RefusedAsync(Create("Docs", pool: "fresh")));
        Assert.Equal(ErrorCodes.FsSharePoolNotPrepared, await RefusedAsync(Create("Docs", pool: "bare")));
        Assert.Equal(ErrorCodes.FsShareInvalid, await RefusedAsync(Create("")));
        Assert.Equal(ErrorCodes.FsShareInvalid, await RefusedAsync(Create("a/b")));
        Assert.Equal(ErrorCodes.FsShareInvalid, await RefusedAsync(Create(".hidden")));
        Assert.Equal(ErrorCodes.FsShareDatasetNameInvalid, await RefusedAsync(Create("日本")));
        Assert.Equal(ErrorCodes.FsShareNameReserved, await RefusedAsync(Create("Homes")));
        Assert.Equal(ErrorCodes.StorageDatasetExists, await RefusedAsync(Create("MEDIA")));
        Assert.Equal(ErrorCodes.FsShareExists, await RefusedAsync(Create("archive")));
        Assert.Equal(ErrorCodes.FsSharePathTaken, await RefusedAsync(Create("Scratch")));
        Assert.Equal(ErrorCodes.FsSharePathTaken, await RefusedAsync(Create("Old")));
        Assert.Equal(ErrorCodes.FsShareDescriptionInvalid, await RefusedAsync(Create("Docs", description: new string('x', 201))));
        Assert.Equal(ErrorCodes.FsShareDescriptionInvalid, await RefusedAsync(Create("Docs", description: "two\nlines")));
        Assert.Equal(ErrorCodes.StoragePlanActionInvalid, await RefusedAsync(new SharePlanRequest("dataset.create", Name: "Docs", Pool: "tank")));
    }

    [Fact]
    public async Task DeleteWithKeepRunsNothingAndOnlyUnregisters()
    {
        var id = await AddShareAsync("Media", "/tank/media", "tank/pithyx/shares/media");
        await using (var connection = await _connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync("INSERT INTO share_access (share_id, user_id, level) VALUES (@id, 2, 'read')", new { id });
            await connection.ExecuteAsync(
                "INSERT INTO trash_items (share_id, original_path, kind, size, deleted_at) VALUES (@id, 'a.txt', 'file', 1, '2026-01-01T00:00:00Z')",
                new { id });
        }

        var (_, plan, datasets) = await PlanAsync(Delete(id, "Media", ShareDataChoice.Keep));

        Assert.Empty(plan!.Steps);
        Assert.Empty(datasets);
        await plan.AfterRun!(Token);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM shares"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM share_access"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM trash_items"));
    }

    [Fact]
    public async Task DeleteWithDestroyListsEverySnapshotAndNeverForces()
    {
        var id = await AddShareAsync("Media", "/tank/media", "tank/pithyx/shares/media");
        _snapshots.Replace(
        [
            new Snapshot("tank/pithyx/shares/media", "pithyx-daily-20260927T000000Z", _time.GetUtcNow(), 0, 0, 0, 20),
            new Snapshot("tank/pithyx/shares/media/photos", "before", _time.GetUtcNow(), 0, 0, 0, 30),
        ]);
        await using (var connection = await _connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync(
                "INSERT INTO snapshot_schedules (dataset, kind, enabled, keep, updated_at) VALUES ('tank/pithyx/shares/media', 'daily', 1, 7, '2026-01-01T00:00:00Z')");
        }

        var (_, plan, datasets) = await PlanAsync(Delete(id, "Media", ShareDataChoice.Destroy));

        Assert.Equal([["zfs", "destroy", "-r", "tank/pithyx/shares/media"]], plan!.Steps.Select(Line));
        Assert.Equal(
            [
                "tank/pithyx/shares/media",
                "tank/pithyx/shares/media@pithyx-daily-20260927T000000Z",
                "tank/pithyx/shares/media/photos",
                "tank/pithyx/shares/media/photos@before",
            ],
            datasets);
        Assert.Contains("/tank/media", plan.Releases!);
        var revision = _changes.Revision;
        await plan.AfterRun!(Token);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM shares"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM snapshot_schedules"));
        Assert.Equal(revision + 1, _changes.Revision);
    }

    [Fact]
    public async Task ADatasetWithoutChildrenOrSnapshotsIsDestroyedWithoutR()
    {
        _pools.Replace(new PoolList(_pools.Current.Pools, [.. Layout(), Filesystem("tank/pithyx/shares/docs", "/tank/docs")]));
        var id = await AddShareAsync("Docs", "/tank/docs", "tank/pithyx/shares/docs");

        var (_, plan, datasets) = await PlanAsync(Delete(id, "Docs", ShareDataChoice.Destroy));

        Assert.Equal(["zfs", "destroy", "tank/pithyx/shares/docs"], Line(Assert.Single(plan!.Steps)));
        Assert.Equal(["tank/pithyx/shares/docs"], datasets);
    }

    [Fact]
    public async Task DeleteNeedsTheNameTypedAndAChoiceThatFitsTheShare()
    {
        var media = await AddShareAsync("Media", "/tank/media", "tank/pithyx/shares/media");
        var folder = await AddShareAsync("Folder", "/srv/folder");
        var gone = await AddShareAsync("Gone", "/tank/gone", "tank/pithyx/shares/gone");
        var outside = await AddShareAsync("Outside", "/tank/data", "tank/data");
        await AddShareAsync("Photos 2024", "/tank/media/photos/2024");

        Assert.Equal(ErrorCodes.FsShareNotFound, await RefusedAsync(Delete(999, "Media", ShareDataChoice.Keep)));
        Assert.Equal(ErrorCodes.FsShareNotFound, await RefusedAsync(new SharePlanRequest("share.delete", ConfirmName: "Media", Data: ShareDataChoice.Keep)));
        Assert.Equal(ErrorCodes.FsShareNameMismatch, await RefusedAsync(Delete(media, "media", ShareDataChoice.Keep)));
        Assert.Equal(ErrorCodes.FsShareNameMismatch, await RefusedAsync(Delete(media, "", ShareDataChoice.Destroy)));
        Assert.Equal(ErrorCodes.FsShareDataChoiceRequired, await RefusedAsync(Delete(media, "Media", null)));
        Assert.Equal(ErrorCodes.FsShareNoDataset, await RefusedAsync(Delete(folder, "Folder", ShareDataChoice.Destroy)));
        Assert.Equal(ErrorCodes.StorageDatasetNotFound, await RefusedAsync(Delete(gone, "Gone", ShareDataChoice.Destroy)));
        Assert.Equal(ErrorCodes.StorageDatasetProtected, await RefusedAsync(Delete(outside, "Outside", ShareDataChoice.Destroy)));
        var inUse = await Assert.ThrowsAsync<PithyxException>(() => PlanAsync(Delete(media, "Media", ShareDataChoice.Destroy)));
        Assert.Equal((ErrorCodes.StorageDatasetInUseByShare, "Photos 2024"), (inUse.Code, inUse.Parameters["share"]));
        // A folder share can still be removed, its files stay.
        Assert.Empty((await PlanAsync(Delete(folder, " Folder ", ShareDataChoice.Keep))).Plan!.Steps);
    }

    [Fact]
    public async Task ARenamedShareDatasetTakesTheShareRowAlong()
    {
        var id = await AddShareAsync("Media", "/tank/media", "tank/pithyx/shares/media");

        var plan = (await _storage.PlanAsync(new StoragePlanRequest("dataset.rename", "tank", Dataset: "tank/pithyx/shares/media", NewName: "movies"), Token)).Plan!;
        _pools.Replace(new PoolList(
            _pools.Current.Pools,
            [.. Layout().Select(dataset => dataset.Name.StartsWith("tank/pithyx/shares/media", StringComparison.Ordinal)
                ? dataset with { Name = dataset.Name.Replace("/media", "/movies", StringComparison.Ordinal), MountPoint = dataset.MountPoint!.Replace("/media", "/movies", StringComparison.Ordinal) }
                : dataset)]));
        await plan.AfterRun!(Token);

        var share = await _shares.FindAsync(id, Token);
        Assert.Equal(("/tank/movies", "tank/pithyx/shares/movies", "Media"), (share!.Path, share.Dataset, share.Name));
    }

    [Fact]
    public async Task DetailsCarryTheDatasetsSpaceAndCountRightsWithoutAdministrators()
    {
        var media = await AddShareAsync("Media", "/tank/media", "tank/pithyx/shares/media");
        await AddShareAsync("Folder", "/srv/folder");
        await AddShareAsync("Gone", "/tank/gone", "tank/pithyx/shares/gone");
        var admin = Jobs.JobTestSupport.Principal(1, admin: true);
        await _shares.SetAccessAsync(admin, media, 1, ShareAccess.ReadWrite, Token);
        await _shares.SetAccessAsync(admin, media, 2, ShareAccess.Read, Token);

        var details = (await _shares.ListDetailsAsync(admin, _pools.Current, Token)).ToDictionary(share => share.Name);

        Assert.Equal(("tank", 5 * GiB, 100 * GiB, 20 * GiB, 1, 0), (details["Media"].Pool, details["Media"].UsedBytes, details["Media"].AvailableBytes, details["Media"].QuotaBytes, details["Media"].ReadUsers, details["Media"].ReadWriteUsers));
        Assert.Equal((null, null, null), (details["Folder"].Pool, details["Folder"].Dataset, details["Folder"].UsedBytes));
        Assert.Equal(("tank", null), (details["Gone"].Pool, details["Gone"].UsedBytes));
        // Restore points: off until switched on, and none for a folder share.
        Assert.Equal((false, (bool?)null), (details["Media"].RestorePoints, details["Folder"].RestorePoints));
        await _snapshotService.SetSchedulesAsync(SharePlanner.RestorePoints("tank/pithyx/shares/media", enabled: true), Token);
        Assert.True((await _shares.ListDetailsAsync(admin, _pools.Current, Token)).Single(share => share.Name == "Media").RestorePoints);
        await Assert.ThrowsAsync<PithyxException>(() => _shares.ListDetailsAsync(Jobs.JobTestSupport.Principal(2), _pools.Current, Token));
    }

    [Fact]
    public async Task AShareWhosePoolStoppedIsRefusedAsUnavailableAndComesBackWithIt()
    {
        var id = await AddShareAsync("Media", "/tank/media", "tank/pithyx/shares/media");
        var shares = new ShareService(_connections, _time, _pools);
        var admin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1"), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, Pithyx.Daemon.Auth.SessionAuthenticationHandler.AdminRole)],
            "test"));
        Assert.Equal("/tank/media", (await shares.ResolveAsync(admin, id, write: false, Token)).Path);

        _pools.Replace(new PoolList([Pool("tank") with { State = "SUSPENDED" }, Pool("fresh"), Pool("bare")], Layout()));

        var error = await Assert.ThrowsAsync<PithyxException>(() => shares.ResolveAsync(admin, id, write: false, Token));
        Assert.Equal((ErrorCodes.FsShareUnavailable, "pool_unavailable"), (error.Code, error.Parameters["reason"]));
        Assert.Equal("pool_unavailable", (await shares.ListAsync(admin, Token)).Single(share => share.Id == id).Unavailable);

        _pools.Replace(new PoolList([Pool("tank"), Pool("fresh"), Pool("bare")], Layout()));
        Assert.Null((await shares.ListAsync(admin, Token)).Single(share => share.Id == id).Unavailable);
    }

    [Fact]
    public async Task APlanWaitsForZfsAtMostTenSecondsAndThenSaysSo()
    {
        _commands.Hanging.Add("zpool status");

        var plan = _planner.PlanAsync(new SharePlanRequest("share.create", Name: "Family", Pool: "tank"), Token);
        Assert.False(plan.IsCompleted);
        _time.Advance(PlanReads.Limit);

        var error = await Assert.ThrowsAsync<PithyxException>(() => plan);
        Assert.Equal(ErrorCodes.StorageZfsUnavailable, error.Code);
    }

    [Fact]
    public async Task DeleteWithDestroyNeedsAFreshSnapshotList()
    {
        var id = await AddShareAsync("Media", "/tank/media", "tank/pithyx/shares/media");
        _commands.Handler = request => request.Arguments.SequenceEqual(SnapshotPlans.List.Arguments) ? new Pithyx.Daemon.Commands.CommandResult(1, "", "cannot open", TimeSpan.Zero) : null;

        var error = await Assert.ThrowsAsync<PithyxException>(() => _planner.PlanAsync(new SharePlanRequest("share.delete", ShareId: id, ConfirmName: "Media", Data: ShareDataChoice.Destroy), Token));

        Assert.Equal(ErrorCodes.StorageZfsUnavailable, error.Code);
    }
}
