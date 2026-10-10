using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Updates;

namespace Pithyx.Daemon.Tests.Updates;

public sealed class SystemParsersTests
{
    private static string Fixture(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryFiles.Root, "daemon", "tests", "fixtures", .. parts]));

    [Fact]
    public void ASimulatedUpgradeListsEveryPackageWithItsSecurityOrigin()
    {
        var updates = SystemParsers.ParseSimulatedUpgrade(Fixture("apt", "apt-get-simulate-upgrade.txt"));

        Assert.Equal(7, updates.Count);
        Assert.Contains(new DebianUpdate("libssl3t64", "3.5.7-1~deb13u2", "3.5.7-1~deb13u3", true), updates);
        Assert.Contains(new DebianUpdate("gh", "2.101.0", "2.102.0", false), updates);
        Assert.Equal(6, updates.Count(update => update.Security));
    }

    [Fact]
    public void NothingToUpgradeIsAnEmptyList() =>
        Assert.Empty(SystemParsers.ParseSimulatedUpgrade(Fixture("apt", "apt-get-simulate-upgrade-none.txt")));

    [Fact]
    public void ANewPackageHasNoInstalledVersion()
    {
        var updates = SystemParsers.ParseSimulatedUpgrade("Inst linux-image-6.12.115+deb13-amd64 (6.12.115-1 Debian:13.2/stable [amd64])\n");

        Assert.Equal([new DebianUpdate("linux-image-6.12.115+deb13-amd64", null, "6.12.115-1", false)], updates);
    }

    [Theory]
    [InlineData("healthy.txt", false, false)]
    [InlineData("resyncing.txt", false, true)]
    [InlineData("degraded-failed.txt", true, false)]
    [InlineData("degraded-missing.txt", true, false)]
    public void TheMirrorStateComesFromMdstat(string file, bool degraded, bool syncing)
    {
        var arrays = SystemParsers.ParseMdstat(Fixture("mdstat", file));

        Assert.Equal(["md126", "md127"], arrays.Select(array => array.Device));
        var mirror = arrays.Single(array => array.Device == "md127");
        Assert.Equal(degraded, mirror.Degraded);
        Assert.Equal(syncing, mirror.Syncing);
        Assert.False(arrays.Single(array => array.Device == "md126").Degraded);
    }

    [Fact]
    public void AFailedMemberIsNamed()
    {
        var mirror = SystemParsers.ParseMdstat(Fixture("mdstat", "degraded-failed.txt")).Single(array => array.Device == "md127");

        Assert.Equal(["loop0"], mirror.Members);
        Assert.Equal(["loop1"], mirror.Failed);
        Assert.Equal((2, 1), (mirror.Expected, mirror.Working));
    }

    [Fact]
    public void NoArrays() => Assert.Empty(SystemParsers.ParseMdstat(Fixture("mdstat", "none.txt")));
}
