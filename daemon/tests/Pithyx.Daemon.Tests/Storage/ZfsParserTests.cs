using Pithyx.Daemon.Storage.Zfs;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class ZfsParserTests
{
    [Fact]
    public void AHealthyRaidz1HasOneVdevWithThreeDisks()
    {
        var pool = Assert.Single(ZpoolStatusParser.Parse(Read("zfs-raidz1", "zpool-status")));

        Assert.Equal("pithyxtestr", pool.Name);
        Assert.Equal("ONLINE", pool.State);
        Assert.Equal("8921785722493269352", pool.PoolGuid);
        var raidz = Assert.Single(pool.Vdevs);
        Assert.Equal(("raidz", "data", 1), (raidz.Type, raidz.Role, raidz.ParityDisks));
        Assert.Equal(3, raidz.Children.Count);
        Assert.All(raidz.Children, disk => Assert.StartsWith("/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi", disk.Path, StringComparison.Ordinal));
        Assert.Null(pool.Scan);
    }

    [Fact]
    public void ADiskTakenOfflineLeavesTheRaidzDegraded()
    {
        var pool = Assert.Single(ZpoolStatusParser.Parse(Read("zfs-degraded", "zpool-status")));

        Assert.Equal("DEGRADED", pool.State);
        Assert.Equal("DEGRADED", pool.Vdevs[0].State);
        Assert.Equal(["ONLINE", "OFFLINE", "ONLINE"], pool.Vdevs[0].Children.Select(disk => disk.State));
        Assert.Equal(("scrub", "finished"), (pool.Scan!.Kind, pool.Scan.State));
    }

    [Fact]
    public void ScansAreReadWithTheirStateAndTimes()
    {
        var paused = Assert.Single(ZpoolStatusParser.Parse(Read("zfs-scrub-paused", "zpool-status"))).Scan!;
        var scrubbed = Assert.Single(ZpoolStatusParser.Parse(Read("zfs-scrubbed", "zpool-status"))).Scan!;
        var resilvered = Assert.Single(ZpoolStatusParser.Parse(Read("zfs-resilvered", "zpool-status"))).Scan!;

        Assert.Equal(("scrub", "scanning", true), (paused.Kind, paused.State, paused.Paused));
        Assert.Null(paused.EndedAt);
        Assert.Equal(("scrub", "finished", false, 0L), (scrubbed.Kind, scrubbed.State, scrubbed.Paused, scrubbed.Errors));
        Assert.True(scrubbed.EndedAt >= scrubbed.StartedAt);
        Assert.True(scrubbed.ExaminedBytes > 0);
        Assert.Equal(("resilver", "finished"), (resilvered.Kind, resilvered.State));
    }

    [Fact]
    public void NoPoolsParseToEmptyLists()
    {
        Assert.Empty(ZpoolStatusParser.Parse(Read("zfs-empty", "zpool-status")));
        Assert.Empty(ZpoolListParser.Parse(Read("zfs-empty", "zpool-list")));
        Assert.Empty(ZfsDatasetParser.ParseList(Read("zfs-empty", "zfs-list")));
    }

    [Fact]
    public void AMirrorAndASingleDiskPoolAreTwoPoolsWithTheirSizes()
    {
        var status = ZpoolStatusParser.Parse(Read("zfs-mirror-single", "zpool-status"));
        var sizes = ZpoolListParser.Parse(Read("zfs-mirror-single", "zpool-list"));

        Assert.Equal(["pithyxtestm", "pithyxtests"], status.Select(pool => pool.Name));
        Assert.Equal("mirror", status[0].Vdevs[0].Type);
        Assert.Null(status[0].Vdevs[0].ParityDisks);
        Assert.Equal(("disk", 0), (status[1].Vdevs[0].Type, status[1].Vdevs[0].Children.Count));
        Assert.All(sizes, pool => Assert.Equal(10200547328, pool.SizeBytes));
        Assert.All(sizes, pool => Assert.Equal((0, 0, false), (pool.CapacityPercent, pool.FragmentationPercent, pool.ReadOnly)));
    }

    [Fact]
    public void DatasetsCarryMountPointAndEncryption()
    {
        var dataset = Assert.Single(ZfsDatasetParser.ParseList(Read("zfs-raidz1", "zfs-list")));

        Assert.Equal(("pithyxtestr", "pithyxtestr", "filesystem"), (dataset.Name, dataset.Pool, dataset.Type));
        Assert.Equal(("/pithyxtestr", true), (dataset.MountPoint, dataset.Mounted));
        Assert.Equal(("off", (string?)null), (dataset.Encryption, dataset.KeyStatus));
        Assert.True(dataset.AvailableBytes > dataset.UsedBytes);
    }

    // Recorded on dev01 from the owner's test pool, read-only.
    [Fact]
    public void DatasetsCarryLimitsCompressionAndBlockSizes()
    {
        var datasets = ZfsDatasetParser.ParseList(Read("zfs-datasets", "zfs-list"));
        var share = datasets.Single(dataset => dataset.Name == "test/pithyx/shares/teet");

        Assert.Equal(("/test/teet", true), (share.MountPoint, share.Mounted));
        Assert.Equal(((long?)null, (long?)null), (share.QuotaBytes, share.ReservationBytes));
        Assert.Equal(("lz4", (long?)131072), (share.Compression, share.RecordSizeBytes));
        Assert.Equal(((long?)null, (long?)null, 0L), (share.VolumeSizeBytes, share.VolumeBlockSizeBytes, share.SnapshotsUsedBytes));
        Assert.All(datasets, dataset => Assert.Equal("test", dataset.Pool));
    }

    [Fact]
    public void ZfsGetShapeGivesPropertyValues()
    {
        var properties = ZfsDatasetParser.ParseProperties(Read("zfs-raidz1", "zfs-list"));

        Assert.Equal("/pithyxtestr", properties["pithyxtestr"]["mountpoint"]);
        Assert.False(properties["pithyxtestr"].ContainsKey("keystatus"));
    }

    [Fact]
    public void ARunningRaidzExpansionNamesItsVdevAndProgress()
    {
        var pool = Assert.Single(ZpoolStatusParser.Parse(Read("zfs-raidz-expanding", "zpool-status")));

        var raidz = Assert.Single(pool.Vdevs);
        var expansion = pool.Expansion!;
        Assert.Equal(("scanning", raidz.VdevGuid, false), (expansion.State, expansion.VdevGuid, expansion.WaitingForResilver));
        Assert.Equal((504100864L, 0L), (expansion.ToReflowBytes, expansion.ReflowedBytes));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790622413), expansion.StartedAt);
        Assert.Null(expansion.EndedAt);
        Assert.Equal(4, raidz.Children.Count);
    }

    [Fact]
    public void AFinishedExpansionStaysInTheStatusWithItsEnd()
    {
        var pool = Assert.Single(ZpoolStatusParser.Parse(Read("zfs-raidz-expanded", "zpool-status")));

        Assert.Equal(("finished", pool.Vdevs[0].VdevGuid), (pool.Expansion!.State, pool.Expansion.VdevGuid));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790622414), pool.Expansion.EndedAt);
        Assert.True(pool.Expansion.ReflowedBytes > 0);
        Assert.Null(Assert.Single(ZpoolStatusParser.Parse(Read("zfs-raidz1", "zpool-status"))).Expansion);
    }
}
