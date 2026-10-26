using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Tests.Shares;

public sealed class ShareAvailabilityTests
{
    private static Pool Pool(string name, string state = "ONLINE") => new(name, "1", state, 100, 10, 90, 10, 0, false, 0, null, []);

    private static Dataset Dataset(string name, bool mounted = true) =>
        new(name, name.Split('/')[0], "filesystem", 1, 1, 1, $"/{name}", mounted, "off", null, null, null, null, "lz4", 131072, null, null, 0);

    private static PoolList Pools(Pool pool, params Dataset[] datasets) => new([pool], datasets);

    [Fact]
    public void AMountedShareOnAHealthyOrDegradedPoolIsAvailable()
    {
        Assert.Null(ShareAvailability.Reason("tank/pithyx/shares/media", Pools(Pool("tank"), Dataset("tank/pithyx/shares/media")), poolsRead: true));
        Assert.Null(ShareAvailability.Reason("tank/pithyx/shares/media", Pools(Pool("tank", "DEGRADED"), Dataset("tank/pithyx/shares/media")), poolsRead: true));
    }

    [Theory]
    [InlineData("SUSPENDED")]
    [InlineData("UNAVAIL")]
    [InlineData("FAULTED")]
    public void AShareOnAStoppedPoolIsUnavailable(string state)
    {
        Assert.Equal(ShareAvailability.PoolUnavailable, ShareAvailability.Reason("tank/pithyx/shares/media", Pools(Pool("tank", state), Dataset("tank/pithyx/shares/media")), poolsRead: true));
    }

    [Fact]
    public void AShareWhosePoolIsGoneOrWhoseDatasetIsNotMountedIsUnavailable()
    {
        Assert.Equal(ShareAvailability.PoolMissing, ShareAvailability.Reason("gone/pithyx/shares/media", Pools(Pool("tank")), poolsRead: true));
        Assert.Equal(ShareAvailability.NotMounted, ShareAvailability.Reason("tank/pithyx/shares/media", Pools(Pool("tank"), Dataset("tank/pithyx/shares/media", mounted: false)), poolsRead: true));
    }

    [Fact]
    public void AShareOnALockedPoolSaysItIsLocked()
    {
        var locked = Dataset("tank/pithyx/shares/media", mounted: false) with { Encryption = "aes-256-gcm", KeyStatus = "unavailable", EncryptionRoot = "tank/pithyx" };

        Assert.Equal(ShareAvailability.Locked, ShareAvailability.Reason("tank/pithyx/shares/media", Pools(Pool("tank"), locked), poolsRead: true));
        Assert.Null(ShareAvailability.Reason("tank/pithyx/shares/media", Pools(Pool("tank"), locked with { Mounted = true, KeyStatus = "available" }), poolsRead: true));
        Assert.NotEqual(
            ShareAvailability.Fingerprint(Pools(Pool("tank"), locked)),
            ShareAvailability.Fingerprint(Pools(Pool("tank"), locked with { KeyStatus = "available" })));
    }

    [Fact]
    public void PlainFoldersAndPoolsNeverReadAreNotJudged()
    {
        Assert.Null(ShareAvailability.Reason(null, Pools(Pool("tank")), poolsRead: true));
        Assert.Null(ShareAvailability.Reason("tank/pithyx/shares/media", new PoolList([], []), poolsRead: false));
    }

    [Fact]
    public void TheFingerprintChangesOnlyWithWhatDecidesAvailability()
    {
        var healthy = Pools(Pool("tank"), Dataset("tank/pithyx/shares/media"), Dataset("tank/pithyx/apps/x"));

        Assert.Equal(ShareAvailability.Fingerprint(healthy), ShareAvailability.Fingerprint(Pools(Pool("tank", "DEGRADED"), Dataset("tank/pithyx/shares/media"), Dataset("tank/pithyx/apps/x", mounted: false))));
        Assert.NotEqual(ShareAvailability.Fingerprint(healthy), ShareAvailability.Fingerprint(Pools(Pool("tank", "SUSPENDED"), Dataset("tank/pithyx/shares/media"))));
        Assert.NotEqual(ShareAvailability.Fingerprint(healthy), ShareAvailability.Fingerprint(Pools(Pool("tank"), Dataset("tank/pithyx/shares/media", mounted: false))));
    }
}
