using Pithyx.Daemon.Backups;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>The retention of server backups keeps as restic's forget does (D420).</summary>
public sealed class BackupKeepRulesTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void EachRuleKeepsTheNewestVersionOfItsPeriodsUntilItsCountIsUsedUp()
    {
        // Four days with two runs each, the newest first in the result.
        var versions = Enumerable.Range(0, 8)
            .Select(index => new KeepCandidate($"v{index}", new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero).AddHours(12 * index), false))
            .ToList();

        var keep = BackupKeepRules.Keep(versions, new BackupRetention(1, 0, 3, 0, 0, 0), Berlin);

        Assert.Equal(["v7", "v5", "v3"], keep.Keys.OrderByDescending(name => name, StringComparer.Ordinal));
        Assert.Equal([BackupKeepReason.Last, BackupKeepReason.Daily], keep["v7"]);
        Assert.Equal([BackupKeepReason.Daily], keep["v5"]);
    }

    [Fact]
    public void DaysAreTheBoxsDaysNotUtcDays()
    {
        // 23:30 UTC on the 20th is already the 21st in Berlin, like 08:00 UTC on the 21st.
        KeepCandidate[] versions =
        [
            new("late", new DateTimeOffset(2026, 11, 20, 23, 30, 0, TimeSpan.Zero), false),
            new("morning", new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero), false),
        ];

        var keep = BackupKeepRules.Keep(versions, new BackupRetention(0, 0, 2, 0, 0, 0), Berlin);

        Assert.Equal(["morning"], keep.Keys);
        Assert.Equal(2, BackupKeepRules.Keep(versions, new BackupRetention(0, 0, 2, 0, 0, 0), TimeZoneInfo.Utc).Count);
    }

    [Fact]
    public void WeeksAreIsoWeeksAndProtectedVersionsStayAndStillCount()
    {
        KeepCandidate[] versions =
        [
            new("sunday", new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero), true),
            new("monday", new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero), false),
            new("old", new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero), true),
        ];

        var keep = BackupKeepRules.Keep(versions, new BackupRetention(0, 0, 0, 1, 0, 0), Berlin);

        Assert.Equal([BackupKeepReason.Weekly], keep["monday"]);
        Assert.Equal([BackupKeepReason.Protected], keep["sunday"]);
        Assert.Equal([BackupKeepReason.Protected], keep["old"]);
    }

    [Fact]
    public void KeepingEverythingKeepsEveryVersionWithoutAReason()
    {
        KeepCandidate[] versions = [new("a", DateTimeOffset.UnixEpoch, false), new("b", DateTimeOffset.UnixEpoch.AddDays(1), true)];

        var keep = BackupKeepRules.Keep(versions, new BackupRetention(0, 0, 0, 0, 0, 0), Berlin);

        Assert.Empty(keep["a"]);
        Assert.Equal([BackupKeepReason.Protected], keep["b"]);
    }
}
