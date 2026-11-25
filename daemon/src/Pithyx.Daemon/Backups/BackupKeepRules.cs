using System.Globalization;

namespace Pithyx.Daemon.Backups;

/// <summary>A version the retention judges: its name, when it was made and whether it is protected.</summary>
public sealed record KeepCandidate(string Name, DateTimeOffset Time, bool Protected);

/// <summary>
/// The retention of server backups (D420), with the rules of restic's <c>forget</c> so both kinds of backup keep alike:
/// newest first, each rule keeps the newest version of each hour, day, ISO week, month or year in the box's time zone
/// until its count is used up; <c>last</c> keeps the newest versions. A protected version is always kept and still
/// counts for the rules, as restic's <c>--keep-tag</c> does.
/// </summary>
public static class BackupKeepRules
{
    /// <summary>
    /// The versions to keep, each with why; every other version goes. When the retention keeps everything, every version
    /// is kept without a reason.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<BackupKeepReason>> Keep(IEnumerable<KeepCandidate> versions, BackupRetention retention, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(zone);
        var ordered = versions.OrderByDescending(version => version.Time).ThenByDescending(version => version.Name, StringComparer.Ordinal).ToList();
        if (retention.KeepsAll)
        {
            return ordered.ToDictionary(
                version => version.Name,
                version => (IReadOnlyList<BackupKeepReason>)(version.Protected ? [BackupKeepReason.Protected] : []));
        }
        (BackupKeepReason Reason, int Count, Func<DateTime, string?> Bucket)[] rules =
        [
            (BackupKeepReason.Last, retention.Last, _ => null),
            (BackupKeepReason.Hourly, retention.Hourly, local => local.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)),
            (BackupKeepReason.Daily, retention.Daily, local => local.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            (BackupKeepReason.Weekly, retention.Weekly, local => string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(local)}-{ISOWeek.GetWeekOfYear(local)}")),
            (BackupKeepReason.Monthly, retention.Monthly, local => local.ToString("yyyyMM", CultureInfo.InvariantCulture)),
            (BackupKeepReason.Yearly, retention.Yearly, local => local.ToString("yyyy", CultureInfo.InvariantCulture)),
        ];
        var left = rules.Select(rule => rule.Count).ToArray();
        var lastBucket = new string?[rules.Length];
        var result = new Dictionary<string, IReadOnlyList<BackupKeepReason>>(StringComparer.Ordinal);
        foreach (var version in ordered)
        {
            var local = TimeZoneInfo.ConvertTime(version.Time, zone).DateTime;
            var reasons = new List<BackupKeepReason>();
            if (version.Protected)
            {
                reasons.Add(BackupKeepReason.Protected);
            }
            for (var index = 0; index < rules.Length; index++)
            {
                if (left[index] <= 0)
                {
                    continue;
                }
                var bucket = rules[index].Bucket(local);
                if (bucket is not null && bucket == lastBucket[index])
                {
                    continue;
                }
                lastBucket[index] = bucket;
                left[index]--;
                reasons.Add(rules[index].Reason);
            }
            if (reasons.Count > 0)
            {
                result[version.Name] = reasons;
            }
        }
        return result;
    }
}
