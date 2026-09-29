using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// Dataset and volume plans as the daemon runs them (planner, plan runner, bookkeeping), against real OpenZFS on a
/// file pool with the Pithyx layout. <c>PITHYX_RECORD_FIXTURES</c> names a scenario folder to record the dataset list
/// of the volume test into.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class DatasetIntegrationTests
{
    private const long MiB = 1024 * 1024;

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The daemon's storage services with the real command runner and a fresh database.</summary>
    private sealed class Daemon : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("pithyxci-db-").FullName;
        private readonly SystemStateService _state;
        private readonly CommandPlanRunner _runner;
        private readonly StoragePlanner _planner;

        public Daemon(FileVdevs vdevs)
        {
            Vdevs = vdevs;
            Connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
            var disks = new DiskInventory();
            var facts = new StorageFacts(disks, Pools, DeploymentEnvironment.Prod, SystemFiles.Host);
            var hub = new TopicHub(new TopicRegistry([disks.Definition(), Pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
            _state = new SystemStateService(vdevs.Commands, disks, Pools, new ZfsBackend(vdevs.Commands), new DiskProbe(vdevs.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance);
            Snapshots = new SnapshotService(vdevs.Commands, SnapshotList, Pools, Connections, TimeProvider.System, NullLogger<SnapshotService>.Instance);
            var datasets = new DatasetPlanner(_state, Pools, SnapshotList, Snapshots, new ShareService(Connections, TimeProvider.System), TimeProvider.System, NullLogger<DatasetPlanner>.Instance);
            _planner = new StoragePlanner(_state, facts, Pools, new PoolImportScanner(vdevs.Commands, _state, facts, Pools), datasets, vdevs.Commands, TimeProvider.System);
            _runner = new CommandPlanRunner(vdevs.Commands, _state, facts, TimeProvider.System);
        }

        public FileVdevs Vdevs { get; }

        public SqliteConnectionFactory Connections { get; }

        public PoolInventory Pools { get; } = new();

        public SnapshotInventory SnapshotList { get; } = new();

        public SnapshotService Snapshots { get; }

        public string Pool => Vdevs.PoolName;

        public static async Task<Daemon> StartAsync(FileVdevs vdevs)
        {
            await vdevs.CreateLayoutPoolAsync();
            var daemon = new Daemon(vdevs);
            await new MigrationRunner(daemon.Connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
            return daemon;
        }

        public Task<StoragePlanner.Result> PlanAsync(StoragePlanRequest request) => _planner.PlanAsync(request, Token);

        public async Task<StoragePlanner.Result> RunAsync(StoragePlanRequest request)
        {
            var result = await PlanAsync(request);
            await _runner.RunAsync(result.Plan!, null, Token);
            return result;
        }

        /// <summary>The datasets of the test pool, read the way the daemon reads them.</summary>
        public async Task<Dictionary<string, Dataset>> DatasetsAsync() =>
            (await new ZfsBackend(Vdevs.Commands).ReadAsync(Token)).Datasets
                .Where(dataset => dataset.Pool == Pool)
                .ToDictionary(dataset => dataset.Name, StringComparer.Ordinal);

        public Task<string> CreateShareDatasetAsync(string name) => Vdevs.RunAsync("zfs", [.. ZfsPlans.CreateShareDataset(Pool, name)[0].Command.Arguments]);

        public async ValueTask DisposeAsync()
        {
            Snapshots.Dispose();
            _state.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ARenamedShareDatasetMountsAtItsNewNameAndItsShareFollows()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await using var daemon = await Daemon.StartAsync(vdevs);
        await daemon.CreateShareDatasetAsync("media");
        var before = (await daemon.DatasetsAsync())[$"{daemon.Pool}/pithyx/shares/media"];
        Assert.Equal(Path.Combine(vdevs.AltRoot, daemon.Pool, "media"), before.MountPoint);
        await File.WriteAllTextAsync(Path.Combine(before.MountPoint!, "note.txt"), "kept", Token);
        await using (var connection = await daemon.Connections.OpenAsync(Token))
        {
            await connection.ExecuteAsync(
                "INSERT INTO shares (name, path, created_at, updated_at) VALUES ('Media', @path, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')",
                new { path = before.MountPoint });
        }

        await daemon.RunAsync(new StoragePlanRequest("dataset.rename", daemon.Pool, Dataset: $"{daemon.Pool}/pithyx/shares/media", NewName: "movies"));

        var datasets = await daemon.DatasetsAsync();
        Assert.DoesNotContain($"{daemon.Pool}/pithyx/shares/media", datasets.Keys);
        var after = datasets[$"{daemon.Pool}/pithyx/shares/movies"];
        var moved = Path.Combine(vdevs.AltRoot, daemon.Pool, "movies");
        Assert.Equal((moved, true), (after.MountPoint, after.Mounted));
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(moved, "note.txt"), Token));
        await using (var connection = await daemon.Connections.OpenAsync(Token))
        {
            Assert.Equal(moved, await connection.ExecuteScalarAsync<string>("SELECT path FROM shares WHERE name = 'Media'"));
        }
    }

    [Fact]
    public async Task QuotaReservationCompressionAndRecordSizeAreSetAndReadBack()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await using var daemon = await Daemon.StartAsync(vdevs);
        await daemon.CreateShareDatasetAsync("media");
        var name = $"{daemon.Pool}/pithyx/shares/media";

        var set = await daemon.RunAsync(new StoragePlanRequest("dataset.set", daemon.Pool, Dataset: name, QuotaBytes: 64 * MiB, ReservationBytes: 16 * MiB, Compression: "zstd-3", RecordSizeBytes: MiB));

        Assert.Equal(["set", "quota=67108864", "reservation=16777216", "compression=zstd-3", "recordsize=1048576", name], set.Plan!.Steps[0].Command.Arguments);
        var media = (await daemon.DatasetsAsync())[name];
        Assert.Equal((64 * MiB, 16 * MiB, "zstd-3", MiB), (media.QuotaBytes, media.ReservationBytes, media.Compression, media.RecordSizeBytes));

        await daemon.RunAsync(new StoragePlanRequest("dataset.set", daemon.Pool, Dataset: name, QuotaBytes: 0, ReservationBytes: 0));

        var cleared = (await daemon.DatasetsAsync())[name];
        Assert.Equal(((long?)null, (long?)null), (cleared.QuotaBytes, cleared.ReservationBytes));
        Assert.Equal(
            ErrorCodes.StorageDatasetReservationTooLarge,
            (await Assert.ThrowsAsync<PithyxException>(() => daemon.PlanAsync(new StoragePlanRequest("dataset.set", daemon.Pool, Dataset: name, ReservationBytes: 4096 * MiB)))).Code);
    }

    [Fact]
    public async Task DestroyTakesChildrenAndSnapshotsItListedButNotHeldOnes()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await using var daemon = await Daemon.StartAsync(vdevs);
        await daemon.CreateShareDatasetAsync("media");
        await daemon.CreateShareDatasetAsync("docs");
        var media = $"{daemon.Pool}/pithyx/shares/media";
        var docs = $"{daemon.Pool}/pithyx/shares/docs";
        await vdevs.RunAsync("zfs", ["create", $"{media}/photos"]);
        await vdevs.RunAsync("zfs", [.. SnapshotPlans.Take($"{media}/photos", "before").Arguments]);
        await vdevs.RunAsync("zfs", [.. SnapshotPlans.Take(docs, "pithyx-manual-20260928T120000Z").Arguments]);
        await vdevs.RunAsync("zfs", [.. SnapshotPlans.Hold(docs, "pithyx-manual-20260928T120000Z").Arguments]);

        var destroyed = await daemon.RunAsync(new StoragePlanRequest("dataset.destroy", daemon.Pool, ConfirmName: media, Dataset: media));

        Assert.Equal(["destroy", "-r", media], destroyed.Plan!.Steps[0].Command.Arguments);
        Assert.Equal([media, $"{media}/photos", $"{media}/photos@before"], destroyed.Datasets);
        var left = await daemon.DatasetsAsync();
        Assert.DoesNotContain(left.Keys, dataset => dataset.StartsWith(media, StringComparison.Ordinal));
        var held = await Assert.ThrowsAsync<PithyxException>(() => daemon.PlanAsync(new StoragePlanRequest("dataset.destroy", daemon.Pool, ConfirmName: docs, Dataset: docs)));
        Assert.Equal((ErrorCodes.StorageSnapshotHeld, $"{docs}@pithyx-manual-20260928T120000Z"), (held.Code, held.Parameters["name"]));
        Assert.Contains(docs, left.Keys);
    }

    [Fact]
    public async Task SparseAndThickVolumesAreCreatedAndDestroyed()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await using var daemon = await Daemon.StartAsync(vdevs);
        var volumes = $"{daemon.Pool}/pithyx/volumes";

        var thick = await daemon.RunAsync(new StoragePlanRequest("volume.create", daemon.Pool, Name: "thick", SizeBytes: (32 * MiB) + 1));
        Assert.Equal(2, thick.Plan!.Steps.Count);
        var sparse = await daemon.RunAsync(new StoragePlanRequest("volume.create", daemon.Pool, Name: "sparse", SizeBytes: 1024 * MiB, Sparse: true));
        Assert.Single(sparse.Plan!.Steps);
        Assert.Equal(
            ErrorCodes.StorageVolumeTooLarge,
            (await Assert.ThrowsAsync<PithyxException>(() => daemon.PlanAsync(new StoragePlanRequest("volume.create", daemon.Pool, Name: "huge", SizeBytes: 1024 * MiB)))).Code);
        await daemon.CreateShareDatasetAsync("media");
        await daemon.RunAsync(new StoragePlanRequest("dataset.set", daemon.Pool, Dataset: $"{daemon.Pool}/pithyx/shares/media", QuotaBytes: 64 * MiB, Compression: "zstd"));

        var datasets = await daemon.DatasetsAsync();
        Assert.Equal(("volume", (32 * MiB) + (16 * 1024), 16 * 1024L), (datasets[$"{volumes}/thick"].Type, datasets[$"{volumes}/thick"].VolumeSizeBytes, datasets[$"{volumes}/thick"].VolumeBlockSizeBytes));
        Assert.Equal(("volume", 1024 * MiB), (datasets[$"{volumes}/sparse"].Type, datasets[$"{volumes}/sparse"].VolumeSizeBytes));
        // A thick volume holds its whole size; a sparse one only what was written.
        Assert.True(datasets[$"{volumes}/thick"].UsedBytes >= 32 * MiB);
        Assert.True(datasets[$"{volumes}/sparse"].UsedBytes < MiB);
        Assert.Null(datasets[volumes].MountPoint);
        await RecordAsync(vdevs);

        foreach (var volume in new[] { $"{volumes}/thick", $"{volumes}/sparse" })
        {
            var destroyed = await daemon.RunAsync(new StoragePlanRequest("dataset.destroy", daemon.Pool, ConfirmName: volume, Dataset: volume));
            Assert.Equal(["destroy", volume], destroyed.Plan!.Steps[0].Command.Arguments);
        }
        Assert.Equal([volumes], (await daemon.DatasetsAsync()).Keys.Where(name => name.StartsWith(volumes, StringComparison.Ordinal)));
    }

    /// <summary>Writes the daemon's dataset list as a fixture, in the format of <c>tools/fixtures/record.sh</c>.</summary>
    private static async Task RecordAsync(FileVdevs vdevs)
    {
        if (Environment.GetEnvironmentVariable("PITHYX_RECORD_FIXTURES") is not { Length: > 0 } scenario)
        {
            return;
        }
        var result = await vdevs.Commands.RunAsync(ZfsBackend.Datasets, Token);
        Directory.CreateDirectory(scenario);
        await File.WriteAllTextAsync(Path.Combine(scenario, "zfs-list.stdout"), result.StandardOutput, Token);
        var metadata = new { command = ZfsBackend.Datasets.FileName, args = ZfsBackend.Datasets.Arguments, exitCode = result.ExitCode };
        await File.WriteAllTextAsync(Path.Combine(scenario, "zfs-list.json"), JsonSerializer.Serialize(metadata, _indented) + "\n", Token);
    }
}
