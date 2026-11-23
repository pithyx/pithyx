using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Backups;

public sealed class BackupDaysTests
{
    private static readonly TimeZoneInfo _berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static BackupDayRun Run(long job, string startedAt, BackupRunState state, BackupRunKind kind = BackupRunKind.Backup, long? added = null, long? size = null) =>
        new(job, kind, state, DateTimeOffset.Parse(startedAt, System.Globalization.CultureInfo.InvariantCulture), added, size);

    [Fact]
    public void ARunCountsOnTheDayItStartedInTheBoxsTimeZone()
    {
        // 23:30 UTC on 31 October is already 1 November in Berlin.
        var days = BackupDays.Days([Run(1, "2026-10-31T23:30:00Z", BackupRunState.Succeeded, added: 5)], new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 1), _berlin);

        Assert.Equal((0, 0L), (days[0].Succeeded, days[0].AddedBytes));
        Assert.Equal((1, 5L), (days[1].Succeeded, days[1].AddedBytes));
    }

    [Fact]
    public void AJobWithAFailedRunIsNotOkThatDayAndCancelledRunsAreNotCounted()
    {
        var days = BackupDays.Days(
            [
                Run(1, "2026-11-02T08:00:00Z", BackupRunState.Succeeded),
                Run(1, "2026-11-02T09:00:00Z", BackupRunState.Failed, BackupRunKind.Check),
                Run(2, "2026-11-02T10:00:00Z", BackupRunState.Warning),
                Run(3, "2026-11-02T11:00:00Z", BackupRunState.Interrupted),
                Run(4, "2026-11-02T12:00:00Z", BackupRunState.Cancelled),
            ],
            new DateOnly(2026, 11, 2),
            new DateOnly(2026, 11, 2),
            _berlin);

        var day = Assert.Single(days);
        Assert.Equal((1, 1, 2, 3, 1), (day.Succeeded, day.Warnings, day.Failed, day.Jobs, day.JobsOk));
    }

    [Fact]
    public void TheRepositoriesSizesAddUpAndCarryOverFromEarlierDays()
    {
        var days = BackupDays.Days(
            [
                Run(1, "2026-10-01T08:00:00Z", BackupRunState.Succeeded, size: 100),
                Run(2, "2026-11-02T08:00:00Z", BackupRunState.Succeeded, size: 50),
                Run(1, "2026-11-03T08:00:00Z", BackupRunState.Succeeded, size: 120),
            ],
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 4),
            _berlin);

        Assert.Equal([100L, 150L, 170L, 170L], days.Select(day => day.RepositoryBytes));
        Assert.Null(BackupDays.Days([], new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), _berlin)[0].RepositoryBytes);
    }

    [Fact]
    public void DaysBeginAtLocalMidnightAlsoWhenTheClocksChange()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-03-28T23:00:00Z", System.Globalization.CultureInfo.InvariantCulture), BackupDays.Start(new DateOnly(2026, 3, 29), _berlin));
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture), BackupDays.Start(new DateOnly(2026, 3, 30), _berlin));
        var days = BackupDays.Days([], new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 30), _berlin);
        Assert.Equal(TimeSpan.FromHours(23), days[1].End - days[1].Start);
        Assert.Equal(days[2].Start, days[1].End);
    }

    [Fact]
    public void ThePeriodDefaultsToTheLast30DaysAndRefusesWhatItCannotRead()
    {
        var today = new DateOnly(2026, 11, 23);

        Assert.Equal((new DateOnly(2026, 10, 25), today), BackupDays.Period(null, null, today));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), BackupDays.Period("2026-11-01", "2026-11-30", today));
        foreach (var (from, to) in new[] { ("2026-11-30", "2026-11-01"), ("2026-11-1", "2026-11-30"), ("2025-01-01", "2026-11-30"), ("x", null) })
        {
            var error = Assert.Throws<PithyxException>(() => BackupDays.Period(from, to, today));
            Assert.Equal(ErrorCodes.RequestBodyInvalid, error.Code);
        }
    }
}
