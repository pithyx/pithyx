using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>Golden argument lists of import, export, add vdev and RAID-Z expansion, and the decisions behind them.</summary>
public sealed class PoolOperationPlansTests
{
    private static GuardDisk Disk(string link, DiskSignatures signatures, params BlockDevice[] partitions) =>
        new(
            new Disk($"id-{link}", link, $"/dev/{link}", link.ToUpperInvariant(), null, null, null, 10_000_000_000, false, null, false, false, []),
            new BlockDevice(link, $"/dev/{link}", $"scsi-{link}", "8:0", "disk", null, [], partitions),
            signatures);

    private static string[] Line(PlanStep step) => [step.Command.FileName, .. step.Command.Arguments];

    private static readonly TypedName _typed = TypedName.Confirm("tank", "tank")!;

    private static Vdev Leaf(string name) => new(name, $"g-{name}", "disk", "data", "ONLINE", $"/dev/disk/by-id/{name}", null, 0, 0, 0, []);

    private static Vdev Group(string type, int width, int? parity = null, int index = 0) =>
        new(
            type == "raidz" ? $"raidz{parity}-{index}" : $"{type}-{index}",
            $"g-{type}-{index}",
            type,
            "data",
            "ONLINE",
            null,
            parity,
            0,
            0,
            0,
            [.. Enumerable.Range(0, width).Select(child => Leaf($"d{index}{child}"))]);

    private static Pool PoolOf(params Vdev[] vdevs) => new("tank", "1", "ONLINE", 0, 0, 0, 0, 0, false, 0, null, vdevs);

    [Fact]
    public void AReadOnlyImportNeverMountsNeverCachesAndForcesOnlyWithTheTypedName()
    {
        Assert.Equal(
            [["zpool", "import", "-o", "readonly=on", "-o", "cachefile=none", "-N", "-d", "/dev/disk/by-id", "123"]],
            ImportPlans.ImportReadOnly("123", null, null, ["id-a"]).Select(Line));
        Assert.Equal(
            [["zpool", "import", "-o", "readonly=on", "-o", "cachefile=none", "-N", "-d", "/dev/disk/by-id", "-f", "123", "tank2"]],
            ImportPlans.ImportReadOnly("123", "tank2", _typed, ["id-a"]).Select(Line));
        var step = Assert.Single(ImportPlans.ImportReadOnly("123", null, null, ["id-a"]));
        Assert.Equal((GuardRule.Import, (string?)null), (step.Rule, step.ImportingPool));
        Assert.Equal(["id-a"], step.DiskIds);
        Assert.Equal(["zpool", "import", "-d", "/dev/disk/by-id"], [ImportPlans.Scan.FileName, .. ImportPlans.Scan.Arguments]);
    }

    [Fact]
    public void TheTypedNameExistsOnlyForTheExactName()
    {
        Assert.Null(TypedName.Confirm("tank", null));
        Assert.Null(TypedName.Confirm("tank", ""));
        Assert.Null(TypedName.Confirm("tank", "Tank"));
        Assert.Null(TypedName.Confirm("tank", "tank2"));
        Assert.Equal("tank", TypedName.Confirm("tank", " tank ")!.Name);
    }

    [Fact]
    public void AReadWriteImportExportsImportsByGuidProtectsUnsafeDatasetsAndMountsTheRest()
    {
        var mounts = new ImportMounts(
            ["tank/data", "tank/data/photos"],
            [new UnmountedDataset("tank/usr", "/usr", "system_path"), new UnmountedDataset("tank/secret", "/tank/secret", "key_unavailable")]);

        var steps = ImportPlans.ImportReadWrite("tank", "123", null, ["id-a", "id-b"], mounts);

        Assert.Equal(
            [
                ["zpool", "export", "tank"],
                ["zpool", "import", "-N", "-d", "/dev/disk/by-id", "123", "tank"],
                ["zfs", "set", "canmount=noauto", "tank/usr"],
                ["zfs", "mount", "tank/data"],
                ["zfs", "mount", "tank/data/photos"],
            ],
            steps.Select(Line));
        Assert.All(steps, step => Assert.Equal((GuardRule.Import, "tank", 2), (step.Rule, step.ImportingPool, step.DiskIds.Count)));
        Assert.Equal(
            ["zpool", "import", "-N", "-d", "/dev/disk/by-id", "-f", "123", "tank"],
            Line(ImportPlans.ImportReadWrite("tank", "123", _typed, [], new ImportMounts([], []))[1]));
    }

    [Fact]
    public void ExportIsNeverForced() =>
        Assert.Equal([["zpool", "export", "tank"]], ImportPlans.Export("tank").Select(Line));

    [Fact]
    public void OnlySafeMountPointsAreMounted()
    {
        static IReadOnlyDictionary<string, string> Values(string mountPoint, string canMount = "on", string encryption = "off", string? keyStatus = null)
        {
            var values = new Dictionary<string, string> { ["mountpoint"] = mountPoint, ["canmount"] = canMount, ["encryption"] = encryption };
            if (keyStatus is not null)
            {
                values["keystatus"] = keyStatus;
            }
            return values;
        }
        var properties = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["old"] = Values("/old", canMount: "off"),
            ["old/data"] = Values("/old/data"),
            ["old/data/photos"] = Values("/old/data/photos"),
            ["old/root"] = Values("/"),
            ["old/etc"] = Values("/etc"),
            ["old/varlib"] = Values("/var/lib"),
            ["old/home"] = Values("/home/alice"),
            ["old/tmp"] = Values("/tmp/x"),
            ["old/usrlocal"] = Values("/usr/local"),
            ["old/sneaky"] = Values("/old/../etc"),
            ["old/relative"] = Values("old"),
            ["old/none"] = Values("none"),
            ["old/legacy"] = Values("legacy"),
            ["old/noauto"] = Values("/old/noauto", canMount: "noauto"),
            ["old/test"] = Values("/test"),
            ["old/over"] = Values("/srvdata"),
            ["old/twin"] = Values("/old/data"),
            ["old/locked"] = Values("/old/locked", encryption: "aes-256-gcm", keyStatus: "unavailable"),
            ["old/open"] = Values("/old/open", encryption: "aes-256-gcm", keyStatus: "available"),
            ["old/mnt"] = Values("/mnt"),
        };
        MountEntry[] mounts =
        [
            new("8:3", "/", "ext4", "/dev/sda3"),
            new("0:50", "/test/teet", "zfs", "test/pithyx/shares/teet"),
            new("0:51", "/mnt/usb", "vfat", "/dev/sdh1"),
        ];

        var decision = ImportPlans.DecideMounts(properties, mounts);

        Assert.Equal(["old/data", "old/data/photos", "old/open", "old/over"], decision.Mount);
        Assert.Equal(
            [
                ("old/root", "system_path"),
                ("old/etc", "system_path"),
                ("old/home", "system_path"),
                ("old/mnt", "shadows_mount"),
                ("old/sneaky", "system_path"),
                ("old/twin", "duplicate_mountpoint"),
                ("old/locked", "key_unavailable"),
                ("old/test", "shadows_mount"),
                ("old/tmp", "system_path"),
                ("old/usrlocal", "system_path"),
                ("old/varlib", "system_path"),
                ("old/relative", "system_path"),
            ],
            decision.Unmounted.Select(dataset => (dataset.Name, dataset.Reason)));
    }

    [Fact]
    public void AddingAVdevNamesDisksByStablePathAndOverridesOnlyWithTheTypedName()
    {
        var disks = new[] { Disk("a", new([])), Disk("b", new([])) };

        Assert.Equal([["zpool", "add", "tank", "mirror", "/dev/disk/by-id/scsi-a", "/dev/disk/by-id/scsi-b"]], ZfsPlans.AddVdev("tank", "mirror", disks, null).Select(Line));
        Assert.Equal([["zpool", "add", "tank", "/dev/disk/by-id/scsi-a"]], ZfsPlans.AddVdev("tank", "single", [disks[0]], null).Select(Line));
        Assert.Equal(
            [["zpool", "add", "--allow-replication-mismatch", "tank", "/dev/disk/by-id/scsi-a"]],
            ZfsPlans.AddVdev("tank", "single", [disks[0]], _typed).Select(Line));
        Assert.Equal(["id-a", "id-b"], ZfsPlans.AddVdev("tank", "mirror", disks, null).Single().DiskIds);
        Assert.DoesNotContain("-f", ZfsPlans.AddVdev("tank", "single", [disks[0]], _typed).Single().Command.Arguments);
    }

    [Fact]
    public void AConfirmedDiskWithDataIsClearedBeforeItIsAddedOrAttached()
    {
        var old = Disk("a", new(["gpt"]), new BlockDevice("a1", "/dev/a1", "scsi-a-part1", "8:1", "part", "zfs_member", [], []));

        Assert.Equal(
            [
                ["zpool", "labelclear", "-f", "/dev/disk/by-id/scsi-a-part1"],
                ["wipefs", "--all", "/dev/disk/by-id/scsi-a-part1", "/dev/disk/by-id/scsi-a"],
                ["zpool", "add", "tank", "/dev/disk/by-id/scsi-a"],
            ],
            ZfsPlans.AddVdev("tank", "single", [old], null).Select(Line));
        Assert.Equal(
            [
                ["zpool", "labelclear", "-f", "/dev/disk/by-id/scsi-a-part1"],
                ["wipefs", "--all", "/dev/disk/by-id/scsi-a-part1", "/dev/disk/by-id/scsi-a"],
                ["zpool", "attach", "tank", "456", "/dev/disk/by-id/scsi-a"],
            ],
            ZfsPlans.ExpandRaidz("tank", "456", old).Select(Line));
        Assert.Equal([["zpool", "attach", "tank", "456", "/dev/disk/by-id/scsi-b"]], ZfsPlans.ExpandRaidz("tank", "456", Disk("b", new([]))).Select(Line));
    }

    [Theory]
    [InlineData("single", 1, 0, "single", 1, "no_redundancy", false, false)]
    [InlineData("mirror", 2, 0, "single", 1, "lowers_redundancy", true, true)]
    [InlineData("mirror", 2, 0, "mirror", 2, "", false, false)]
    [InlineData("mirror", 2, 0, "mirror", 3, "replication_mismatch", true, true)]
    [InlineData("mirror", 3, 0, "mirror", 2, "lowers_redundancy", true, true)]
    [InlineData("mirror", 2, 0, "raidz1", 3, "irreversible", false, true)]
    [InlineData("mirror", 3, 0, "raidz1", 3, "lowers_redundancy irreversible", true, true)]
    [InlineData("raidz", 3, 1, "mirror", 2, "irreversible", false, true)]
    [InlineData("raidz", 3, 1, "mirror", 3, "replication_mismatch irreversible", true, true)]
    [InlineData("raidz", 3, 1, "single", 1, "lowers_redundancy irreversible", true, true)]
    [InlineData("raidz", 3, 1, "raidz1", 3, "irreversible", false, true)]
    [InlineData("raidz", 3, 1, "raidz1", 4, "replication_mismatch irreversible", true, true)]
    [InlineData("raidz", 4, 2, "raidz1", 3, "lowers_redundancy irreversible", true, true)]
    [InlineData("raidz", 3, 1, "raidz2", 4, "replication_mismatch irreversible", true, true)]
    public void AddingAVdevWarnsAboutRedundancyAndPermanence(string type, int width, int parity, string layout, int disks, string warnings, bool mismatch, bool confirmName)
    {
        var pool = PoolOf(type == "single" ? Leaf("d00") : Group(type, width, type == "raidz" ? parity : null));

        var assessment = VdevRedundancy.AssessAdd(pool, layout, disks);

        Assert.Equal(warnings.Split(' ', StringSplitOptions.RemoveEmptyEntries), assessment.Warnings);
        Assert.Equal((mismatch, confirmName), (assessment.ReplicationMismatch, assessment.RequiresConfirmName));
    }

    [Fact]
    public void OnlyDataVdevsCountAndTheWeakestDecides()
    {
        var spare = Leaf("spare0") with { Role = "spare" };
        var log = Leaf("log0") with { Role = "log" };
        var mirrors = PoolOf(Group("mirror", 3), Group("mirror", 2, index: 1), log, spare);

        Assert.Equal([new VdevShape("mirror", 3, 2), new VdevShape("mirror", 2, 1)], VdevRedundancy.DataVdevs(mirrors));
        // 2-way is the weakest: another 2-way lowers nothing, but differs from the 3-way.
        Assert.Equal(["replication_mismatch"], VdevRedundancy.AssessAdd(mirrors, "mirror", 2).Warnings);
        Assert.Equal(new VdevShape("draid", 5, 2), VdevRedundancy.Shape(Group("draid", 5) with { Name = "draid2:3d:5c:0s-0" }));
        Assert.Null(VdevRedundancy.Shape(Leaf("indirect-0") with { Type = "indirect" }));
    }
}
