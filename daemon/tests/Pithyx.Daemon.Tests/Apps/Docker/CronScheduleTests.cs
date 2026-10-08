using Pithyx.Daemon.Apps.Docker;

namespace Pithyx.Daemon.Tests.Apps.Docker;

/// <summary>When a manifest schedule fires next (A15).</summary>
public sealed class CronScheduleTests
{
    private static readonly TimeZoneInfo _berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTimeOffset Next(string cron, string after, TimeZoneInfo? zone = null) =>
        CronSchedule.Parse(cron).Next(DateTimeOffset.Parse(after, System.Globalization.CultureInfo.InvariantCulture), zone ?? TimeZoneInfo.Utc)!.Value;

    [Theory]
    [InlineData("0 3 * * *", "2026-10-08T03:00:00Z", "2026-10-09T03:00:00Z")]
    [InlineData("0 3 * * *", "2026-10-08T02:59:30Z", "2026-10-08T03:00:00Z")]
    [InlineData("*/15 * * * *", "2026-10-08T10:07:00Z", "2026-10-08T10:15:00Z")]
    [InlineData("30 8-10/2 * * 1-5", "2026-10-09T10:31:00Z", "2026-10-12T08:30:00Z")]
    [InlineData("0 0 1 * *", "2026-12-15T00:00:00Z", "2027-01-01T00:00:00Z")]
    [InlineData("0 0 29 2 *", "2026-03-01T00:00:00Z", "2028-02-29T00:00:00Z")]
    [InlineData("0 12 * * 0", "2026-10-08T00:00:00Z", "2026-10-11T12:00:00Z")]
    [InlineData("0 12 * * 7", "2026-10-08T00:00:00Z", "2026-10-11T12:00:00Z")]
    public void TheNextTimeFollowsTheFields(string cron, string after, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Next(cron, after));

    [Fact]
    public void ARestrictedDayOfMonthAndDayOfWeekFireOnEither()
    {
        // The 13th, or any Friday: 2026-10-09 is a Friday, before the 13th.
        Assert.Equal(DateTimeOffset.Parse("2026-10-09T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), Next("0 0 13 * 5", "2026-10-08T12:00:00Z"));
    }

    [Fact]
    public void LocalTimeSkipsTheMissingHourAndFiresOnceInTheRepeatedOne()
    {
        // 2027-03-28 02:30 does not exist in Berlin; the next 02:30 is the day after.
        Assert.Equal(new DateTimeOffset(2027, 3, 29, 2, 30, 0, TimeSpan.FromHours(2)), Next("30 2 * * *", "2027-03-28T00:00:00Z", _berlin));
        // 2026-10-25 02:30 happens twice; the schedule fires at the first.
        var first = Next("30 2 * * *", "2026-10-24T22:00:00Z", _berlin);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)), first);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 2, 30, 0, TimeSpan.FromHours(1)), CronSchedule.Parse("30 2 * * *").Next(first, _berlin));
    }
}
