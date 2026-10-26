using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Health;
using Pithyx.Daemon.Storage.Zfs;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage.Health;

public sealed class StorageAlertsTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-09-27T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    internal static readonly Disk Sdb = new("wwn-0x5000cca264c0ffee", "sdb", "/dev/sdb", "WD-HANDWRITTEN2", "0x5000cca264c0ffee", "WDC WD40EFRX-68N32N0", "ATA", 4000787030016, true, "sata", false, false, []);

    internal static Pool Pool(string name = "tank", string state = "ONLINE", int capacity = 10, long dataErrors = 0, PoolScan? scan = null) =>
        new(name, "1", state, 100, capacity, 100 - capacity, capacity, 0, false, dataErrors, scan, []);

    private static IReadOnlyList<StorageAlert> Evaluate(params Pool[] pools) => StorageAlerts.Evaluate(new PoolList(pools, []), [], [], _now);

    private static SmartReport Smart(string state, long pending = 0, string? selfTest = "passed") =>
        new(Sdb.Id, state, null, false, 34, 100, 0, pending, 0, null, null, selfTest, _now, _now);

    [Fact]
    public void AHealthyPoolHasNoAlerts()
    {
        Assert.Empty(Evaluate(Pool()));
    }

    [Theory]
    [InlineData(79, null, null)]
    [InlineData(80, "warning", "storage.alert.capacityWarning")]
    [InlineData(89, "warning", "storage.alert.capacityWarning")]
    [InlineData(90, "critical", "storage.alert.capacityCritical")]
    [InlineData(100, "critical", "storage.alert.capacityCritical")]
    public void FillLevelWarnsAt80AndIsCriticalAt90Percent(int percent, string? severity, string? code)
    {
        var alerts = Evaluate(Pool(capacity: percent));

        if (severity is null)
        {
            Assert.Empty(alerts);
            return;
        }
        var alert = Assert.Single(alerts);
        Assert.Equal(("pool.capacity:tank", severity, code), (alert.Key, alert.Severity, alert.Code));
        Assert.Equal(percent, alert.Params["percent"].GetInt32());
        Assert.Equal("tank", alert.Params["pool"].GetString());
    }

    [Theory]
    [InlineData(false, false, false, "warning", "storage.alert.poolLocked")]
    [InlineData(false, true, false, null, null)]
    [InlineData(false, true, true, "critical", "storage.alert.poolLockedVault")]
    [InlineData(true, false, false, null, null)]
    public void ALockedPoolWarnsWhileItWaitsForItsKeyAndIsCriticalWhenTheVaultFailed(bool readOnly, bool inVault, bool failed, string? severity, string? code)
    {
        var alerts = Evaluate(Pool() with { ReadOnly = readOnly, Encryption = new PoolEncryption(true, inVault, failed) });

        if (severity is null)
        {
            Assert.Empty(alerts);
            return;
        }
        var alert = Assert.Single(alerts);
        Assert.Equal(("pool.locked:tank", severity, code), (alert.Key, alert.Severity, alert.Code));
        Assert.Empty(Evaluate(Pool() with { Encryption = new PoolEncryption(false, inVault, false) }));
    }

    [Fact]
    public void ADegradedPoolIsCritical()
    {
        var pool = Assert.Single(ZpoolStatusParser.Parse(Read("zfs-degraded", "zpool-status")));

        var alert = Assert.Single(StorageAlerts.Evaluate(new PoolList([new Pool(pool.Name, pool.PoolGuid, pool.State, 1, 0, 1, 0, 0, false, pool.DataErrors, pool.Scan, pool.Vdevs)], []), [], [], _now));

        Assert.Equal(($"pool.degraded:{pool.Name}", "critical", "storage.alert.poolDegraded"), (alert.Key, alert.Severity, alert.Code));
    }

    [Theory]
    [InlineData("FAULTED")]
    [InlineData("UNAVAIL")]
    public void AFaultedPoolIsCriticalWithItsState(string state)
    {
        var alert = Assert.Single(Evaluate(Pool(state: state)));

        Assert.Equal(("pool.faulted:tank", "critical"), (alert.Key, alert.Severity));
        Assert.Equal(state, alert.Params["state"].GetString());
    }

    [Fact]
    public void ASuspendedPoolHasItsOwnCriticalAlert()
    {
        var alert = Assert.Single(Evaluate(Pool(state: "SUSPENDED")));

        Assert.Equal(("pool.suspended:tank", "critical", "storage.alert.poolSuspended"), (alert.Key, alert.Severity, alert.Code));
    }

    [Fact]
    public void EveryMissingDiskWarnsWithItsSerialNextToTheDegradedPool()
    {
        Vdev Leaf(string guid, bool missing, string? serial) =>
            new($"scsi-{guid}", guid, "disk", "data", missing ? "REMOVED" : "ONLINE", $"/dev/disk/by-id/scsi-{guid}-part1", null, 0, 0, 0, [], missing, serial);
        var mirror = new Vdev("mirror-0", "m", "mirror", "data", "DEGRADED", null, null, 0, 0, 0, [Leaf("a", false, "VYXTEST01"), Leaf("b", true, "VYXTEST02"), Leaf("c", true, null)]);

        var alerts = Evaluate(Pool(state: "DEGRADED") with { Vdevs = [mirror] });

        Assert.Equal(
            [("pool.degraded:tank", "critical"), ("disk.missing:tank:b", "warning"), ("disk.missing:tank:c", "warning")],
            alerts.Select(alert => (alert.Key, alert.Severity)));
        Assert.Equal(["VYXTEST02", "scsi-c"], alerts.Skip(1).Select(alert => alert.Params["disk"].GetString()));
    }

    [Fact]
    public void AFinishedScrubWithErrorsWarnsUntilAScrubFindsNone()
    {
        var withErrors = new PoolScan("scrub", "finished", false, _now.AddHours(-2), _now, 100, 100, 3);

        var alert = Assert.Single(Evaluate(Pool(scan: withErrors)));
        Assert.Equal(("pool.scrub_errors:tank", "warning", 3L), (alert.Key, alert.Severity, alert.Params["errors"].GetInt64()));

        Assert.Empty(Evaluate(Pool(scan: withErrors with { State = "scanning", Errors = 0 })));
        Assert.Empty(Evaluate(Pool(scan: withErrors with { Errors = 0 })));
        Assert.Empty(Evaluate(Pool(scan: withErrors with { Kind = "resilver" })));
    }

    [Fact]
    public void PermanentDataErrorsAreCritical()
    {
        var alert = Assert.Single(Evaluate(Pool(dataErrors: 2)));

        Assert.Equal(("pool.data_errors:tank", "critical"), (alert.Key, alert.Severity));
    }

    [Fact]
    public void SmartFailuresAndWarningsNameTheDisk()
    {
        var failed = Assert.Single(StorageAlerts.Evaluate(new PoolList([], []), [Smart("failed")], [Sdb], _now));
        var warning = Assert.Single(StorageAlerts.Evaluate(new PoolList([], []), [Smart("warning", pending: 8, selfTest: "failed")], [Sdb], _now));

        Assert.Equal(($"disk.smart_failed:{Sdb.Id}", "critical", "sdb"), (failed.Key, failed.Severity, failed.Params["disk"].GetString()));
        Assert.Equal(($"disk.smart_warning:{Sdb.Id}", "warning"), (warning.Key, warning.Severity));
        Assert.Equal((8L, "failed", "WD-HANDWRITTEN2"), (warning.Params["pending"].GetInt64(), warning.Params["selfTest"].GetString(), warning.Params["serial"].GetString()));
    }

    [Fact]
    public void DisksWithoutSmartOrThatAreGoneRaiseNothing()
    {
        Assert.Empty(StorageAlerts.Evaluate(new PoolList([], []), [Smart("passed"), Smart("unsupported") with { DiskId = "x" }], [Sdb], _now));
        Assert.Empty(StorageAlerts.Evaluate(new PoolList([], []), [Smart("failed")], [], _now));
    }

    [Fact]
    public void AlertsVanishWhenTheCauseIsGoneAndCriticalOnesComeFirst()
    {
        var bad = Evaluate(Pool("a", capacity: 85), Pool("b", state: "DEGRADED"));
        Assert.Equal(["pool.degraded:b", "pool.capacity:a"], bad.Select(alert => alert.Key));

        Assert.Empty(Evaluate(Pool("a", capacity: 70), Pool("b")));
    }
}
