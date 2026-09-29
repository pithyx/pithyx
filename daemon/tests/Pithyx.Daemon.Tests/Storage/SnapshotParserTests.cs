using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

public sealed class SnapshotParserTests
{
    [Fact]
    public void SnapshotsCarryTheirDatasetCreationAndHolds()
    {
        var snapshots = ZfsSnapshotParser.Parse(Read("default", "zfs-list-snapshots"));

        Assert.Equal(
            ["pithyxtestm@pithyx-manual-20260920T101500Z", "pithyxtestm@before-upgrade", "pithyxtestm@pithyx-daily-20260926T000000Z", "pithyxtestm@pithyx-daily-20260927T000000Z", "pithyxtests@pithyx-hourly-20260927T200000Z"],
            snapshots.Select(snapshot => snapshot.FullName));
        var held = snapshots[1];
        Assert.Equal(("pithyxtestm", "before-upgrade", 1, 16384L, 98304L), (held.Dataset, held.Name, held.Holds, held.UsedBytes, held.ReferencedBytes));
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 8, 30, 12, TimeSpan.Zero), held.CreatedAt);
        Assert.Null(held.PithyxKind);
        Assert.Equal(["manual", "daily", "daily", "hourly"], snapshots.Select(snapshot => snapshot.PithyxKind).OfType<string>());
    }

    [Fact]
    public void NoSnapshotsIsAnEmptyList()
    {
        Assert.Empty(ZfsSnapshotParser.Parse(""));
        Assert.Empty(ZfsSnapshotParser.Parse("""{"output_version":{"command":"zfs list","vers_major":0,"vers_minor":1},"datasets":{}}"""));
    }

    [Fact]
    public void FileSystemsInTheListAreLeftOut()
    {
        Assert.Empty(ZfsSnapshotParser.Parse(Read("zfs-raidz1", "zfs-list")));
    }

    [Theory]
    [InlineData("pithyx-hourly-20260927T210000Z", "hourly")]
    [InlineData("pithyx-manual-20260927T210000Z", "manual")]
    [InlineData("pithyx-monthly-20260101T000000Z", "monthly")]
    [InlineData("pithyx-hourly-20260927T210000", null)]
    [InlineData("pithyx-yearly-20260927T210000Z", null)]
    [InlineData("xpithyx-hourly-20260927T210000Z", null)]
    [InlineData("pithyx-hourly-20260927T210000Z ", null)]
    [InlineData("before-upgrade", null)]
    public void OnlyExactPithyxNamesHaveAKind(string name, string? kind)
    {
        Assert.Equal(kind, SnapshotNames.KindOf(name));
    }

    [Fact]
    public void NamesUseTheUtcTime()
    {
        Assert.Equal("pithyx-daily-20260927T210005Z", SnapshotNames.For("daily", new DateTimeOffset(2026, 9, 27, 23, 0, 5, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void CommandsNameExactlyOneSnapshot()
    {
        Assert.Equal(["snapshot", "tank/data@pithyx-manual-20260927T210000Z"], SnapshotPlans.Take("tank/data", "pithyx-manual-20260927T210000Z").Arguments);
        Assert.Equal(["destroy", "tank/data@pithyx-daily-20260927T000000Z"], SnapshotPlans.Destroy("tank/data", "pithyx-daily-20260927T000000Z").Arguments);
        Assert.Equal(["hold", "pithyx", "tank/data@x"], SnapshotPlans.Hold("tank/data", "x").Arguments);
        Assert.Equal(["release", "pithyx", "tank/data@x"], SnapshotPlans.Release("tank/data", "x").Arguments);
        Assert.Equal(["rollback", "tank/data@x"], SnapshotPlans.Rollback("tank/data", "x", discardNewer: false).Arguments);
        Assert.Equal(["rollback", "-r", "tank/data@x"], SnapshotPlans.Rollback("tank/data", "x", discardNewer: true).Arguments);
    }

    [Theory]
    [InlineData("tank/data", "before-upgrade")]
    [InlineData("tank/data", "pithyx-daily-20260927T000000Z%pithyx-daily-20260928T000000Z")]
    [InlineData("tank/data", "pithyx-daily-2026")]
    [InlineData("tank/data@a", "pithyx-daily-20260927T000000Z")]
    [InlineData("", "pithyx-daily-20260927T000000Z")]
    [InlineData("-r", "pithyx-daily-20260927T000000Z")]
    public void DestroyRefusesAnythingButOnePithyxSnapshot(string dataset, string name)
    {
        Assert.Throws<ArgumentException>(() => SnapshotPlans.Destroy(dataset, name));
    }

    [Theory]
    [InlineData("a,b")]
    [InlineData("a%b")]
    [InlineData("a@b")]
    [InlineData("")]
    public void NoCommandReachesMoreThanOneSnapshot(string name)
    {
        Assert.Throws<ArgumentException>(() => SnapshotPlans.Take("tank/data", name));
        Assert.Throws<ArgumentException>(() => SnapshotPlans.Hold("tank/data", name));
        Assert.Throws<ArgumentException>(() => SnapshotPlans.Rollback("tank/data", name, discardNewer: false));
    }

    [Theory]
    [InlineData("hourly", "2026-09-27T21:30:00Z", "2026-09-27T21:00:00Z", "2026-09-27T22:00:00Z")]
    [InlineData("daily", "2026-09-27T21:30:00Z", "2026-09-27T00:00:00Z", "2026-09-28T00:00:00Z")]
    [InlineData("weekly", "2026-09-27T21:30:00Z", "2026-09-21T00:00:00Z", "2026-09-28T00:00:00Z")]
    [InlineData("weekly", "2026-09-28T00:00:00Z", "2026-09-28T00:00:00Z", "2026-10-05T00:00:00Z")]
    [InlineData("monthly", "2026-12-31T23:59:59Z", "2026-12-01T00:00:00Z", "2027-01-01T00:00:00Z")]
    public void PeriodsAreUtcHoursDaysIsoWeeksAndMonths(string kind, string now, string start, string end)
    {
        var period = SnapshotScheduler.Period(kind, DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal((DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(end, System.Globalization.CultureInfo.InvariantCulture)), period);
    }
}
