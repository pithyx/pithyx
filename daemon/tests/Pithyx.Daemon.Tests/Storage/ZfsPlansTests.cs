using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class ZfsPlansTests
{
    private static GuardDisk Disk(string link, DiskSignatures signatures, params BlockDevice[] partitions) =>
        new(
            new Disk($"id-{link}", link, $"/dev/{link}", link.ToUpperInvariant(), null, null, null, 10_000_000_000, false, null, false, false, []),
            new BlockDevice(link, $"/dev/{link}", $"scsi-{link}", "8:0", "disk", null, [], partitions),
            signatures);

    private static readonly string[] _options =
        ["-o", "ashift=12", "-O", "compression=lz4", "-O", "xattr=sa", "-O", "acltype=posixacl", "-O", "atime=off", "-O", "normalization=formD", "-O", "canmount=off"];

    private static string[] Line(PlanStep step) => [step.Command.FileName, .. step.Command.Arguments];

    [Fact]
    public void ARaidz1OnBlankDisksIsOneCreateByStablePaths()
    {
        var steps = ZfsPlans.CreatePool("tank", "raidz1", [Disk("a", new([])), Disk("b", new([])), Disk("c", new([]))]);

        var create = Assert.Single(steps);
        Assert.Equal(
            ["zpool", "create", .. _options, "tank", "raidz1", "/dev/disk/by-id/scsi-a", "/dev/disk/by-id/scsi-b", "/dev/disk/by-id/scsi-c"],
            Line(create));
        Assert.Equal(["id-a", "id-b", "id-c"], create.DiskIds);
    }

    [Theory]
    [InlineData("single", 1, new string[0])]
    [InlineData("stripe", 2, new string[0])]
    [InlineData("mirror", 2, new[] { "mirror" })]
    [InlineData("raidz2", 4, new[] { "raidz2" })]
    [InlineData("raidz3", 5, new[] { "raidz3" })]
    public void EveryLayoutNamesItsVdevType(string layout, int count, string[] vdevType)
    {
        var disks = Enumerable.Range(0, count).Select(index => Disk($"d{index}", new([]))).ToList();

        var create = Assert.Single(ZfsPlans.CreatePool("tank", layout, disks));

        Assert.Equal([.. vdevType, .. disks.Select(disk => $"/dev/disk/by-id/scsi-{disk.Disk.Name}")], create.Command.Arguments.Skip(_options.Length + 2));
    }

    [Fact]
    public void AConfirmedDiskWithAnOldPoolIsClearedFirst()
    {
        var old = Disk("a", new(["gpt"]),
            new BlockDevice("a1", "/dev/a1", "scsi-a-part1", "8:1", "part", "zfs_member", [], []),
            new BlockDevice("a9", "/dev/a9", "scsi-a-part9", "8:9", "part", null, [], []));

        var steps = ZfsPlans.CreatePool("tank", "single", [old]);

        Assert.Equal(
            [
                ["zpool", "labelclear", "-f", "/dev/disk/by-id/scsi-a-part1"],
                ["wipefs", "--all", "/dev/disk/by-id/scsi-a-part1", "/dev/disk/by-id/scsi-a-part9", "/dev/disk/by-id/scsi-a"],
                ["zpool", "create", .. _options, "tank", "/dev/disk/by-id/scsi-a"],
            ],
            steps.Select(Line));
    }

    [Theory]
    [InlineData("Tank")]
    [InlineData("1tank")]
    [InlineData("mirror")]
    [InlineData("pithyx")]
    [InlineData("c0t0")]
    [InlineData("tank space")]
    [InlineData("a23456789012345678901234567890123")]
    public void InvalidNamesAreRefused(string name) =>
        Assert.Equal(ErrorCodes.StoragePoolNameInvalid, Assert.Throws<PithyxException>(() => ZfsPlans.ValidateName(name, [])).Code);

    [Fact]
    public void TakenNamesLayoutsAndDiskCountsAreChecked()
    {
        Assert.Equal(ErrorCodes.StoragePoolNameTaken, Assert.Throws<PithyxException>(() => ZfsPlans.ValidateName("tank", ["tank"])).Code);
        Assert.Equal(ErrorCodes.StoragePoolLayoutInvalid, Assert.Throws<PithyxException>(() => ZfsPlans.ValidateLayout("raid5", 3)).Code);
        var tooFew = Assert.Throws<PithyxException>(() => ZfsPlans.ValidateLayout("raidz2", 3));
        Assert.Equal((ErrorCodes.StoragePoolDiskCount, 4, 3), (tooFew.Code, tooFew.Parameters["min"], tooFew.Parameters["count"]));
        Assert.Equal(ErrorCodes.StoragePoolDiskCount, Assert.Throws<PithyxException>(() => ZfsPlans.ValidateLayout("single", 2)).Code);
        ZfsPlans.ValidateLayout("mirror", 3);
    }
}
