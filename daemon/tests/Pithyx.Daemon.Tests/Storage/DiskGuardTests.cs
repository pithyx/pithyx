using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class DiskGuardTests
{
    private static readonly MountEntry _root = new("8:3", "/", "ext4", "/dev/sda3");
    private static readonly DeploymentEnvironment _dev = new(DeploymentKind.Dev, new HashSet<string> { "VYXTEST01", "VYXTEST02", "VYXTEST03" });

    private static BlockDevice Device(string name, string majorMinor, string type = "part", string? fileSystem = null, string[]? mountPoints = null, params BlockDevice[] children) =>
        new(name, name.StartsWith('/') ? name : $"/dev/{name}", $"id-{name}", majorMinor, type, fileSystem, mountPoints ?? [], children);

    private static GuardDisk Disk(string name, string? serial, DiskSignatures? signatures, params BlockDevice[] children) =>
        new(
            new Disk($"id-{name}", name, $"/dev/{name}", serial, null, null, null, 10_000_000_000, false, null, false, false, []),
            Device(name, $"250:{name.Sum(c => c)}", "disk", null, null, children),
            signatures);

    private static readonly DiskSignatures _blank = new([]);

    private static GuardFacts Facts(DeploymentEnvironment environment, IReadOnlyList<GuardDisk> disks, MountEntry[]? mounts = null, Dictionary<string, string>? poolMembers = null) =>
        new(environment, disks, mounts ?? [_root], [], poolMembers ?? []);

    private static GuardVerdict Only(GuardFacts facts, string name, Dictionary<string, string>? confirmations = null) =>
        DiskGuard.Evaluate(facts, confirmations).Single(verdict => verdict.DiskId == $"id-{name}");

    [Fact]
    public void TheDevVmsSystemDiskIsRefusedAndTestDisksFollowTheirContent()
    {
        var system = Disk("sda", "drive-scsi0", new DiskSignatures(["PMBR", "gpt"]), Device("sda1", "8:1", mountPoints: ["/boot/efi"]), Device("sda2", "8:2", fileSystem: "swap", mountPoints: ["[SWAP]"]), Device("sda3", "8:3", fileSystem: "ext4", mountPoints: ["/"]));
        var blank = Disk("sdc", "VYXTEST02", _blank);
        var oldLabel = Disk("sdb", "VYXTEST01", new DiskSignatures(["gpt"]), Device("sdb1", "8:17", fileSystem: "zfs_member"), Device("sdb9", "8:25"));
        var facts = Facts(_dev, [system, blank, oldLabel]);

        Assert.Equal((GuardDecision.Refused, "system_disk"), Decision(Only(facts, "sda", new() { ["id-sda"] = "drive-scsi0" })));
        Assert.Equal((GuardDecision.Allowed, "empty"), Decision(Only(facts, "sdc")));
        Assert.Equal((GuardDecision.NeedsSerialConfirmation, "has_data"), Decision(Only(facts, "sdb")));
        Assert.Equal((GuardDecision.NeedsSerialConfirmation, "has_data"), Decision(Only(facts, "sdb", new() { ["id-sdb"] = "VYXTEST02" })));
        Assert.Equal((GuardDecision.Allowed, "confirmed"), Decision(Only(facts, "sdb", new() { ["id-sdb"] = " VYXTEST01 " })));
        Assert.Contains("zfs_member", Only(facts, "sdb").Contents);
    }

    [Fact]
    public void OnDevOnlyListedTestDisksAreAllowedEvenWhenBlankAndConfirmed()
    {
        var facts = Facts(_dev, [Disk("sdg", "VYXTEST06", _blank), Disk("sdh", null, _blank)]);

        Assert.Equal((GuardDecision.Refused, "not_test_disk"), Decision(Only(facts, "sdg", new() { ["id-sdg"] = "VYXTEST06" })));
        Assert.Equal((GuardDecision.Refused, "not_test_disk"), Decision(Only(facts, "sdh")));
    }

    [Fact]
    public void OnProdDisksWithoutSerialOrWithADuplicateSerialCannotBeConfirmed()
    {
        var facts = Facts(DeploymentEnvironment.Prod, [
            Disk("sdb", null, new DiskSignatures(["linux_raid_member"])),
            Disk("sdc", "SAME", new DiskSignatures(["gpt"])),
            Disk("sdd", "SAME", _blank),
            Disk("sde", null, _blank),
        ]);

        Assert.Equal((GuardDecision.Refused, "no_serial"), Decision(Only(facts, "sdb")));
        Assert.Equal((GuardDecision.Refused, "duplicate_serial"), Decision(Only(facts, "sdc", new() { ["id-sdc"] = "SAME" })));
        Assert.Equal((GuardDecision.Refused, "duplicate_serial"), Decision(Only(facts, "sdd")));
        Assert.Equal((GuardDecision.Allowed, "empty"), Decision(Only(facts, "sde")));
    }

    [Fact]
    public void AnUnprobedDiskNeedsItsSerialAndPoolMembersAndMountedDisksAreRefused()
    {
        var facts = Facts(
            DeploymentEnvironment.Prod,
            [Disk("sdb", "A", null), Disk("sdc", "B", null, Device("sdc1", "8:33")), Disk("sdd", "C", _blank, Device("sdd1", "8:49", fileSystem: "ext4", mountPoints: ["/mnt/usb"]))],
            poolMembers: new() { ["/dev/disk/by-id/id-sdc1"] = "tank" });

        Assert.Equal((GuardDecision.NeedsSerialConfirmation, "not_checked"), Decision(Only(facts, "sdb")));
        var member = Only(facts, "sdc", new() { ["id-sdc"] = "B" });
        Assert.Equal((GuardDecision.Refused, "pool_member", "tank"), (member.Decision, member.Reason, member.Pool));
        Assert.Equal((GuardDecision.Refused, "mounted"), Decision(Only(facts, "sdd", new() { ["id-sdd"] = "C" })));
    }

    [Fact]
    public void TheDisksOfAPoolHoldingTheRootFileSystemAreSystemDisks()
    {
        var facts = Facts(
            DeploymentEnvironment.Prod,
            [Disk("nvme0n1", "N1", _blank, Device("nvme0n1p2", "259:2", fileSystem: "zfs_member")), Disk("nvme1n1", "N2", _blank, Device("nvme1n1p2", "259:5", fileSystem: "zfs_member"))],
            [new MountEntry("0:26", "/", "zfs", "rpool/ROOT/debian")],
            new() { ["/dev/disk/by-id/id-nvme0n1p2"] = "rpool", ["/dev/nvme1n1p2"] = "rpool" });

        Assert.All(DiskGuard.Evaluate(facts), verdict => Assert.Equal((GuardDecision.Refused, "system_disk"), Decision(verdict)));
    }

    [Fact]
    public void WithoutARootMountEveryDiskIsRefused()
    {
        var facts = Facts(DeploymentEnvironment.Prod, [Disk("sdb", "A", _blank)], []);

        Assert.Equal((GuardDecision.Refused, "system_disk"), Decision(Only(facts, "sdb")));
    }

    private static (GuardDecision, string) Decision(GuardVerdict verdict) => (verdict.Decision, verdict.Reason);

    /// <summary>
    /// Generated disk trees: the system disk anywhere (partition, LVM, crypt, md, swap, root on ZFS), partitions and
    /// signatures of every kind, pool members, missing and duplicate serials, dev and prod, right and wrong confirmations.
    /// </summary>
    [Fact]
    public void InvariantsHoldForGeneratedTrees()
    {
        var random = new Random(20260927);
        string[] fileSystems = ["ext4", "swap", "zfs_member", "linux_raid_member", "crypto_LUKS", "LVM2_member", "vfat"];
        string[][] signatureSets = [[], ["gpt"], ["PMBR", "gpt"], ["zfs_member"], ["linux_raid_member"], ["dos"]];
        for (var run = 0; run < 2000; run++)
        {
            var minor = 0;
            string Next() => $"8:{minor++}";
            var disks = new List<GuardDisk>();
            var diskCount = random.Next(1, 7);
            var serialPool = new[] { "VYXTEST01", "VYXTEST02", "VYXTEST03", "S4", "S5", "S5", null };
            var mounts = new List<MountEntry>();
            var poolMembers = new Dictionary<string, string>();
            var systemIndex = random.Next(diskCount + 1) - 1;
            var systemVia = random.Next(5);
            for (var index = 0; index < diskCount; index++)
            {
                var name = $"d{index}";
                var partitions = new List<BlockDevice>();
                var partitionCount = random.Next(0, 4);
                for (var p = 0; p < partitionCount; p++)
                {
                    var stacked = random.Next(4) switch
                    {
                        0 => Device($"/dev/mapper/{name}-vg{p}", Next(), "lvm", "ext4"),
                        1 => Device($"/dev/mapper/{name}-crypt{p}", Next(), "crypt", "ext4"),
                        2 => Device("md127", "9:127", "raid1", "ext4"),
                        _ => null,
                    };
                    partitions.Add(Device($"{name}p{p}", Next(), "part", random.Next(3) == 0 ? null : fileSystems[random.Next(fileSystems.Length)], null, stacked is null ? [] : [stacked]));
                }
                if (index == systemIndex)
                {
                    var holder = Device($"{name}sys", Next(), "part", "ext4", systemVia == 3 ? ["[SWAP]"] : null, systemVia == 1 ? [Device($"/dev/mapper/{name}-root", Next(), "lvm", "ext4")] : systemVia == 2 ? [Device("md126", "9:126", "raid1", "ext4")] : []);
                    partitions.Insert(random.Next(partitions.Count + 1), holder);
                    var mounted = holder.Children.Count > 0 ? holder.Children[0] : holder;
                    if (systemVia == 4)
                    {
                        mounts.Add(new MountEntry("0:30", "/", "zfs", "rpool/ROOT/debian"));
                        poolMembers[mounted.Path] = "rpool";
                    }
                    else if (systemVia != 3)
                    {
                        mounts.Add(new MountEntry(mounted.MajorMinor, random.Next(2) == 0 ? "/" : "/var/lib/pithyx", "ext4", mounted.Path));
                    }
                }
                else if (random.Next(6) == 0 && partitions.Count > 0)
                {
                    poolMembers[$"/dev/disk/by-id/id-{partitions[0].Name}"] = "tank";
                }
                var signatures = random.Next(5) == 0 ? null : new DiskSignatures(signatureSets[random.Next(signatureSets.Length)]);
                var disk = Disk(name, serialPool[random.Next(serialPool.Length)], signatures, [.. partitions]);
                disks.Add(disk with { Device = disk.Device with { IdLink = $"id-{name}" } });
            }
            if (!mounts.Any(mount => mount.Target == "/"))
            {
                mounts.Add(new MountEntry("8:250", "/", "ext4", "/dev/elsewhere"));
            }
            var environment = random.Next(2) == 0 ? _dev : DeploymentEnvironment.Prod;
            var confirmations = disks.Where(_ => random.Next(2) == 0)
                .ToDictionary(disk => disk.Disk.Id, disk => random.Next(3) == 0 ? "wrong" : disk.Disk.Serial ?? "none");
            var facts = new GuardFacts(environment, disks, mounts, [], poolMembers);

            foreach (var verdict in DiskGuard.Evaluate(facts, confirmations))
            {
                var disk = disks.Single(candidate => candidate.Disk.Id == verdict.DiskId);
                var devices = disk.Device.SelfAndDescendants().ToList();
                var context = $"run {run}, {verdict.DiskId}: {verdict.Decision} {verdict.Reason}";
                if (disks.IndexOf(disk) == systemIndex)
                {
                    Assert.True(verdict.Decision == GuardDecision.Refused && verdict.Reason == "system_disk", context);
                }
                if (verdict.Decision != GuardDecision.Allowed)
                {
                    continue;
                }
                if (environment.Kind == DeploymentKind.Dev)
                {
                    Assert.True(disk.Disk.Serial is not null && environment.TestDiskSerials.Contains(disk.Disk.Serial), context);
                }
                var hasContent = devices.Count > 1 || disk.Signatures is not { Types.Count: 0 };
                if (hasContent)
                {
                    Assert.True(confirmations.GetValueOrDefault(disk.Disk.Id) == disk.Disk.Serial && disk.Disk.Serial is not null, context);
                }
                Assert.DoesNotContain(devices, device => device.MountPoints.Count > 0 || poolMembers.Keys.Any(device.IsNamed));
                Assert.True(disk.Disk.Serial is null || disks.Count(other => other.Disk.Serial == disk.Disk.Serial) == 1, context);
            }
        }
    }
}
