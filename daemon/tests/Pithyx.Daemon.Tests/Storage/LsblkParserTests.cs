using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>
/// The fixture is real <c>lsblk -J -b -O</c> output from the dev VM (virtio SCSI disks), anonymized by
/// tools/fixtures/record.sh. The <c>lsblk-qemu-*</c> fixtures come from tools/fixtures/record-lsblk-qemu.sh: one disk
/// behind an emulated AHCI, NVMe or USB controller, seen by the guest kernel's drivers and udev.
/// </summary>
public sealed class LsblkParserTests
{
    private static readonly IReadOnlyList<Disk> _disks = LsblkParser.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "commands", "default", "lsblk.stdout")));

    [Fact]
    public void ReturnsWholeDisksButNotTheOpticalDrive()
    {
        Assert.Equal(["sda", "sdb", "sdc", "sdd", "sde", "sdf", "sdg"], _disks.Select(d => d.Name));
    }

    [Fact]
    public void ReadsTestDiskDetails()
    {
        var disk = _disks.Single(d => d.Name == "sdg");

        Assert.Equal("serial-VYXTEST06", disk.Id);
        Assert.Equal("/dev/sdg", disk.Path);
        Assert.Equal("VYXTEST06", disk.Serial);
        Assert.Equal(10L * 1024 * 1024 * 1024, disk.SizeBytes);
        Assert.Equal("QEMU HARDDISK", disk.Model);
        Assert.Equal("QEMU", disk.Vendor);
        Assert.False(disk.Rotational);
        Assert.Null(disk.Transport);
        Assert.Null(disk.Wwn);
        Assert.Empty(disk.Partitions);
    }

    [Fact]
    public void ReadsPartitionsWithFileSystemsAndMountPoints()
    {
        var system = _disks.Single(d => d.Name == "sda");

        Assert.Equal(["sda1", "sda2", "sda3"], system.Partitions.Select(p => p.Name));
        Assert.Contains(system.Partitions, p => p.MountPoints.Contains("/"));
        Assert.All(system.Partitions, p => Assert.True(p.SizeBytes > 0));
    }

    [Fact]
    public void RealSerialsAreAnonymizedInTheFixture()
    {
        var system = _disks.Single(d => d.Name == "sda");

        Assert.DoesNotContain("drive-scsi0", system.Serial, StringComparison.Ordinal);
        Assert.Matches("^[0-9A-F]+$", system.Serial);
    }

    [Theory]
    [InlineData("lsblk-qemu-sata", "sda", "sata", true, "wwn-0x2bd765b56cceb6d3", "wwn-0x2bd765b56cceb6d3")]
    [InlineData("lsblk-qemu-nvme", "nvme0n1", "nvme", false, "wwn-nvme.afd965ff9784f9a213208300aaef10cf61ea03e9ba2f02aed72157e8e99438c5", "nvme-QEMU_NVMe_Ctrl_DD39D157AB6D")]
    [InlineData("lsblk-qemu-usb", "sda", "usb", true, "serial-D355F42E0AB", "usb-QEMU_QEMU_HARDDISK_D355F42E0AB-0:0")]
    public void ReadsDisksBehindSataNvmeAndUsbControllers(string scenario, string name, string transport, bool rotational, string id, string idLink)
    {
        var parsed = LsblkParser.ParseWithDevices(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "commands", scenario, "lsblk.stdout")));

        // The VM's own virtio root disk is listed next to the data disk.
        Assert.Equal(new[] { name, "vda" }.Order(StringComparer.Ordinal), parsed.Select(entry => entry.Disk.Name).Order(StringComparer.Ordinal));
        var (disk, device) = parsed.Single(entry => entry.Disk.Name == name);
        Assert.Equal((id, transport, rotational, 8L * 1024 * 1024 * 1024), (disk.Id, disk.Transport, disk.Rotational, disk.SizeBytes));
        Assert.False(disk.Removable);
        Assert.Equal($"/dev/{name}", disk.Path);
        // Pools name their vdevs by this link, so an NVMe or USB disk is found without a wwn- link.
        Assert.True(device.IsNamed($"/dev/disk/by-id/{idLink}"));
    }

    [Theory]
    [InlineData("""{"blockdevices":[{"name":"sda","type":"disk","size":"1000","rota":"1","rm":"0","ro":"0","serial":"S1 ","wwn":null,"path":"/dev/sda"}]}""")]
    [InlineData("""{"blockdevices":[{"name":"sda","type":"disk","size":1000,"rota":true,"rm":false,"ro":false,"serial":"S1","wwn":null,"path":"/dev/sda"}]}""")]
    public void AcceptsOldAndNewValueFormats(string json)
    {
        var disk = Assert.Single(LsblkParser.Parse(json));

        Assert.Equal(1000, disk.SizeBytes);
        Assert.True(disk.Rotational);
        Assert.Equal("serial-S1", disk.Id);
    }

    [Fact]
    public void PrefersTheWwnAsId() =>
        Assert.Equal("wwn-0x5000c500a1b2c3d4", Assert.Single(LsblkParser.Parse(
            """{"blockdevices":[{"name":"sda","type":"disk","size":1,"serial":"S1","wwn":"0x5000c500a1b2c3d4"}]}""")).Id);

    [Fact]
    public void SkipsLoopAndRamDevices() =>
        Assert.Empty(LsblkParser.Parse(
            """{"blockdevices":[{"name":"loop0","type":"loop","size":1},{"name":"zram0","type":"disk","size":1}]}"""));
}
