using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// A disk pulled out of a running box, the way the owner tests it on dev01: the kernel deletes the SCSI device
/// (<c>/sys/block/&lt;dev&gt;/device/delete</c>) and a rescan of its host brings it back. The disks are <c>scsi_debug</c>
/// devices in memory, one host each with its own store, so nothing but RAM is touched. There is no ZED on
/// dev01: ZFS notices nothing until its next write, and the daemon must see the disk go on its own.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class DiskFailureIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APulledDiskDegradesItsPoolAtOnceLeavesOtherPoolsAloneAndIsBroughtBackAfterARescan()
    {
        FileVdevs.RequireEnabled();
        await using var disks = await ScsiDebugDisks.CreateAsync(2);
        await using var other = FileVdevs.Create(1);
        await other.CreatePoolAsync();
        var pool = $"{other.PoolName}m";
        await disks.RunAsync("zpool", ["create", "-m", "none", pool, "mirror", .. disks.ByIdPaths]);
        try
        {
            var inventory = new DiskInventory();
            var pools = new PoolInventory();
            var facts = new StorageFacts(inventory, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
            var hub = new TopicHub(new TopicRegistry([inventory.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
            using var state = new SystemStateService(disks.Commands, inventory, pools, new ZfsBackend(disks.Commands), new DiskProbe(disks.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance);
            await state.StartAsync(Token);
            try
            {
                Assert.Equal("ONLINE", StateOf(pools, pool));

                var pulled = await disks.PullAsync(1);
                await WaitForAsync(() => StateOf(pools, pool) == "DEGRADED", "the pool to show DEGRADED after the disk was pulled");

                var leaf = pools.Current.Pools.Single(candidate => candidate.Name == pool).Vdevs.SelectMany(vdev => vdev.Leaves()).Single(candidate => candidate.Missing);
                Assert.Equal(disks.ByIdPaths[1] + "-part1", leaf.Path);
                Assert.Equal("ONLINE", StateOf(pools, other.PoolName));
                // A mirror with one disk left can still be destroyed; the guard only refuses what would hang.
                Assert.Null(PoolHealth.Unreachable(pools.Current.Pools.Single(candidate => candidate.Name == pool)));

                await disks.RescanAsync(pulled);
                await WaitForAsync(
                    () => StateOf(pools, pool) == "ONLINE" && pools.Current.Pools.Single(candidate => candidate.Name == pool).Vdevs.SelectMany(vdev => vdev.Leaves()).All(candidate => !candidate.Missing),
                    "the disk to be brought back after the rescan");
                var status = await disks.RunAsync("zpool", ["status", "-j", "--json-int", pool]);
                Assert.All(ZpoolStatusParser.Parse(status).Single().Vdevs.SelectMany(vdev => vdev.Leaves()), candidate => Assert.Equal("ONLINE", candidate.State));
            }
            finally
            {
                await state.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await disks.ReleaseAsync(pool);
        }
    }

    [Fact]
    public async Task APoolWithoutAnyOfItsDisksIsNotDestroyedSinceZfsWouldHangOnIt()
    {
        FileVdevs.RequireEnabled();
        await using var disks = await ScsiDebugDisks.CreateAsync(1);
        var pool = $"pithyxci-gone-{Environment.ProcessId}";
        await disks.RunAsync("zpool", ["create", "-m", "none", pool, disks.ByIdPaths[0]]);
        var inventory = new DiskInventory();
        var pools = new PoolInventory();
        var facts = new StorageFacts(inventory, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
        var hub = new TopicHub(new TopicRegistry([inventory.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        using var state = new SystemStateService(disks.Commands, inventory, pools, new ZfsBackend(disks.Commands), new DiskProbe(disks.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance);
        await state.StartAsync(Token);
        try
        {
            var pulled = await disks.PullAsync(0);
            await WaitForAsync(() => StateOf(pools, pool) == "UNAVAIL", "the pool to show UNAVAIL after its only disk was pulled");

            var refused = Assert.Throws<PithyxException>(() => state.RequireReachable(pool));
            Assert.Equal(ErrorCodes.StoragePoolDisksMissing, refused.Code);

            // Back again, the pool works and can go; nothing was written while the disk was away, so it never suspended.
            await disks.RescanAsync(pulled);
            await WaitForAsync(() => StateOf(pools, pool) == "ONLINE", "the pool to be back after the rescan");
            state.RequireReachable(pool);
        }
        finally
        {
            await state.StopAsync(CancellationToken.None);
            await disks.ReleaseAsync(pool);
        }
    }

    private static string? StateOf(PoolInventory pools, string name) => pools.Current.Pools.FirstOrDefault(pool => pool.Name == name)?.State;

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100, Token);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary><c>scsi_debug</c> disks, one SCSI host with its own memory store each; disposing unloads the module.</summary>
    private sealed class ScsiDebugDisks : IAsyncDisposable
    {
        private readonly List<(string Host, string Device)> _disks;

        private ScsiDebugDisks(List<(string Host, string Device)> disks, IReadOnlyList<string> byIdPaths)
        {
            _disks = disks;
            ByIdPaths = byIdPaths;
        }

        public ProcessCommandRunner Commands { get; } = new(NullLogger<ProcessCommandRunner>.Instance);

        /// <summary>The disks' stable paths below <c>/dev/disk/by-id</c>, as a pool made by Pithyx names them.</summary>
        public IReadOnlyList<string> ByIdPaths { get; }

        public static async Task<ScsiDebugDisks> CreateAsync(int count)
        {
            var commands = new ProcessCommandRunner(NullLogger<ProcessCommandRunner>.Instance);
            var load = await commands.RunAsync(
                new CommandRequest("modprobe", ["scsi_debug", $"add_host={count}", "per_host_store=1", "num_tgts=1", "dev_size_mb=256"]),
                Token);
            Assert.True(load.ExitCode == 0, $"modprobe scsi_debug failed ({load.StandardError}); it is in linux-modules-extra on some kernels");
            await commands.RunAsync(new CommandRequest("udevadm", ["settle"]), Token);
            var disks = new List<(string Host, string Device)>();
            foreach (var block in Directory.EnumerateDirectories("/sys/block", "sd*"))
            {
                var model = await File.ReadAllTextAsync(Path.Combine(block, "device", "model"), Token);
                if (model.Trim() == "scsi_debug")
                {
                    disks.Add((HostOf(block), Path.GetFileName(block)));
                }
            }
            Assert.Equal(count, disks.Count);
            disks.Sort((left, right) => string.CompareOrdinal(left.Host, right.Host));
            return new ScsiDebugDisks(disks, [.. disks.Select(disk => ByIdPath(disk.Device))]);
        }

        public async Task<string> RunAsync(string command, IReadOnlyList<string> arguments)
        {
            var result = await Commands.RunAsync(new CommandRequest(command, arguments), Token);
            Assert.True(result.ExitCode == 0, $"{command} {string.Join(' ', arguments)} failed with {result.ExitCode}: {result.StandardError}");
            return result.StandardOutput;
        }

        /// <summary>Deletes the disk's SCSI device like pulling it out; returns its host for the rescan.</summary>
        public async Task<string> PullAsync(int index)
        {
            var (host, device) = _disks[index];
            await File.WriteAllTextAsync($"/sys/block/{device}/device/delete", "1", Token);
            await Commands.RunAsync(new CommandRequest("udevadm", ["settle"]), Token);
            return host;
        }

        /// <summary>Scans the host again, like plugging the disk back in; it may come back under another name.</summary>
        public async Task RescanAsync(string host)
        {
            await File.WriteAllTextAsync($"/sys/class/scsi_host/{host}/scan", "- - -", Token);
            await Commands.RunAsync(new CommandRequest("udevadm", ["settle"]), Token);
            for (var i = 0; i < _disks.Count; i++)
            {
                if (_disks[i].Host == host)
                {
                    var block = Directory.EnumerateDirectories("/sys/block", "sd*").Single(candidate => HostOf(candidate) == host);
                    _disks[i] = (host, Path.GetFileName(block));
                }
            }
        }

        /// <summary>
        /// Brings every disk back and resumes the pool before destroying it: <c>zpool destroy</c> on a pool without its
        /// disks would wait in the kernel forever, which is the bug these tests guard against.
        /// </summary>
        public async Task ReleaseAsync(string pool)
        {
            foreach (var host in _disks.Select(disk => disk.Host).Distinct().ToList())
            {
                if (!Directory.EnumerateDirectories("/sys/block", "sd*").Any(block => HostOf(block) == host))
                {
                    await RescanAsync(host);
                }
            }
            await Commands.RunAsync(new CommandRequest("zpool", ["clear", pool], TimeSpan.FromSeconds(30)), CancellationToken.None);
            await Commands.RunAsync(new CommandRequest("zpool", ["destroy", "-f", pool], TimeSpan.FromSeconds(60)), CancellationToken.None);
        }

        private static string HostOf(string block) =>
            new DirectoryInfo(block).ResolveLinkTarget(returnFinalTarget: true)!.FullName.Split('/').Single(part => part.StartsWith("host", StringComparison.Ordinal));

        private static string ByIdPath(string device) =>
            Directory.EnumerateFileSystemEntries("/dev/disk/by-id")
                .Where(link => !link.Contains("-part", StringComparison.Ordinal))
                .Where(link => Path.GetFileName(new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true)!.FullName) == device)
                .Order(StringComparer.Ordinal)
                .First();

        public async ValueTask DisposeAsync()
        {
            await Commands.RunAsync(new CommandRequest("udevadm", ["settle"]), CancellationToken.None);
            await Commands.RunAsync(new CommandRequest("modprobe", ["-r", "scsi_debug"]), CancellationToken.None);
        }
    }
}
