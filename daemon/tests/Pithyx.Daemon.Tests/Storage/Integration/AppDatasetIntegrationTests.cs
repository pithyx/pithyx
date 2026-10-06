using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Tests.Apps.Packages;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// An app's dataset on real OpenZFS (D301): created below the pool's <c>apps</c> and mounted at
/// <c>.pithyx/apps/&lt;id&gt;</c>, a failed update rolled back to its held snapshot (volumes included), the snapshot gone
/// afterwards either way, and the dataset destroyed with its data.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class AppDatasetIntegrationTests
{
    private const string AppId = "com.example.probe";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<UnpackedPackage> PackageAsync(string directory, string version, string content)
    {
        var bytes = TarBuilder.MinimalPackage(AppId, version).File("frontend/marker.txt", content).End().Gzip();
        return await PackageReader.UnpackAsync(new MemoryStream(bytes), Path.Combine(directory, "staging-" + version + "-" + content + "-" + Guid.NewGuid().ToString("N")), PackageLimits.Default, Token);
    }

    [Fact]
    public async Task AnAppDatasetIsCreatedRolledBackAfterAFailedUpdateAndDestroyed()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var pool = vdevs.PoolName;
        var pools = new PoolInventory();
        var disks = new DiskInventory();
        var facts = new StorageFacts(disks, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
        var hub = new TopicHub(new TopicRegistry([disks.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        using var state = new SystemStateService(vdevs.Commands, disks, pools, new ZfsBackend(vdevs.Commands), new DiskProbe(vdevs.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance);
        await state.RefreshPoolsAsync(Token);
        var storage = new AppStorage(vdevs.Commands, pools, state, new AppStorageOptions(UseZfs: true, "/"), TimeProvider.System);
        var staging = Directory.CreateTempSubdirectory("pithyxci-app-").FullName;
        try
        {
            await AppVersions.InstallAsync(storage, pool, AppId, await PackageAsync(staging, "1.0.0", "one"), "1.0.0", update: false, _ => Task.CompletedTask, Token);
            var dataset = pools.Current.Datasets.Single(candidate => candidate.Name == ZfsPlans.AppDataset(pool, AppId));
            Assert.Equal(Path.Combine(vdevs.AltRoot, pool, ".pithyx", "apps", AppId), dataset.MountPoint);
            var root = storage.RootOf(pool, AppId)!;
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(root, "versions", "1.0.0", "frontend", "marker.txt"), Token));
            await File.WriteAllTextAsync(Path.Combine(root, "volumes", "data.txt"), "before", Token);

            // The update changes the volumes too, then its registry commit fails: everything returns to the snapshot.
            var next = await PackageAsync(staging, "2.0.0", "two");
            await Assert.ThrowsAsync<PithyxException>(() => AppVersions.InstallAsync(storage, pool, AppId, next, "2.0.0", update: true, async _ =>
            {
                await File.WriteAllTextAsync(Path.Combine(root, "volumes", "data.txt"), "migrated", Token);
                Assert.Contains("@pithyx-update-", await vdevs.RunAsync("zfs", ["list", "-H", "-t", "snapshot", "-o", "name,userrefs", "-r", ZfsPlans.AppDataset(pool, AppId)]), StringComparison.Ordinal);
                throw new PithyxException(ErrorCodes.SystemInternalUnexpected);
            }, Token));
            Assert.Equal(["1.0.0"], Directory.GetFileSystemEntries(Path.Combine(root, "versions")).Select(Path.GetFileName));
            Assert.Equal("versions/1.0.0", new FileInfo(Path.Combine(root, "current")).LinkTarget);
            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(root, "volumes", "data.txt"), Token));
            Assert.Equal("", (await vdevs.RunAsync("zfs", ["list", "-H", "-t", "snapshot", "-o", "name", "-r", ZfsPlans.AppDataset(pool, AppId)])).Trim());

            await AppVersions.InstallAsync(storage, pool, AppId, await PackageAsync(staging, "2.0.0", "two"), "2.0.0", update: true, _ => Task.CompletedTask, Token);
            Assert.Equal(["2.0.0"], Directory.GetFileSystemEntries(Path.Combine(root, "versions")).Select(Path.GetFileName));
            Assert.Equal("", (await vdevs.RunAsync("zfs", ["list", "-H", "-t", "snapshot", "-o", "name", "-r", ZfsPlans.AppDataset(pool, AppId)])).Trim());

            await storage.DestroyAsync(pool, AppId, Token);
            Assert.DoesNotContain(pools.Current.Datasets, candidate => candidate.Name == ZfsPlans.AppDataset(pool, AppId));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }
}
