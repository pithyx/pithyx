using Pithyx.Daemon.Storage.Zfs;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class ZfsUserSpaceTests
{
    private const string Fixture = "pithyxtestfs/quotafixture";

    [Fact]
    public void ReadsTheSpaceAndQuotaOfEveryUidAsRoot()
    {
        var spaces = ZfsUserSpaces.Parse(Read("zfs-userspace", "zfs-get"));

        Assert.Equal(["pithyxtestfs/pithyx", Fixture], spaces.Keys.Order(StringComparer.Ordinal));
        var fixture = spaces[Fixture];
        Assert.Equal(new ZfsUserSpace(3154432, 52428800), fixture[60001]);
        Assert.Equal(new ZfsUserSpace(0, 1073741824), fixture[60002]);
        // ZFS prints 0 for no quota.
        Assert.Equal(new ZfsUserSpace(1057280, null), fixture[60003]);
        Assert.All(spaces["pithyxtestfs/pithyx"].Values, space => Assert.Equal(new ZfsUserSpace(0, null), space));
    }

    [Fact]
    public void WithoutRootTheValuesAreUnknown()
    {
        var spaces = ZfsUserSpaces.Parse(
            """
            {"output_version":{"command":"zfs get","vers_major":0,"vers_minor":1},"datasets":{"tank/pithyx/shares/media":{"name":"tank/pithyx/shares/media","type":"FILESYSTEM","properties":{"userused@20001":{"value":"-","source":{"type":"NONE","data":"-"}},"userquota@20001":{"value":"-","source":{"type":"NONE","data":"-"}}}}}}
            """);

        Assert.Equal(new ZfsUserSpace(null, null), spaces["tank/pithyx/shares/media"][20001]);
        Assert.Empty(ZfsUserSpaces.Parse(""));
    }

    [Fact]
    public void ReadsEveryUidOnEveryDatasetWithOneCommand()
    {
        var read = ZfsUserSpaces.Read([Fixture, "pithyxtestfs/pithyx"], [60003, 60001, 60002, 60001]);

        Assert.Equal("zfs", read.FileName);
        Assert.Equal(
            ["get", "-j", "--json-int", "-t", "filesystem", "userused@60001,userquota@60001,userused@60002,userquota@60002,userused@60003,userquota@60003", Fixture, "pithyxtestfs/pithyx"],
            read.Arguments);
        Assert.Throws<ArgumentException>(() => ZfsUserSpaces.Read([], [60001]));
        Assert.Throws<ArgumentException>(() => ZfsUserSpaces.Set("-o", 60001, null));
    }

    [Fact]
    public void SetsOnlyTheQuotasThatDiffer()
    {
        var actual = ZfsUserSpaces.Parse(Read("zfs-userspace", "zfs-get"));
        var wanted = new Dictionary<(string Dataset, long Uid), long>
        {
            // Unchanged, changed, and new on another dataset.
            [(Fixture, 60001)] = 52428800,
            [(Fixture, 60003)] = 2097152,
            [("pithyxtestfs/pithyx", 60002)] = 1048576,
        };

        var changes = ZfsUserSpaces.Changes(wanted, actual, [60001, 60002, 60003]);

        Assert.Equal(
            [
                "set userquota@60002=1048576 pithyxtestfs/pithyx",
                // 60002 has a quota nobody wants any more.
                $"set userquota@60002=none {Fixture}",
                $"set userquota@60003=2097152 {Fixture}",
            ],
            changes.Select(change => string.Join(' ', change.Arguments)));
        // Uids the box does not manage keep what they have.
        Assert.Empty(ZfsUserSpaces.Changes(new Dictionary<(string Dataset, long Uid), long>(), actual, []));
    }
}
