using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// The ZFS backend, the plan commands and the disk probe against real OpenZFS on file vdevs, so the parsers and
/// argument lists are checked against the tool itself and not only against recorded output.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class ZfsIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<Pool> ReadPoolAsync(FileVdevs vdevs) =>
        Assert.Single((await new ZfsBackend(vdevs.Commands).ReadAsync(Token)).Pools, pool => pool.Name == vdevs.PoolName);

    [Fact]
    public async Task ReadsARaidzOnFileVdevs()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(3);
        await vdevs.CreatePoolAsync("raidz1");

        var list = await new ZfsBackend(vdevs.Commands).ReadAsync(Token);

        var pool = Assert.Single(list.Pools, pool => pool.Name == vdevs.PoolName);
        Assert.Equal("ONLINE", pool.State);
        var raidz = Assert.Single(pool.Vdevs);
        Assert.Equal(("raidz", "data", 1), (raidz.Type, raidz.Role, raidz.ParityDisks));
        Assert.Equal(vdevs.Files, raidz.Children.Select(child => child.Path));
        Assert.All(raidz.Children, child => Assert.Equal(("file", "ONLINE"), (child.Type, child.State)));
        Assert.True(pool.SizeBytes > 0 && pool.FreeBytes > 0 && pool.FreeBytes <= pool.SizeBytes);
        Assert.Contains(list.Datasets, dataset => dataset.Name == vdevs.PoolName && dataset.Pool == vdevs.PoolName);
    }

    [Fact]
    public async Task AnOfflineVdevDegradesThePool()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(3);
        await vdevs.CreatePoolAsync("raidz1");

        await vdevs.RunAsync("zpool", ["offline", vdevs.PoolName, vdevs.Files[0]]);

        var pool = await ReadPoolAsync(vdevs);
        Assert.Equal("DEGRADED", pool.State);
        var leaves = pool.Vdevs.SelectMany(vdev => vdev.Leaves()).ToDictionary(leaf => leaf.Path!, leaf => leaf.State);
        Assert.Equal(["OFFLINE", "ONLINE", "ONLINE"], vdevs.Files.Select(file => leaves[file]));
    }

    [Fact]
    public async Task ZpoolEventsRefreshThePoolInventory()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(3);
        await vdevs.CreatePoolAsync("raidz1");
        var disks = new DiskInventory();
        var pools = new PoolInventory();
        var facts = new StorageFacts(disks, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
        var hub = new TopicHub(
            new TopicRegistry([disks.Definition(), pools.Definition()]),
            TimeProvider.System,
            Options.Create(new JsonOptions()),
            NullLogger<TopicHub>.Instance);
        using var state = new SystemStateService(
            vdevs.Commands,
            disks,
            pools,
            new ZfsBackend(vdevs.Commands),
            new DiskProbe(vdevs.Commands),
            facts,
            hub,
            TimeProvider.System,
            NullLogger<SystemStateService>.Instance);
        await state.StartAsync(Token);
        try
        {
            Assert.Equal("ONLINE", PoolState(pools, vdevs.PoolName));
            // zpool events -f first replays the event history; the offline must arrive as a new event.
            await Task.Delay(TimeSpan.FromSeconds(1), Token);

            await vdevs.RunAsync("zpool", ["offline", vdevs.PoolName, vdevs.Files[1]]);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (PoolState(pools, vdevs.PoolName) != "DEGRADED" && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), Token);
            }
            Assert.Equal("DEGRADED", PoolState(pools, vdevs.PoolName));
        }
        finally
        {
            await state.StopAsync(CancellationToken.None);
        }
    }

    private static string? PoolState(PoolInventory pools, string name) =>
        pools.Current.Pools.SingleOrDefault(pool => pool.Name == name)?.State;

    [Theory]
    [InlineData("mirror", 2)]
    [InlineData("raidz1", 3)]
    public async Task ZpoolAcceptsTheCreateCommandOfAPlan(string layout, int count)
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(count);
        var disks = vdevs.Files.Select((file, index) => new GuardDisk(
            new Disk($"path-file{index}", $"file{index}", file, null, null, null, null, new FileInfo(file).Length, false, null, false, false, []),
            new BlockDevice($"file{index}", file, null, $"0:{index}", "disk", null, [], []),
            new DiskSignatures([])));

        var create = Assert.Single(ZfsPlans.CreatePool(vdevs.PoolName, layout, [.. disks])).Command;

        Assert.Equal("create", create.Arguments[0]);
        await vdevs.RunAsync(create.FileName, ["create", "-n", .. create.Arguments.Skip(1)]);
    }

    [Fact]
    public async Task TheProbeFindsTheLabelsADestroyedPoolLeavesBehind()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(2);
        await vdevs.RunAsync("zpool", ["create", "-m", "none", vdevs.PoolName, vdevs.Files[0]]);
        await vdevs.RunAsync("zpool", ["destroy", vdevs.PoolName]);
        // Attached only now, so the loop devices never cache blocks from before the pool existed.
        var used = await vdevs.AttachLoopDeviceAsync(vdevs.Files[0]);
        var blank = await vdevs.AttachLoopDeviceAsync(vdevs.Files[1]);
        var probe = new DiskProbe(vdevs.Commands);

        Assert.Contains("zfs_member", (await probe.ProbeAsync(used, Token)).Types);
        Assert.Empty((await probe.ProbeAsync(blank, Token)).Types);
    }
}
