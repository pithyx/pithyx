using System.Globalization;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Backups;

/// <summary>What the runs of one day in the box's time zone came to.</summary>
/// <param name="Date">The day.</param>
/// <param name="Start">When it began, UTC.</param>
/// <param name="End">When it ended, UTC: the next day's start.</param>
/// <param name="Succeeded">Runs and checks that succeeded.</param>
/// <param name="Warnings">Runs that ended with warnings.</param>
/// <param name="Failed">Runs and checks that failed or were interrupted; cancelled ones are not counted.</param>
/// <param name="Jobs">Jobs with a counted run or check that day.</param>
/// <param name="JobsOk">Of these, the jobs none of whose runs failed that day.</param>
/// <param name="AddedBytes">Bytes the day's backup runs added to the repositories.</param>
/// <param name="RepositoryBytes">What the repositories of all jobs took on their targets at the end of the day, as last measured; null before any measurement.</param>
public sealed record BackupDay(DateOnly Date, DateTimeOffset Start, DateTimeOffset End, int Succeeded, int Warnings, int Failed, int Jobs, int JobsOk, long AddedBytes, long? RepositoryBytes);

/// <summary>The days of a period, oldest first, with every day in it.</summary>
/// <param name="TimeZone">The box's time zone, which the days follow.</param>
/// <param name="Days">One entry per day from the first to the last.</param>
public sealed record BackupDayList(string TimeZone, IReadOnlyList<BackupDay> Days);

/// <summary>A finished run as the day list reads it.</summary>
public sealed record BackupDayRun(long JobId, BackupRunKind Kind, BackupRunState State, DateTimeOffset StartedAt, long? AddedBytes, long? RepositoryBytes);

/// <summary>The Backup app's overview (#403), pure: runs grouped by the day they started in the box's time zone.</summary>
public static class BackupDays
{
    /// <summary>The longest period one request covers.</summary>
    public const int MaxDays = 400;

    /// <summary>The default period: the last 30 days up to today.</summary>
    public const int DefaultDays = 30;

    /// <summary>
    /// The period of a request: <paramref name="from"/> and <paramref name="to"/> as <c>yyyy-MM-dd</c>, both included;
    /// left out, the 30 days up to <paramref name="today"/>. Throws <see cref="ErrorCodes.RequestBodyInvalid"/>.
    /// </summary>
    public static (DateOnly From, DateOnly To) Period(string? from, string? to, DateOnly today)
    {
        var last = to is null ? today : Date(to);
        var first = from is null ? last.AddDays(1 - DefaultDays) : Date(from);
        if (first > last || last.DayNumber - first.DayNumber >= MaxDays)
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        return (first, last);
    }

    /// <summary>When the day begins, in UTC.</summary>
    public static DateTimeOffset Start(DateOnly date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A zone that moves its clocks at midnight skips it; the day then starts at the first time that exists.
        while (zone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(15);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeSpan.Zero);
    }

    /// <summary>
    /// One entry per day from <paramref name="from"/> to <paramref name="to"/>. <paramref name="runs"/> are the finished
    /// runs up to the end of the period, older ones included for the repositories' sizes.
    /// </summary>
    public static IReadOnlyList<BackupDay> Days(IEnumerable<BackupDayRun> runs, DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(zone);
        var ordered = runs.OrderBy(run => run.StartedAt).ToList();
        var sizes = new Dictionary<long, long>();
        var index = 0;
        var start = Start(from, zone);
        for (; index < ordered.Count && ordered[index].StartedAt < start; index++)
        {
            if (ordered[index].RepositoryBytes is { } bytes)
            {
                sizes[ordered[index].JobId] = bytes;
            }
        }
        var days = new List<BackupDay>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var end = Start(date.AddDays(1), zone);
            var begin = start;
            start = end;
            int succeeded = 0, warnings = 0, failed = 0;
            long added = 0;
            var jobs = new Dictionary<long, bool>();
            for (; index < ordered.Count && ordered[index].StartedAt < end; index++)
            {
                var run = ordered[index];
                if (run.RepositoryBytes is { } bytes)
                {
                    sizes[run.JobId] = bytes;
                }
                if (run.Kind == BackupRunKind.Backup)
                {
                    added += run.AddedBytes ?? 0;
                }
                switch (run.State)
                {
                    case BackupRunState.Succeeded:
                        succeeded++;
                        jobs.TryAdd(run.JobId, true);
                        break;
                    case BackupRunState.Warning:
                        warnings++;
                        jobs.TryAdd(run.JobId, true);
                        break;
                    case BackupRunState.Failed or BackupRunState.Interrupted:
                        failed++;
                        jobs[run.JobId] = false;
                        break;
                }
            }
            days.Add(new BackupDay(date, begin, end, succeeded, warnings, failed, jobs.Count, jobs.Count(job => job.Value), added, sizes.Count == 0 ? null : sizes.Values.Sum()));
        }
        return days;
    }

    private static DateOnly Date(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new PithyxException(ErrorCodes.RequestBodyInvalid);
}
