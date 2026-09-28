using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class ZfsDatasetPlansTests
{
    private static string[] Line(PlanStep step) => [step.Command.FileName, .. step.Command.Arguments];

    [Fact]
    public void RenameKeepsTheParentAndNeverForces()
    {
        var steps = ZfsDatasetPlans.Rename("tank/pithyx/shares/media", "movies");

        Assert.Equal([["zfs", "rename", "tank/pithyx/shares/media", "tank/pithyx/shares/movies"]], steps.Select(Line));
        Assert.Empty(Assert.Single(steps).DiskIds);
    }

    [Fact]
    public void SetNamesEveryChangedPropertyInOneCommand()
    {
        var steps = ZfsDatasetPlans.Set(
            "tank/pithyx/shares/media",
            [new("quota", "10737418240"), new("reservation", "none"), new("compression", "zstd-3"), new("recordsize", "1048576")]);

        Assert.Equal(
            [["zfs", "set", "quota=10737418240", "reservation=none", "compression=zstd-3", "recordsize=1048576", "tank/pithyx/shares/media"]],
            steps.Select(Line));
        Assert.Throws<ArgumentException>(() => ZfsDatasetPlans.Set("tank/data", []));
    }

    [Fact]
    public void DestroyTakesChildrenOnlyWhenAskedAndNeverClonesOrForce()
    {
        Assert.Equal([["zfs", "destroy", "tank/data"]], ZfsDatasetPlans.Destroy("tank/data", recursive: false).Select(Line));
        Assert.Equal([["zfs", "destroy", "-r", "tank/data"]], ZfsDatasetPlans.Destroy("tank/data", recursive: true).Select(Line));
        Assert.Throws<ArgumentException>(() => ZfsDatasetPlans.Destroy("tank", recursive: true));
    }

    [Fact]
    public void AVolumeGetsItsParentFirstWhenItIsMissing()
    {
        Assert.Equal(
            [
                ["zfs", "create", "-o", "canmount=off", "-o", "mountpoint=none", "tank/pithyx/volumes"],
                ["zfs", "create", "-V", "10737418240", "-o", "volblocksize=16K", "tank/pithyx/volumes/vm1"],
            ],
            ZfsDatasetPlans.CreateVolume("tank", "vm1", 10L * 1024 * 1024 * 1024, sparse: false, createParent: true).Select(Line));
        Assert.Equal(
            [["zfs", "create", "-V", "1064960", "-s", "-o", "volblocksize=16K", "tank/pithyx/volumes/vm2"]],
            ZfsDatasetPlans.CreateVolume("tank", "vm2", 1024 * 1024 + 1, sparse: true, createParent: false).Select(Line));
    }

    [Theory]
    [InlineData(1024 * 1024, 1024 * 1024)]
    [InlineData(1024 * 1024 + 1, 1024 * 1024 + 16 * 1024)]
    [InlineData(1024 * 1024 + 16 * 1024, 1024 * 1024 + 16 * 1024)]
    public void VolumeSizesAreWholeBlocks(long size, long expected) =>
        Assert.Equal(expected, ZfsDatasetPlans.RoundVolumeSize(size));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1024 * 1024 - 1)]
    [InlineData(long.MaxValue)]
    public void VolumeSizesOutsideTheRangeAreRefused(long size) =>
        Assert.Equal(ErrorCodes.StorageVolumeSizeInvalid, Assert.Throws<PithyxException>(() => ZfsDatasetPlans.RoundVolumeSize(size)).Code);

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("../x")]
    [InlineData("-o")]
    [InlineData("a b")]
    [InlineData("a@b")]
    [InlineData("a;reboot")]
    [InlineData("$(reboot)")]
    [InlineData(".hidden")]
    public void NewNamesAreOneSafeComponent(string name)
    {
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, Assert.Throws<PithyxException>(() => ZfsDatasetPlans.Rename("tank/data", name)).Code);
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, Assert.Throws<PithyxException>(() => ZfsDatasetPlans.CreateVolume("tank", name, 1024 * 1024, false, false)).Code);
    }

    [Fact]
    public void FullNamesLongerThanZfsAllowsAreRefused()
    {
        var deep = string.Join('/', Enumerable.Repeat(new string('d', 60), 4));
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, Assert.Throws<PithyxException>(() => ZfsDatasetPlans.Child(deep, new string('n', 20))).Code);
    }

    [Theory]
    [InlineData("-r")]
    [InlineData("tank/data@snap")]
    [InlineData("tank/data#mark")]
    [InlineData("tank//data")]
    [InlineData("tank/data,tank/other")]
    public void CommandsNameExactlyOneDataset(string dataset)
    {
        Assert.Throws<ArgumentException>(() => ZfsDatasetPlans.Destroy(dataset, recursive: false));
        Assert.Throws<ArgumentException>(() => ZfsDatasetPlans.Set(dataset, [new("compression", "lz4")]));
    }

    [Fact]
    public void TheCompressionListHasNoAmbiguousOrLegacyAlgorithms()
    {
        Assert.Contains("zstd-19", ZfsDatasetPlans.Compressions);
        Assert.Contains("gzip-9", ZfsDatasetPlans.Compressions);
        Assert.DoesNotContain("on", ZfsDatasetPlans.Compressions);
        Assert.DoesNotContain("lzjb", ZfsDatasetPlans.Compressions);
        Assert.DoesNotContain("zstd-20", ZfsDatasetPlans.Compressions);
        Assert.Equal(33, ZfsDatasetPlans.Compressions.Count);
    }
}
