using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class DiskProbeTests
{
    [Fact]
    public void WipefsAndBlkidNameTheSignatures()
    {
        Assert.Equal(["gpt", "gpt", "PMBR"], DiskProbe.ParseWipefs(Read("default", "wipefs-sdb")));
        Assert.Empty(DiskProbe.ParseWipefs(""));
        Assert.Equal(["gpt"], DiskProbe.ParseBlkid(Read("default", "blkid-sdb")));
        Assert.Equal(["zfs_member"], DiskProbe.ParseBlkid("DEVNAME=/dev/sdb1\nLABEL=tank\nTYPE=zfs_member\nUSAGE=filesystem\nPART_ENTRY_TYPE=6a898cc3\n"));
    }

    [Fact]
    public async Task ABlankDiskHasNoSignaturesAndAnUnreadableOneFails()
    {
        var commands = new ScriptedCommandRunner();
        commands.Results["wipefs"] = new CommandResult(0, "", "", TimeSpan.Zero);
        commands.Results["blkid"] = new CommandResult(2, "", "", TimeSpan.Zero);
        var probe = new DiskProbe(commands);

        Assert.Empty((await probe.ProbeAsync("/dev/sdc", TestContext.Current.CancellationToken)).Types);
        Assert.Equal(["--no-act", "--json", "/dev/sdc"], commands.Calls[0].Arguments);

        commands.Results["blkid"] = new CommandResult(4, "", "cannot open", TimeSpan.Zero);
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync("/dev/sdc", TestContext.Current.CancellationToken));
    }
}
