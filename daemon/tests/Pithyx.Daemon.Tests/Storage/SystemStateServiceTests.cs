using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class SystemStateServiceTests : IAsyncDisposable
{
    private static readonly string _allDisks = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "commands", "default", "lsblk.stdout"));

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ScriptedCommandRunner _commands = new();
    private readonly DiskInventory _inventory = new();
    private readonly PoolInventory _pools = new();
    private readonly TopicHub _hub;
    private readonly SystemStateService _service;
    private readonly TopicHub.Connection _subscriber;
    private readonly string _kstat = Directory.CreateTempSubdirectory("pithyx-kstat-").FullName;

    public SystemStateServiceTests()
    {
        _hub = new TopicHub(new TopicRegistry([_inventory.Definition(), _pools.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        _service = new SystemStateService(
            _commands,
            _inventory,
            _pools,
            new ZfsBackend(_commands),
            new DiskProbe(_commands),
            new StorageFacts(_inventory, _pools, DeploymentEnvironment.Prod, SystemFiles.Host with { ZfsKstat = _kstat }),
            _hub,
            _time,
            NullLogger<SystemStateService>.Instance);
        // Disks and pools are for administrators only.
        _subscriber = _hub.Connect(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, SessionAuthenticationHandler.AdminRole)], "test")), "s");
        SetDisks(_allDisks);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        Directory.Delete(_kstat, recursive: true);
    }

    /// <summary>The default fixture's pools: the mirror pithyxtestm on VYXTEST04 and 05 (sde, sdf), pithyxtests on VYXTEST06 (sdg).</summary>
    private void SetPools()
    {
        foreach (var (key, id) in new[] { ("zpool status", "zpool-status"), ("zpool list", "zpool-list"), ("zfs list", "zfs-list") })
        {
            _commands.Results[key] = new CommandResult(0, StorageFixtures.Read("default", id), "", TimeSpan.Zero);
        }
        _commands.Results["zpool online"] = new CommandResult(0, "", "", TimeSpan.Zero);
        _commands.Results["zpool clear"] = new CommandResult(0, "", "", TimeSpan.Zero);
    }

    private async Task<PoolList> NextPoolsAsync()
    {
        while (true)
        {
            var next = NextEventAsync();
            await AdvanceUntilAsync(next);
            var message = await next;
            if (message.GetProperty("topic").GetString() == PoolInventory.Topic)
            {
                return _pools.Current;
            }
        }
    }

    private static Vdev LeafOn(PoolList pools, string pool, string drive) =>
        pools.Pools.Single(candidate => candidate.Name == pool).Vdevs.SelectMany(vdev => vdev.Leaves()).Single(leaf => leaf.Path!.Contains(drive, StringComparison.Ordinal));

    private void SetDisks(string lsblkJson) => _commands.Results["lsblk"] = new CommandResult(0, lsblkJson, "", TimeSpan.Zero);

    private static string Without(string lsblkJson, string name)
    {
        var document = JsonSerializer.Deserialize<Dictionary<string, List<JsonElement>>>(lsblkJson)!;
        document["blockdevices"].RemoveAll(device => device.GetProperty("name").GetString() == name);
        return JsonSerializer.Serialize(document);
    }

    private async Task<JsonElement> NextEventAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var payload = await _subscriber.Outbox.Reader.ReadAsync(timeout.Token);
            var message = JsonDocument.Parse(payload).RootElement.Clone();
            if (message.GetProperty("type").GetString() == "event")
            {
                return message;
            }
        }
    }

    private async Task AdvanceUntilAsync(Task task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            _time.Advance(SystemStateService.Debounce);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ReadsTheDisksBeforeTheDaemonAcceptsRequests()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, _inventory.Current.Disks.Count);
    }

    [Fact]
    public async Task UdevEventLeadsToAnEventWithTheRemovedDisk()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);
        await _hub.SubscribeAsync(_subscriber, "c1", DiskInventory.Topic, TestContext.Current.CancellationToken);

        SetDisks(Without(_allDisks, "sdg"));
        _commands.Push("UDEV  [12698.860548] remove   /devices/pci0000:00/.../block/sdg (block)");
        var next = NextEventAsync();
        await AdvanceUntilAsync(next);
        var message = await next;

        Assert.Equal(DiskInventory.Topic, message.GetProperty("topic").GetString());
        Assert.Equal(["serial-VYXTEST06"], message.GetProperty("data").GetProperty("removals").EnumerateArray().Select(r => r.GetString()));
        Assert.Empty(message.GetProperty("data").GetProperty("upserts").EnumerateArray());
        Assert.Equal(6, _inventory.Current.Disks.Count);
    }

    [Fact]
    public async Task ABurstOfEventsReadsTheDisksOnce()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);
        var before = _commands.Calls.Count(c => c.FileName == "lsblk");

        for (var i = 0; i < 5; i++)
        {
            _commands.Push($"UDEV  [1.{i}] change   /devices/.../block/sdg (block)");
        }
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _time.Advance(SystemStateService.Debounce);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _time.Advance(SystemStateService.Debounce);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.InRange(_commands.Calls.Count(c => c.FileName == "lsblk") - before, 1, 2);
    }

    [Fact]
    public async Task UnchangedDisksPublishNothing()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);
        await _hub.SubscribeAsync(_subscriber, "c1", DiskInventory.Topic, TestContext.Current.CancellationToken);
        while (_subscriber.Outbox.Reader.TryRead(out _))
        {
        }

        _commands.Push("UDEV  [1.0] change   /devices/.../block/sdg (block)");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _time.Advance(SystemStateService.Debounce);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(_subscriber.Outbox.Reader.TryRead(out _));
    }

    [Fact]
    public async Task APulledMirrorDiskDegradesItsPoolAtOnceAndLeavesTheOtherPoolAlone()
    {
        SetPools();
        await _service.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal([("pithyxtestm", "ONLINE"), ("pithyxtests", "ONLINE")], _pools.Current.Pools.Select(pool => (pool.Name, pool.State)));
        Assert.Equal("VYXTEST05", LeafOn(_pools.Current, "pithyxtestm", "scsi5").Serial);
        await _hub.SubscribeAsync(_subscriber, "c1", PoolInventory.Topic, TestContext.Current.CancellationToken);
        while (_subscriber.Outbox.Reader.TryRead(out _))
        {
        }

        // ZFS still reports every disk ONLINE: without ZED it notices a pulled disk only on its next write.
        SetDisks(Without(_allDisks, "sdf"));
        _commands.Push("UDEV  [2.0] remove   /devices/.../block/sdf (block)");
        var pools = await NextPoolsAsync();

        Assert.Equal([("pithyxtestm", "DEGRADED"), ("pithyxtests", "ONLINE")], pools.Pools.Select(pool => (pool.Name, pool.State)));
        var leaf = LeafOn(pools, "pithyxtestm", "scsi5");
        Assert.Equal(("REMOVED", true, "VYXTEST05"), (leaf.State, leaf.Missing, leaf.Serial));
        Assert.False(LeafOn(pools, "pithyxtestm", "scsi4").Missing);
    }

    [Fact]
    public async Task ADiskThatComesBackIsBroughtOnlineOnce()
    {
        SetPools();
        await _service.StartAsync(TestContext.Current.CancellationToken);
        await _hub.SubscribeAsync(_subscriber, "c1", PoolInventory.Topic, TestContext.Current.CancellationToken);
        SetDisks(Without(_allDisks, "sdf"));
        _commands.Push("UDEV  [2.0] remove   /devices/.../block/sdf (block)");
        await NextPoolsAsync();
        Assert.DoesNotContain(_commands.Calls, call => call.FileName == "zpool" && call.Arguments[0] == "online");

        SetDisks(_allDisks);
        _commands.Push("UDEV  [3.0] add      /devices/.../block/sdh (block)");
        for (var i = 0; i < 100 && !_commands.Calls.Any(call => call.FileName == "zpool" && call.Arguments[0] == "online"); i++)
        {
            _time.Advance(SystemStateService.Debounce);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var online = Assert.Single(_commands.Calls, call => call.FileName == "zpool" && call.Arguments[0] == "online");
        Assert.Equal(["online", "pithyxtestm", LeafOn(_pools.Current, "pithyxtestm", "scsi5").VdevGuid], online.Arguments);
        for (var i = 0; i < 20 && _pools.Current.Pools[0].State != "ONLINE"; i++)
        {
            _time.Advance(SystemStateService.Debounce);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Equal("ONLINE", _pools.Current.Pools.Single(pool => pool.Name == "pithyxtestm").State);

        // Another udev event or pool read does not run it again.
        _commands.Push("UDEV  [4.0] change   /devices/.../block/sdf (block)");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _time.Advance(SystemStateService.Debounce);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await _service.RefreshPoolsAsync(TestContext.Current.CancellationToken);
        Assert.Single(_commands.Calls, call => call.FileName == "zpool" && call.Arguments[0] == "online");
    }

    [Fact]
    public async Task ASuspendedPoolShowsEvenWhileZpoolHangsAndIsResumedWhenItsDiskIsBack()
    {
        SetPools();
        await _service.StartAsync(TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(_kstat, "pithyxtests"));
        await File.WriteAllTextAsync(Path.Combine(_kstat, "pithyxtests", "state"), "SUSPENDED\n", TestContext.Current.CancellationToken);
        _commands.Results["zpool status"] = new CommandResult(-1, "", "", TimeSpan.Zero);
        _commands.Handler = request => request.FileName == "zpool" && request.Arguments[0] == "status" ? throw new TimeoutException("zpool did not finish within 00:01:00.") : null;

        await _service.RefreshPoolsAsync(TestContext.Current.CancellationToken);

        Assert.Equal([("pithyxtestm", "ONLINE"), ("pithyxtests", "SUSPENDED")], _pools.Current.Pools.Select(pool => (pool.Name, pool.State)));
        Assert.Throws<Pithyx.Daemon.Errors.PithyxException>(() => _service.RequireReachable("pithyxtests"));
        _service.RequireReachable("pithyxtestm");

        _commands.Handler = null;
        SetPools();
        await _service.RefreshPoolsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(_commands.Calls, call => call.FileName == "zpool" && call.Arguments.SequenceEqual(["clear", "pithyxtests"]));
    }
}
