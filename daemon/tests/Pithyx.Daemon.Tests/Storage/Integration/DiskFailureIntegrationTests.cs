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
/// A disk pulled out of a running box, the way a maintainer tests it on a dev VM: the kernel deletes the SCSI device
/// (<c>/sys/block/&lt;dev&gt;/device/delete</c>) and a rescan of its host brings it back. The disks are <c>scsi_debug</c>
/// devices in memory, one host each with its own store, so nothing but RAM is touched. There is no ZED on
/// devbox: ZFS notices nothing until its next write, and the daemon must see the disk go on its own.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class DiskFailureIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APulledDiskDegradesItsPoolAtOnceLeavesOtherPoolsAloneAndIsBroughtBackAfterARescan()
    {
        RequireDiskPulls();
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
                // ZFS may report the lost leaf before the disk inventory drops the device; the daemon shows both.
                await WaitForAsync(
                    () => StateOf(pools, pool) == "DEGRADED" && pools.Current.Pools.Single(candidate => candidate.Name == pool).Vdevs.SelectMany(vdev => vdev.Leaves()).Any(candidate => candidate.Missing),
                    "the pool to show DEGRADED with the pulled disk missing");

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
        RequireDiskPulls();
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
            // UNAVAIL while nothing was written; a write that happened to go out meanwhile suspends the pool instead.
            await WaitForAsync(() => StateOf(pools, pool) is "UNAVAIL" or "SUSPENDED", "the pool to show UNAVAIL or SUSPENDED after its only disk was pulled");

            var refused = Assert.Throws<PithyxException>(() => state.RequireReachable(pool));
            Assert.Contains(refused.Code, new[] { ErrorCodes.StoragePoolDisksMissing, ErrorCodes.StoragePoolSuspended });

            // Back again, the daemon reopens the disk (and resumes the pool if it suspended); then the pool can go.
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

    /// <summary>
    /// Pulling a disk that ZFS still writes to can leave a transaction group waiting in the kernel for I/O that never
    /// returns (seen on a dev VM with OpenZFS 2.3.9); every later zpool command then waits until a reboot. These tests run
    /// only on a freshly booted dev VM with <c>PITHYX_DISK_PULL_TESTS=1</c> (<c>integration.sh --disk-pulls</c>).
    /// </summary>
    private static void RequireDiskPulls()
    {
        FileVdevs.RequireEnabled();
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("PITHYX_DISK_PULL_TESTS") == "1",
            "Disk pull tests can hang ZFS until a reboot; run them with integration.sh --disk-pulls on a fresh boot.");
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
            await File.WriteAllTextAsync($"/sys/class/scsi_host/{host}/scan", "- - -", CancellationToken.None);
            // The kernel attaches the disk after the scan returns; udevadm settle only waits for events already queued.
            string? block = null;
            for (var attempt = 0; attempt < 100 && block is null; attempt++)
            {
                await Commands.RunAsync(new CommandRequest("udevadm", ["settle"]), CancellationToken.None);
                block = Directory.EnumerateDirectories("/sys/block", "sd*").FirstOrDefault(candidate => HostOf(candidate) == host);
                if (block is null)
                {
                    await Task.Delay(100, CancellationToken.None);
                }
            }
            Assert.True(block is not null, $"no disk came back on {host} after the rescan");
            for (var i = 0; i < _disks.Count; i++)
            {
                if (_disks[i].Host == host)
                {
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
            // Until the pool is gone, disposing must not unload the module under it.
            _leftOver = true;
            foreach (var host in _disks.Select(disk => disk.Host).Distinct().ToList())
            {
                if (!Directory.EnumerateDirectories("/sys/block", "sd*").Any(block => HostOf(block) == host))
                {
                    await RescanAsync(host);
                }
            }
            // zpool clear reopens a disk ZFS still holds under its old device and resumes a suspended pool. A pool that
            // stays suspended is never destroyed: that would wait in the kernel.
            var steps = new List<string> { await TryAsync("zpool", ["clear", pool]) };
            var state = "";
            for (var attempt = 0; attempt < 50; attempt++)
            {
                state = (await File.ReadAllTextAsync($"/proc/spl/kstat/zfs/{pool}/state", CancellationToken.None)).Trim();
                if (state != "SUSPENDED")
                {
                    break;
                }
                await Task.Delay(200, CancellationToken.None);
            }
            if (state != "SUSPENDED")
            {
                steps.Add(await TryAsync("zpool", ["destroy", "-f", pool]));
            }
            _leftOver = Directory.Exists($"/proc/spl/kstat/zfs/{pool}");
            Assert.False(_leftOver, $"the test pool {pool} could not be removed (state {state}): {string.Join("; ", steps)}");
        }

        private static string HostOf(string block) =>
            new DirectoryInfo(block).ResolveLinkTarget(returnFinalTarget: true)!.FullName.Split('/').Single(part => part.StartsWith("host", StringComparison.Ordinal));

        private static string ByIdPath(string device) =>
            Directory.EnumerateFileSystemEntries("/dev/disk/by-id")
                .Where(link => !link.Contains("-part", StringComparison.Ordinal))
                .Where(link => Path.GetFileName(new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true)!.FullName) == device)
                .Order(StringComparer.Ordinal)
                .First();

        private bool _leftOver;

        private async Task<string> TryAsync(string command, IReadOnlyList<string> arguments)
        {
            try
            {
                var result = await Commands.RunAsync(new CommandRequest(command, arguments, TimeSpan.FromSeconds(30)), CancellationToken.None);
                return $"{command} {string.Join(' ', arguments)}: exit {result.ExitCode} {result.StandardError.Trim()}";
            }
            catch (TimeoutException)
            {
                return $"{command} {string.Join(' ', arguments)}: timed out";
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Unloading the module under a pool that is still there would leave it without disks until a reboot.
            if (_leftOver)
            {
                return;
            }
            await Commands.RunAsync(new CommandRequest("udevadm", ["settle"]), CancellationToken.None);
            await Commands.RunAsync(new CommandRequest("modprobe", ["-r", "scsi_debug"]), CancellationToken.None);
        }
    }
}
