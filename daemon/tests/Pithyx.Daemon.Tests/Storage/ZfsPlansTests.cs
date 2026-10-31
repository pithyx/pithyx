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

        var create = steps[0];
        Assert.Equal(
            ["zpool", "create", .. _options, "tank", "raidz1", "/dev/disk/by-id/scsi-a", "/dev/disk/by-id/scsi-b", "/dev/disk/by-id/scsi-c"],
            Line(create));
        Assert.Equal(["id-a", "id-b", "id-c"], create.DiskIds);
        Assert.Equal(
            [
                ["zfs", "create", "-o", "canmount=off", "-o", "mountpoint=none", "tank/pithyx"],
                ["zfs", "create", "-o", "canmount=off", "-o", "mountpoint=/tank", "tank/pithyx/shares"],
                ["zfs", "create", "-o", "mountpoint=/tank/homes", "tank/pithyx/homes"],
                ["zfs", "create", "-o", "canmount=off", "-o", "mountpoint=/tank/.pithyx/apps", "tank/pithyx/apps"],
                ["zfs", "create", "-o", "mountpoint=/tank/.pithyx/system", "tank/pithyx/system"],
                ["zfs", "create", "-p", "-o", "mountpoint=/tank/.pithyx/cache", "-o", "compression=off", "-o", "atime=off", "-o", "quota=2560M", "-o", "com.sun:auto-snapshot=false", "tank/pithyx/cache"],
            ],
            steps.Skip(1).Select(Line));
        Assert.All(steps.Skip(1), step => Assert.Empty(step.DiskIds));
    }

    [Fact]
    public void MirrorPairsAreMirrorVdevsInTheOrderChosen()
    {
        var create = ZfsPlans.CreatePool("tank", "mirrors", [Disk("a", new([])), Disk("b", new([])), Disk("c", new([])), Disk("d", new([]))])[0];

        Assert.Equal(
            ["tank", "mirror", "/dev/disk/by-id/scsi-a", "/dev/disk/by-id/scsi-b", "mirror", "/dev/disk/by-id/scsi-c", "/dev/disk/by-id/scsi-d"],
            create.Command.Arguments.Skip(_options.Length + 1));
        Assert.Equal(ErrorCodes.StoragePoolDiskCount, Assert.Throws<PithyxException>(() => ZfsPlans.ValidateLayout("mirrors", 5)).Code);
    }

    [Fact]
    public void RepairsNameDevicesByGuidAndNewDisksByStablePath()
    {
        var disk = Disk("e", new([]));

        Assert.Equal([["zpool", "replace", "tank", "123", "/dev/disk/by-id/scsi-e"]], ZfsPlans.Replace("tank", "123", disk).Select(Line));
        Assert.Equal([["zpool", "add", "tank", "spare", "/dev/disk/by-id/scsi-e"]], ZfsPlans.AddSpare("tank", disk).Select(Line));
        Assert.Equal([["zpool", "remove", "tank", "123"]], ZfsPlans.RemoveSpare("tank", "123").Select(Line));
        Assert.Equal([["zpool", "destroy", "tank"]], ZfsPlans.DestroyPool("tank").Select(Line));
        Assert.Equal([["zfs", "create", "tank/pithyx/shares/Media"]], ZfsPlans.CreateShareDataset("tank", "Media").Select(Line));
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, Assert.Throws<PithyxException>(() => ZfsPlans.CreateShareDataset("tank", "my media")).Code);
        Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, Assert.Throws<PithyxException>(() => ZfsPlans.CreateShareDataset("tank", "../x")).Code);
        // Commands never pass through a shell, and no name may look like an option.
        foreach (var hostile in new[] { "-o", "--help", "a;reboot", "$(reboot)", "a b", "a/b", "a@b", "`id`" })
        {
            Assert.Equal(ErrorCodes.StorageDatasetNameInvalid, Assert.Throws<PithyxException>(() => ZfsPlans.CreateShareDataset("tank", hostile)).Code);
        }
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

        var create = ZfsPlans.CreatePool("tank", layout, disks)[0];

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
            steps.Take(3).Select(Line));
    }

    [Theory]
    [InlineData("Tank")]
    [InlineData("1tank")]
    [InlineData("mirror")]
    [InlineData("pithyx")]
    [InlineData("c0t0")]
    [InlineData("tank space")]
    [InlineData("a23456789012345678901234567890123")]
    [InlineData("-f")]
    [InlineData("tank;reboot")]
    [InlineData("$(reboot)")]
    [InlineData("tank\nmirror")]
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
