using Pithyx.Daemon.Storage;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class MountTableTests
{
    [Fact]
    public void ReadsTheRootAndEfiMountsOfTheDevVm()
    {
        var mounts = MountTable.ParseMountInfo(Proc("mountinfo"));

        Assert.Equal(new MountEntry("8:3", "/", "ext4", "/dev/sda3"), MountTable.Containing(mounts, "/var/lib/pithyx"));
        Assert.Equal(new MountEntry("8:1", "/boot/efi", "vfat", "/dev/sda1"), MountTable.Containing(mounts, "/boot/efi"));
        Assert.Equal("/", MountTable.Containing(mounts, "/boot")!.Target);
    }

    [Fact]
    public void ReadsSwapDevices() => Assert.Equal(["/dev/sda2"], MountTable.ParseSwaps(Proc("swaps")));

    [Fact]
    public void UnescapesSpacesAndPrefersTheLongestMountPoint()
    {
        var mounts = MountTable.ParseMountInfo("""
            1 0 8:3 / / rw - ext4 /dev/sda3 rw
            2 1 0:45 / /var rw - zfs rpool/var rw
            3 2 8:17 / /var/lib/my\040data rw - xfs /dev/sdb1 rw
            """);

        Assert.Equal("rpool/var", MountTable.Containing(mounts, "/var/lib/pithyx")!.Source);
        Assert.Equal("/var/lib/my data", MountTable.Containing(mounts, "/var/lib/my data/x")!.Target);
        Assert.Equal("/", MountTable.Containing(mounts, "/variable")!.Target);
    }
}
