using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// Shared folder plans as the daemon runs them (share planner, plan runner, bookkeeping), against real OpenZFS on a file
/// pool with the Pithyx layout.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class ShareIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASharedFolderIsADatasetMountedBelowThePoolAndGoesWithItsSnapshots()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var directory = Directory.CreateTempSubdirectory("pithyxci-db-").FullName;
        try
        {
            var connections = new SqliteConnectionFactory(Path.Combine(directory, "pithyx.db"));
            await new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
            var pools = new PoolInventory();
            var snapshots = new SnapshotInventory();
            var disks = new DiskInventory();
            var facts = new StorageFacts(disks, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
            var hub = new TopicHub(new TopicRegistry([disks.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
            using var state = new SystemStateService(vdevs.Commands, disks, pools, new ZfsBackend(vdevs.Commands), new DiskProbe(vdevs.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance);
            using var snapshotService = new SnapshotService(vdevs.Commands, snapshots, pools, connections, TimeProvider.System, NullLogger<SnapshotService>.Instance);
            var shares = new ShareService(connections, TimeProvider.System);
            var datasets = new DatasetPlanner(state, pools, snapshots, snapshotService, shares, TimeProvider.System, NullLogger<DatasetPlanner>.Instance);
            var planner = new SharePlanner(state, pools, shares, new ShareChanges(), datasets, snapshotService, TimeProvider.System, NullLogger<SharePlanner>.Instance);
            var runner = new CommandPlanRunner(vdevs.Commands, state, facts, TimeProvider.System);
            var pool = vdevs.PoolName;

            var create = await planner.PlanAsync(new SharePlanRequest("share.create", Name: "Family Photos", Pool: pool, Description: "Holidays"), Token);
            Assert.Equal([$"{pool}/pithyx/shares/family-photos"], create.Datasets);
            await runner.RunAsync(create.Plan!, null, Token);

            var mountPoint = Path.Combine(vdevs.AltRoot, pool, "family-photos");
            var dataset = pools.Current.Datasets.Single(candidate => candidate.Name == $"{pool}/pithyx/shares/family-photos");
            Assert.Equal((mountPoint, true), (dataset.MountPoint, dataset.Mounted));
            long id;
            await using (var connection = await connections.OpenAsync(Token))
            {
                var row = await connection.QuerySingleAsync<(long Id, string Path, string Dataset, string Description)>("SELECT id, path, dataset, description FROM shares WHERE name = 'Family Photos'");
                Assert.Equal((mountPoint, dataset.Name, "Holidays"), (row.Path, row.Dataset, row.Description));
                id = row.Id;
            }
            await File.WriteAllTextAsync(Path.Combine(mountPoint, "beach.txt"), "sand", Token);
            await vdevs.RunAsync("zfs", ["snapshot", $"{dataset.Name}@before"]);

            var destroy = await planner.PlanAsync(new SharePlanRequest("share.delete", ShareId: id, ConfirmName: "Family Photos", Data: ShareDataChoice.Destroy), Token);
            Assert.Equal([["zfs", "destroy", "-r", dataset.Name]], destroy.Plan!.Steps.Select(step => (string[])[step.Command.FileName, .. step.Command.Arguments]));
            Assert.Equal([dataset.Name, $"{dataset.Name}@before"], destroy.Datasets);
            await runner.RunAsync(destroy.Plan, null, Token);

            Assert.DoesNotContain(pools.Current.Datasets, candidate => candidate.Name == dataset.Name);
            Assert.Null(await shares.FindAsync(id, Token));
            Assert.Contains(pools.Current.Datasets, candidate => candidate.Name == $"{pool}/pithyx/shares");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
