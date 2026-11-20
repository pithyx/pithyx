using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Files.Search;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Tests.Jobs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// The name index (D413) on real OpenZFS as root: fanotify reports changes made past the daemon, the index lives in the
/// pool's cache dataset, and a hold lets a shared folder's dataset be destroyed while the index runs.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class SearchIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChangesMadePastTheDaemonReachTheIndexAndAHoldLetsTheDatasetGo()
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
            var shares = new ShareService(connections, TimeProvider.System, pools);
            var shareChanges = new ShareChanges();
            var datasets = new DatasetPlanner(state, pools, snapshots, snapshotService, shares, TimeProvider.System, NullLogger<DatasetPlanner>.Instance);
            var planner = new SharePlanner(state, pools, shares, shareChanges, datasets, snapshotService, TimeProvider.System, NullLogger<SharePlanner>.Instance);
            var pool = vdevs.PoolName;
            await state.RefreshPoolsAsync(Token);

            using var index = new SearchIndex(shares, shareChanges, pools, TimeProvider.System, null, NullLogger<SearchIndex>.Instance);
            var runner = new CommandPlanRunner(vdevs.Commands, state, facts, TimeProvider.System, search: index);
            var create = await planner.PlanAsync(new SharePlanRequest("share.create", Name: "Holidays", Pool: pool), Token);
            await runner.RunAsync(create.Plan!, null, Token);
            var mountPoint = Path.Combine(vdevs.AltRoot, pool, "holidays");
            await File.WriteAllTextAsync(Path.Combine(mountPoint, "beach.txt"), "sand", Token);
            Directory.CreateDirectory(Path.Combine(mountPoint, "Trips"));
            long id;
            await using (var connection = await connections.OpenAsync(Token))
            {
                id = await connection.ExecuteScalarAsync<long>("SELECT id FROM shares WHERE name = 'Holidays'");
            }

            await index.StartAsync(Token);
            await index.IdleAsync();
            Assert.True(index.Live);
            Assert.Equal(["beach.txt"], Paths(index, id, "beach"));
            Assert.True(File.Exists(Path.Combine(vdevs.AltRoot, pool, ".pithyx", "cache", SearchStore.Folder, SearchStore.FileName)));

            // Written straight to the dataset, as Samba or a container would: only fanotify can tell.
            await File.WriteAllTextAsync(Path.Combine(mountPoint, "Trips", "shell.txt"), "x", Token);
            Directory.CreateDirectory(Path.Combine(mountPoint, "Trips", "Sea"));
            await File.WriteAllTextAsync(Path.Combine(mountPoint, "Trips", "Sea", "sea-shell.txt"), "x", Token);
            File.Move(Path.Combine(mountPoint, "beach.txt"), Path.Combine(mountPoint, "Trips", "beach 2026.txt"));
            await JobTestSupport.WaitUntilAsync(
                async () =>
                {
                    await index.IdleAsync();
                    return Paths(index, id, "shell").SequenceEqual(["Trips/shell.txt", "Trips/Sea/sea-shell.txt"])
                        && Paths(index, id, "beach").SequenceEqual(["Trips/beach 2026.txt"]);
                },
                "the changes in the index");

            var destroy = await planner.PlanAsync(new SharePlanRequest("share.delete", ShareId: id, ConfirmName: "Holidays", Data: ShareDataChoice.Destroy), Token);
            await runner.RunAsync(destroy.Plan!, null, Token);
            shareChanges.Announce();
            await index.IdleAsync();
            Assert.Empty(Paths(index, id, "shell"));

            await index.StopAsync(Token);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] Paths(SearchIndex index, long shareId, string query) =>
        [.. index.Find([shareId], SearchRanking.Terms(query), 50, out _).Select(hit => hit.Path)];
}
