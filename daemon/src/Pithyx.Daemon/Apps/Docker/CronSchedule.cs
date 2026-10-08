using System.Globalization;
using Pithyx.Daemon.Apps.Manifest;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>
/// When a manifest schedule fires next (A15): the five fields of <see cref="CronSyntax"/> in the box's time zone. As in
/// classic cron, a restricted day of month and a restricted day of week fire on either; a time that a switch to daylight
/// saving time skips does not fire that day, and one it repeats fires once.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7];
    private readonly bool _anyDay;
    private readonly bool _anyWeekday;

    private CronSchedule(string cron)
    {
        var fields = cron.Split(' ');
        Fill(_minutes, fields[0], 0, 59);
        Fill(_hours, fields[1], 0, 23);
        Fill(_days, fields[2], 1, 31);
        Fill(_months, fields[3], 1, 12);
        var weekdays = new bool[8];
        Fill(weekdays, fields[4], 0, 7);
        for (var day = 0; day < 7; day++)
        {
            _weekdays[day] = weekdays[day] || (day == 0 && weekdays[7]);
        }
        _anyDay = fields[2].StartsWith('*');
        _anyWeekday = fields[4].StartsWith('*');
    }

    /// <summary>The schedule of a cron expression the manifest validator accepted.</summary>
    public static CronSchedule Parse(string cron)
    {
        if (!CronSyntax.IsValid(cron))
        {
            throw new ArgumentException($"Not a valid schedule: {cron}.", nameof(cron));
        }
        return new CronSchedule(cron);
    }

    /// <summary>The first time after <paramref name="after"/> the schedule fires, within the next four years, or null.</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var end = candidate.AddYears(4);
        while (candidate < end)
        {
            if (!_months[candidate.Month] || !DayMatches(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }
            if (!_hours[candidate.Hour])
            {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }
            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }
            if (!zone.IsInvalidTime(candidate))
            {
                var offset = zone.IsAmbiguousTime(candidate) ? zone.GetAmbiguousTimeOffsets(candidate).Max() : zone.GetUtcOffset(candidate);
                var at = new DateTimeOffset(candidate, offset);
                if (at > after)
                {
                    return at;
                }
            }
            candidate = candidate.AddMinutes(1);
        }
        return null;
    }

    private bool DayMatches(DateTime date)
    {
        var day = _days[date.Day];
        var weekday = _weekdays[(int)date.DayOfWeek];
        return _anyDay || _anyWeekday ? day && weekday : day || weekday;
    }

    private static void Fill(bool[] target, string field, int min, int max)
    {
        foreach (var item in field.Split(','))
        {
            var slash = item.IndexOf('/', StringComparison.Ordinal);
            var range = slash < 0 ? item : item[..slash];
            var step = slash < 0 ? 1 : int.Parse(item[(slash + 1)..], CultureInfo.InvariantCulture);
            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else if (range.IndexOf('-', StringComparison.Ordinal) is var dash and >= 0)
            {
                (from, to) = (int.Parse(range[..dash], CultureInfo.InvariantCulture), int.Parse(range[(dash + 1)..], CultureInfo.InvariantCulture));
            }
            else
            {
                from = to = int.Parse(range, CultureInfo.InvariantCulture);
            }
            for (var value = from; value <= to; value += step)
            {
                target[value] = true;
            }
        }
    }
}
