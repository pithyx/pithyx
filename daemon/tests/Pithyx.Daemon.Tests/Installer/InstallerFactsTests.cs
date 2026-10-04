using System.Runtime.InteropServices;
using Pithyx.Daemon.Installer;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Tests.Installer;

public sealed class InstallerFactsTests
{
    private const long Gb = 1_000_000_000;

    private static string Fixture(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "fixtures", .. path]));

    private static Disk Disk(string name, long size, string? transport = "sata", bool removable = false, params Partition[] partitions) =>
        new($"serial-{name}", name, $"/dev/{name}", $"S{name}", null, "Model", null, size, false, transport, removable, false, partitions);

    private static BlockDevice Device(Disk disk, params (string Name, string? FileSystem, string[] MountPoints)[] children) =>
        new(disk.Name, disk.Path, null, "8:0", "disk", null, [],
            [.. children.Select(child => new BlockDevice(child.Name, $"/dev/{child.Name}", null, "8:1", "part", child.FileSystem, child.MountPoints, []))]);

    [Fact]
    public void TheDevVmProcessorIsX8664V2() =>
        Assert.True(InstallerFacts.IsX8664V2(Fixture("proc", "cpuinfo")));

    [Fact]
    public void AProcessorWithoutSse42IsNot() =>
        Assert.False(InstallerFacts.IsX8664V2("processor\t: 0\nflags\t\t: fpu cx16 lahf_lm popcnt sse4_1 ssse3\n"));

    [Fact]
    public void SecureBootIsTheFifthByteOfTheVariable()
    {
        Assert.True(InstallerFacts.IsSecureBootOn([6, 0, 0, 0, 1]));
        Assert.False(InstallerFacts.IsSecureBootOn([6, 0, 0, 0, 0]));
        Assert.False(InstallerFacts.IsSecureBootOn([6, 0, 0]));
    }

    [Fact]
    public void EveryCheckFailsOnABoxThatMissesEverything()
    {
        var checks = InstallerFacts.Checks("flags : fpu", 2 * Gb, uefi: false, secureBootVariable: [6, 0, 0, 0, 1], [], Architecture.X64);

        Assert.All(checks, check => Assert.False(check.Passed));
        Assert.Equal(["cpu", "uefi", "memory", "secure_boot", "disk"], checks.Select(check => check.Id));
    }

    [Fact]
    public void A4GbBoxPassesAlthoughTheKernelKeepsSomeMemory()
    {
        var checks = InstallerFacts.Checks("", 3_900_000_000, uefi: true, secureBootVariable: null, [], Architecture.Arm64);

        Assert.True(checks.Single(check => check.Id == "memory").Passed);
        Assert.True(checks.Single(check => check.Id == "cpu").Passed);
    }

    [Fact]
    public void TheBootStickUsbAndSmallDisksAreRefused()
    {
        var stick = Disk("sdc", 16 * Gb, transport: "usb", removable: true);
        var isoInVm = Disk("sr0", 64 * Gb, transport: "sata");
        var small = Disk("sdb", 32 * Gb);
        var card = Disk("mmcblk0", 64 * Gb, transport: "mmc");
        var mounted = Disk("sdd", 500 * Gb);
        var good = Disk("nvme0n1", 500 * Gb, transport: "nvme", partitions: new Partition("nvme0n1p1", "/dev/nvme0n1p1", 100 * Gb, "ntfs", null, []));
        var devices = new Dictionary<string, BlockDevice>
        {
            [isoInVm.Id] = Device(isoInVm, ("sr0p1", "iso9660", ["/run/live/medium"])),
            [mounted.Id] = Device(mounted, ("sdd1", "ext4", ["/mnt"])),
            [good.Id] = Device(good, ("nvme0n1p1", "ntfs", [])),
        };

        var verdicts = InstallerFacts.EvaluateDisks([stick, isoInVm, small, card, mounted, good], devices).ToDictionary(disk => disk.Disk.Name);

        Assert.Equal("usb", verdicts["sdc"].Reason);
        Assert.Equal("boot_medium", verdicts["sr0"].Reason);
        Assert.Equal("too_small", verdicts["sdb"].Reason);
        Assert.Equal("removable", verdicts["mmcblk0"].Reason);
        Assert.Equal("in_use", verdicts["sdd"].Reason);
        Assert.True(verdicts["nvme0n1"].Suitable);
        Assert.True(verdicts["nvme0n1"].HasData);
        Assert.False(verdicts["sdb"].HasData);
    }

    [Theory]
    [InlineData("pithyx", true)]
    [InlineData("nas-01", true)]
    [InlineData("NAS", false)]
    [InlineData("-nas", false)]
    [InlineData("nas-", false)]
    [InlineData("nas.local", false)]
    [InlineData("", false)]
    public void HostNamesAreDnsLabels(string name, bool valid) =>
        Assert.Equal(valid, InstallerFacts.IsHostname(name));

    [Theory]
    [InlineData("/dev/sda", 2, "/dev/sda2")]
    [InlineData("/dev/vdb", 1, "/dev/vdb1")]
    [InlineData("/dev/nvme0n1", 3, "/dev/nvme0n1p3")]
    [InlineData("/dev/mmcblk0", 1, "/dev/mmcblk0p1")]
    public void PartitionNamesFollowTheKernel(string disk, int number, string expected) =>
        Assert.Equal(expected, InstallerFacts.PartitionPath(disk, number));

    [Fact]
    public void NoEspStopsTheBoot()
    {
        var fstab = InstallerFiles.Fstab("root-uuid", "swap-uuid", ["AAAA-0001", "AAAA-0002"]);

        Assert.Contains("UUID=root-uuid\t/\tbtrfs\tsubvol=@,compress=zstd:1,noatime\t0\t0", fstab, StringComparison.Ordinal);
        Assert.Contains("UUID=AAAA-0001\t/boot/efi\tvfat\tumask=0077,nofail,x-systemd.device-timeout=10s\t0\t2", fstab, StringComparison.Ordinal);
        Assert.Contains("UUID=AAAA-0002\t/boot/efi2\tvfat\tumask=0077,nofail,x-systemd.device-timeout=10s\t0\t2", fstab, StringComparison.Ordinal);
        Assert.Contains("UUID=swap-uuid\tnone\tswap\tsw\t0\t0", fstab, StringComparison.Ordinal);
    }

    [Fact]
    public void MdadmConfKeepsOnlyTheArrays()
    {
        var conf = InstallerFiles.MdadmConf("ARRAY /dev/md/pithyx-system metadata=1.2 UUID=1:2:3:4\nmdadm: warning\nARRAY /dev/md/pithyx-swap metadata=1.2 UUID=5:6:7:8\n");

        Assert.StartsWith(InstallerFiles.Header, conf, StringComparison.Ordinal);
        Assert.Equal(2, conf.Split('\n').Count(line => line.StartsWith("ARRAY ", StringComparison.Ordinal)));
        Assert.DoesNotContain("warning", conf, StringComparison.Ordinal);
    }
}
