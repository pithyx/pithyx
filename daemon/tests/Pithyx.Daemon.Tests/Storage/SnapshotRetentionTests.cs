using Pithyx.Daemon.Storage.Snapshots;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>Retention as a pure function: hand-written cases, clock jumps and generated time series.</summary>
public sealed class SnapshotRetentionTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 27, 21, 0, 0, TimeSpan.Zero);
    private static readonly string[] _kinds = ["hourly", "daily", "weekly", "monthly"];
    private static readonly string[] _foreignNames = ["before-upgrade", "autosnap_2026-09-27_21:00:00_hourly", "zfs-auto-snap_hourly-2026-09-27-2100", "pithyx-hourly", "pithyx-hourly-2026", "Pithyx-hourly-20260927T210000Z", "pithyx-hourly-20260927T210000Z-copy", "pithyx-yearly-20260927T210000Z"];

    private static Snapshot Taken(string kind, DateTimeOffset at, int holds = 0, long txg = 0) =>
        new("tank/data", SnapshotNames.For(kind, at), at, 0, 0, holds, txg);

    private static Snapshot Foreign(string name, DateTimeOffset at) => new("tank/data", name, at, 0, 0, 0, 0);

    private static List<Snapshot> Hourly(DateTimeOffset from, int count) =>
        [.. Enumerable.Range(0, count).Select(hour => Taken("hourly", from.AddHours(hour)))];

    private static string[] Names(IEnumerable<Snapshot> snapshots) => [.. snapshots.Select(snapshot => snapshot.Name)];

    [Fact]
    public void KeepsTheNewestAndDeletesTheRestOldestFirst()
    {
        var snapshots = Hourly(_start, 30);

        var deleted = SnapshotRetention.Prune(snapshots, "hourly", 24, _start.AddHours(30));

        Assert.Equal(Names(snapshots.Take(6)), Names(deleted));
    }

    [Fact]
    public void NothingIsDeletedWhileThereAreNoMoreThanKeep()
    {
        Assert.Empty(SnapshotRetention.Prune(Hourly(_start, 24), "hourly", 24, _start.AddDays(2)));
        Assert.Empty(SnapshotRetention.Prune([], "hourly", 24, _start));
    }

    [Fact]
    public void OnlySnapshotsOfTheScheduleAreCandidates()
    {
        List<Snapshot> snapshots =
        [
            .. Hourly(_start, 3),
            Taken("daily", _start.AddDays(-10)),
            Taken("manual", _start.AddDays(-20)),
            .. _foreignNames.Select((name, index) => Foreign(name, _start.AddDays(-30 - index))),
        ];

        var deleted = SnapshotRetention.Prune(snapshots, "hourly", 1, _start.AddDays(1));

        Assert.Equal(Names(snapshots.Take(2)), Names(deleted));
    }

    [Fact]
    public void ManualSnapshotsAndUnknownKindsAreNeverPruned()
    {
        List<Snapshot> manual = [.. Enumerable.Range(0, 10).Select(day => Taken("manual", _start.AddDays(-day)))];

        Assert.Empty(SnapshotRetention.Prune(manual, "manual", 1, _start));
        Assert.Empty(SnapshotRetention.Prune(manual, "yearly", 1, _start));
    }

    [Fact]
    public void HeldSnapshotsStayAndDoNotCount()
    {
        var snapshots = Hourly(_start, 5);
        snapshots[0] = snapshots[0] with { Holds = 1 };
        snapshots[4] = snapshots[4] with { Holds = 2 };

        var deleted = SnapshotRetention.Prune(snapshots, "hourly", 2, _start.AddHours(5));

        // Countable are hours 1 to 3; the newest two of them stay.
        Assert.Equal([snapshots[1].Name], Names(deleted));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AtLeastOneAlwaysStays(int keep)
    {
        var snapshots = Hourly(_start, 5);

        var deleted = SnapshotRetention.Prune(snapshots, "hourly", keep, _start.AddHours(5));

        Assert.Equal(Names(snapshots.Take(4)), Names(deleted));
    }

    [Fact]
    public void ABoxThatWasOffForMonthsKeepsItsLastSnapshots()
    {
        var snapshots = Hourly(_start, 24);

        Assert.Empty(SnapshotRetention.Prune(snapshots, "hourly", 24, _start.AddMonths(4)));

        // The first snapshot after the pause replaces only the oldest one.
        snapshots.Add(Taken("hourly", _start.AddMonths(4)));
        Assert.Equal([snapshots[0].Name], Names(SnapshotRetention.Prune(snapshots, "hourly", 24, _start.AddMonths(4))));
    }

    [Fact]
    public void AClockFarBeforeEverySnapshotDeletesNothing()
    {
        var snapshots = Hourly(_start, 100);

        Assert.Empty(SnapshotRetention.Prune(snapshots, "hourly", 3, new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Empty(SnapshotRetention.Prune(snapshots, "hourly", 3, _start.AddYears(-3)));
    }

    [Fact]
    public void ABackwardsJumpNeverDeletesSnapshotsNewerThanNow()
    {
        // 24 snapshots up to 21:00, then NTP moves the clock back five hours.
        var snapshots = Hourly(_start, 24);
        var now = _start.AddHours(23).AddHours(-5);

        var deleted = SnapshotRetention.Prune(snapshots, "hourly", 3, now);

        Assert.All(deleted, snapshot => Assert.True(snapshot.CreatedAt <= now));
        Assert.Equal(Names(snapshots.Take(16)), Names(deleted));
    }

    [Fact]
    public void AfterABackwardsJumpNewSnapshotsAreNotDeletedRightAway()
    {
        // The clock was two years ahead when these were taken; now it is right again.
        var future = Hourly(_start.AddYears(2), 24);
        var now = _start;
        List<Snapshot> snapshots = [.. future, Taken("hourly", now)];

        Assert.Empty(SnapshotRetention.Prune(snapshots, "hourly", 24, now));

        // A day later the new series is pruned on its own; the future-dated ones stay untouched.
        snapshots.AddRange(Hourly(now.AddHours(1), 30));
        var later = now.AddHours(30);
        var deleted = SnapshotRetention.Prune(snapshots, "hourly", 24, later);
        Assert.Equal(7, deleted.Count);
        Assert.DoesNotContain(deleted, snapshot => future.Contains(snapshot));
        Assert.Contains(snapshots[^1], snapshots.Except(deleted));
    }

    [Fact]
    public void AForwardJumpOfYearsKeepsTheNewestCount()
    {
        var snapshots = Hourly(_start, 24);
        snapshots.Add(Taken("hourly", _start.AddYears(5)));

        var deleted = SnapshotRetention.Prune(snapshots, "hourly", 24, _start.AddYears(5));

        Assert.Equal([snapshots[0].Name], Names(deleted));
    }

    [Fact]
    public void SnapshotsTakenInTheSameSecondAreOrderedByName()
    {
        List<Snapshot> snapshots = [.. Enumerable.Range(0, 3).Select(second => Taken("hourly", _start) with { Name = SnapshotNames.For("hourly", _start.AddSeconds(second)) })];

        var deleted = SnapshotRetention.Prune(snapshots, "hourly", 2, _start.AddHours(1));

        Assert.Equal([snapshots[0].Name], Names(deleted));
    }

    [Fact]
    public void GeneratedSetsKeepEveryInvariant()
    {
        var random = new Random(20260927);
        for (var round = 0; round < 5000; round++)
        {
            var kind = _kinds[random.Next(_kinds.Length)];
            var keep = random.Next(-1, 30);
            var now = _start.AddHours(random.Next(-24 * 365 * 3, 24 * 365 * 3));
            var snapshots = new List<Snapshot>();
            var count = random.Next(0, 80);
            for (var index = 0; index < count; index++)
            {
                // Creation spread over six years around the start, so many lie after now and many before.
                var at = _start.AddMinutes(random.Next(-60 * 24 * 365 * 3, 60 * 24 * 365 * 3));
                var roll = random.Next(10);
                snapshots.Add(roll switch
                {
                    0 => Foreign(_foreignNames[random.Next(_foreignNames.Length)], at),
                    1 => Taken("manual", at),
                    2 => Taken(_kinds[random.Next(_kinds.Length)], at),
                    _ => Taken(kind, at, holds: random.Next(8) == 0 ? 1 : 0),
                });
            }

            var deleted = SnapshotRetention.Prune(snapshots, kind, keep, now);

            AssertInvariants(snapshots, kind, keep, now, deleted, $"round {round}");
        }
    }

    [Fact]
    public void GeneratedSchedulesWithClockJumpsNeverLoseTheirSnapshots()
    {
        var random = new Random(4711);
        for (var run = 0; run < 300; run++)
        {
            var kind = _kinds[random.Next(_kinds.Length)];
            var keep = random.Next(1, 12);
            var clock = _start;
            var snapshots = new List<Snapshot>();
            var txg = 0L;
            for (var step = 0; step < 150; step++)
            {
                clock = random.Next(20) switch
                {
                    0 => clock.AddHours(-random.Next(1, 48)),
                    1 => clock.AddDays(random.Next(30, 400)),
                    2 => clock.AddYears(-random.Next(1, 30)),
                    3 => clock.AddYears(random.Next(1, 10)),
                    _ => clock.AddHours(1),
                };
                var taken = Taken(kind, clock, txg: ++txg);
                if (snapshots.All(snapshot => snapshot.Name != taken.Name))
                {
                    snapshots.Add(taken);
                }
                var deleted = SnapshotRetention.Prune(snapshots, kind, keep, clock);
                AssertInvariants(snapshots, kind, keep, clock, deleted, $"run {run} step {step}");
                Assert.DoesNotContain(taken, deleted);
                snapshots.RemoveAll(deleted.Contains);
                Assert.True(snapshots.Count(snapshot => snapshot.CreatedAt <= clock) <= keep, $"run {run} step {step}: more than {keep} not after now");
            }
        }
    }

    private static void AssertInvariants(List<Snapshot> snapshots, string kind, int keep, DateTimeOffset now, IReadOnlyList<Snapshot> deleted, string label)
    {
        var candidates = snapshots.Where(snapshot => snapshot.PithyxKind == kind).ToList();
        foreach (var snapshot in deleted)
        {
            Assert.True(snapshot.Name.StartsWith($"pithyx-{kind}-", StringComparison.Ordinal), $"{label}: deleted {snapshot.Name}");
            Assert.Equal(kind, snapshot.PithyxKind);
            Assert.Equal(0, snapshot.Holds);
            Assert.True(snapshot.CreatedAt <= now, $"{label}: deleted {snapshot.Name} dated after now");
            Assert.Contains(snapshot, snapshots);
        }
        Assert.Equal(deleted.Count, deleted.Distinct().Count());
        if (candidates.Count > 0)
        {
            Assert.True(candidates.Count > deleted.Count, $"{label}: every snapshot of the schedule deleted");
        }
        var newest = candidates.OrderByDescending(snapshot => snapshot.CreatedAt).ThenByDescending(snapshot => snapshot.Name, StringComparer.Ordinal).Take(Math.Max(keep, 1));
        Assert.DoesNotContain(deleted, newest.Contains);
        var countable = candidates.Count(snapshot => snapshot.Holds == 0 && snapshot.CreatedAt <= now);
        Assert.Equal(Math.Max(0, countable - Math.Max(keep, 1)), deleted.Count);
        Assert.Equal(deleted.OrderBy(snapshot => snapshot.CreatedAt).ThenBy(snapshot => snapshot.Name, StringComparer.Ordinal), deleted);
    }
}
