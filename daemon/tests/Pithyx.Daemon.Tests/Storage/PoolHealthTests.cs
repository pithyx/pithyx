using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class PoolHealthTests
{
    private static readonly HashSet<string> _none = [];
    private static readonly Dictionary<string, string> _noSerials = [];

    private static Vdev Disk(string guid, string state = "ONLINE", string role = "data") =>
        new($"disk-{guid}", guid, "disk", role, state, $"/dev/disk/by-id/disk-{guid}-part1", null, 0, 0, 0, []);

    private static Vdev Group(string type, string guid, int? parity, params Vdev[] children) =>
        new($"{type}-{guid}", guid, type, "data", "ONLINE", null, parity, 0, 0, 0, children);

    private static PoolList Pools(params Pool[] pools) => new(pools, []);

    private static Pool Pool(string name, params Vdev[] vdevs) => new(name, "1", "ONLINE", 100, 10, 90, 10, 0, false, 0, null, vdevs);

    private static Func<string, bool> Without(params string[] guids) => path => !guids.Any(guid => path == $"/dev/disk/by-id/disk-{guid}-part1");

    private static Pool Apply(Pool pool, Func<string, bool> present, IReadOnlySet<string>? suspended = null, IReadOnlySet<string>? reconnecting = null) =>
        PoolHealth.Apply(Pools(pool), present, suspended ?? _none, reconnecting ?? _none, _noSerials).Pools.Single();

    [Fact]
    public void APulledMirrorDiskDegradesThePoolBeforeZfsNotices()
    {
        var pool = Apply(Pool("tank", Group("mirror", "m", null, Disk("a"), Disk("b"))), Without("b"));

        Assert.Equal("DEGRADED", pool.State);
        Assert.Equal("DEGRADED", pool.Vdevs[0].State);
        Assert.Equal([("ONLINE", false), ("REMOVED", true)], pool.Vdevs[0].Children.Select(leaf => (leaf.State, leaf.Missing)));
    }

    [Fact]
    public void AMirrorWithoutAnyDiskTakesThePoolDown()
    {
        var pool = Apply(Pool("tank", Group("mirror", "m", null, Disk("a"), Disk("b"))), Without("a", "b"));

        Assert.Equal(("UNAVAIL", "UNAVAIL"), (pool.State, pool.Vdevs[0].State));
    }

    [Theory]
    [InlineData(1, 1, "DEGRADED")]
    [InlineData(1, 2, "UNAVAIL")]
    [InlineData(2, 2, "DEGRADED")]
    [InlineData(2, 3, "UNAVAIL")]
    public void ARaidzSurvivesAsManyMissingDisksAsItHasParity(int parity, int missing, string state)
    {
        var disks = new[] { "a", "b", "c", "d", "e" };
        var pool = Apply(Pool("tank", Group("raidz", "r", parity, [.. disks.Select(guid => Disk(guid))])), Without(disks[..missing]));

        Assert.Equal(state, pool.State);
    }

    [Fact]
    public void AStripedDiskThatIsGoneTakesThePoolDown()
    {
        var pool = Apply(Pool("tank", Disk("a"), Disk("b")), Without("b"));

        Assert.Equal("UNAVAIL", pool.State);
    }

    [Fact]
    public void AMissingCacheOrSpareLeavesThePoolOnlineAndAMissingLogDegradesIt()
    {
        var data = Group("mirror", "m", null, Disk("a"), Disk("b"));

        Assert.Equal("ONLINE", Apply(Pool("tank", data, Disk("c", role: "cache"), Disk("s", role: "spare")), Without("c", "s")).State);
        Assert.Equal("DEGRADED", Apply(Pool("tank", data, Disk("l", role: "log")), Without("l")).State);
    }

    [Fact]
    public void WhatZfsReportsIsNeverMadeBetter()
    {
        var pool = Pool("tank", Group("mirror", "m", null, Disk("a", "FAULTED"), Disk("b"))) with { State = "DEGRADED" };

        var derived = Apply(pool, _ => true);

        Assert.Equal("DEGRADED", derived.State);
        Assert.Equal("FAULTED", derived.Vdevs[0].Children[0].State);
        Assert.False(derived.Vdevs[0].Children[0].Missing);
    }

    [Fact]
    public void ASuspendedPoolSaysSoAndOtherPoolsStayAsTheyAre()
    {
        var pools = PoolHealth.Apply(
            Pools(Pool("tank", Disk("a")), Pool("other", Disk("b"))),
            _ => true,
            new HashSet<string> { "tank" },
            _none,
            _noSerials);

        Assert.Equal([("tank", "SUSPENDED"), ("other", "ONLINE")], pools.Pools.Select(pool => (pool.Name, pool.State)));
    }

    [Fact]
    public void ADiskThatCameBackStaysMissingUntilItIsReconnected()
    {
        var pool = Apply(Pool("tank", Group("mirror", "m", null, Disk("a"), Disk("b"))), _ => true, reconnecting: new HashSet<string> { "b" });

        Assert.Equal("DEGRADED", pool.State);
        Assert.True(pool.Vdevs[0].Children[1].Missing);
    }

    [Fact]
    public void LeavesKeepTheSerialOfTheDiskTheyWereLastSeenOn()
    {
        var pools = PoolHealth.Apply(Pools(Pool("tank", Disk("a"))), Without("a"), _none, _none, new Dictionary<string, string> { ["a"] = "VYXTEST03" });

        Assert.Equal(("VYXTEST03", true), (pools.Pools[0].Vdevs[0].Serial, pools.Pools[0].Vdevs[0].Missing));
    }

    [Fact]
    public void FileVdevsAreNeverTakenForMissingDisks()
    {
        var file = new Vdev("/tmp/vdev0.img", "f", "file", "data", "ONLINE", "/tmp/vdev0.img", null, 0, 0, 0, []);

        Assert.Equal("ONLINE", Apply(Pool("tank", file), _ => false).State);
    }

    [Fact]
    public void AReturnedDiskIsBroughtBackOnlineAndASuspendedPoolIsResumedOnceNothingIsMissing()
    {
        var read = Pools(
            Pool("tank", Group("mirror", "m", null, Disk("a"), Disk("b", "REMOVED"))),
            Pool("stale", Group("mirror", "n", null, Disk("c"), Disk("d"))),
            Pool("stopped", Disk("e", "REMOVED")),
            Pool("gone", Group("mirror", "o", null, Disk("f"), Disk("g", "REMOVED"))));
        var suspended = new HashSet<string> { "stopped" };

        var commands = PoolHealth.Reconnects(read, Without("g"), suspended, new HashSet<string> { "d" });

        Assert.Equal(
            [["online", "tank", "b"], ["online", "stale", "d"], ["clear", "stopped"], ["online", "stopped", "e"]],
            commands.Select(command => command.Arguments.ToArray()));
        Assert.All(commands, command => Assert.Equal("zpool", command.FileName));
    }

    [Fact]
    public void FaultedAndOfflineDisksAreLeftToTheUser()
    {
        var read = Pools(Pool("tank", Group("mirror", "m", null, Disk("a", "FAULTED"), Disk("b", "OFFLINE"), Disk("c"))));

        Assert.Empty(PoolHealth.Reconnects(read, _ => true, _none, new HashSet<string> { "a", "b" }));
    }

    [Fact]
    public void DestroyAndExportAreRefusedForASuspendedPoolOrOneThatLostAVdev()
    {
        var mirror = Pool("tank", Group("mirror", "m", null, Disk("a"), Disk("b")));

        Assert.Null(PoolHealth.Unreachable(Apply(mirror, _ => true)));
        Assert.Null(PoolHealth.Unreachable(Apply(mirror, Without("b"))));
        Assert.Equal(ErrorCodes.StoragePoolDisksMissing, PoolHealth.Unreachable(Apply(mirror, Without("a", "b"))));
        Assert.Equal(ErrorCodes.StoragePoolSuspended, PoolHealth.Unreachable(Apply(mirror, _ => true, new HashSet<string> { "tank" })));
    }
}
