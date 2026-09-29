using Pithyx.Daemon.Storage.Zfs;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class ZpoolImportParserTests
{
    [Fact]
    public void AnExportedRaidzIsOnePoolWithItsDevices()
    {
        var pool = Assert.Single(ZpoolImportParser.Parse(Read("zfs-import", "zpool-import")));

        Assert.Equal(("pithyxtesti", "12476398662218235294", "ONLINE"), (pool.Name, pool.PoolGuid, pool.State));
        Assert.Equal((true, false, (string?)null, (string?)null), (pool.CanImport, pool.NeedsForce, pool.Status, pool.MessageId));
        Assert.Equal("The pool can be imported using its name or numeric identifier.", pool.Action);
        var raidz = Assert.Single(pool.Vdevs);
        Assert.Equal(("raidz1-0", "ONLINE"), (raidz.Name, raidz.State));
        Assert.Equal(
            ["/tmp/pithyxci-fixture/vdev0.img", "/tmp/pithyxci-fixture/vdev1.img", "/tmp/pithyxci-fixture/vdev2.img"],
            raidz.Leaves().Select(leaf => leaf.Name));
        Assert.All(raidz.Children, device => Assert.Equal(("ONLINE", (string?)null, (string?)null), (device.State, device.Detail, device.DiskId)));
    }

    [Fact]
    public void AMissingDeviceLeavesThePoolDegradedButImportable()
    {
        var pool = Assert.Single(ZpoolImportParser.Parse(Read("zfs-import-degraded", "zpool-import")));

        Assert.Equal(("DEGRADED", true, "ZFS-8000-2Q"), (pool.State, pool.CanImport, pool.MessageId));
        Assert.Equal("One or more devices are missing from the system.", pool.Status);
        // The action continues on a second line that starts with a tab.
        Assert.Equal("The pool can be imported despite missing or damaged devices.  The fault tolerance of the pool may be compromised if imported.", pool.Action);
        Assert.Equal(["ONLINE", "ONLINE", "UNAVAIL"], pool.Vdevs[0].Children.Select(device => device.State));
        Assert.Equal("cannot open", pool.Vdevs[0].Children[2].Detail);
    }

    [Fact]
    public void TooManyMissingDevicesCannotBeImported()
    {
        var pool = Assert.Single(ZpoolImportParser.Parse(Read("zfs-import-unavail", "zpool-import")));

        Assert.Equal(("UNAVAIL", false, "ZFS-8000-3C"), (pool.State, pool.CanImport, pool.MessageId));
        Assert.Equal(("UNAVAIL", "insufficient replicas"), (pool.Vdevs[0].State, pool.Vdevs[0].Detail));
    }

    [Fact]
    public void APoolLastUsedByAnotherSystemNeedsForceAndGroupsAreKept()
    {
        var pools = ZpoolImportParser.Parse(Read("zfs-import-handwritten-other-system", "zpool-import"));

        Assert.Equal(["backup", "Media"], pools.Select(pool => pool.Name));
        var backup = pools[0];
        Assert.Equal((true, true, "ZFS-8000-EY"), (backup.CanImport, backup.NeedsForce, backup.MessageId));
        Assert.Equal("The pool can be imported using its name or numeric identifier and the '-f' flag.", backup.Action);
        Assert.Equal(["mirror-0", "logs", "cache", "spares"], backup.Vdevs.Select(vdev => vdev.Name));
        Assert.Equal([null, null, null], backup.Vdevs.Skip(1).Select(group => group.State));
        Assert.Equal(
            ["wwn-0x5000c500a1b2c3d4", "wwn-0x5000c500a1b2c3d5", "nvme-PCIE_SSD_1TB_S1234-part1", "nvme-PCIE_SSD_1TB_S1234-part2", "wwn-0x5000c500a1b2c3d6"],
            backup.Vdevs.SelectMany(vdev => vdev.Leaves()).Select(leaf => leaf.Name));
        var media = pools[1];
        Assert.Equal(("DEGRADED", true, false), (media.State, media.CanImport, media.NeedsForce));
        Assert.Equal(("11470926838497291122", "FAULTED", "corrupted data"), (media.Vdevs[0].Children[3].Name, media.Vdevs[0].Children[3].State, media.Vdevs[0].Children[3].Detail));
    }

    [Fact]
    public void NoPoolsToImportIsAnEmptyList()
    {
        // ZFS says so on stderr and exits with 0.
        Assert.Empty(ZpoolImportParser.Parse(Read("zfs-import-none", "zpool-import")));
        Assert.Empty(ZpoolImportParser.Parse("\n"));
    }
}
