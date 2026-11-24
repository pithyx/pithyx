using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Backups;

/// <summary>What an administrator may set on a backup job. Throws <see cref="ErrorCodes.BackupJobInvalid"/> with the field.</summary>
public static class BackupJobRules
{
    public const int MaxNameLength = 64;
    public const int MaxSources = 32;
    public const int MaxExcludes = 100;
    public const int MaxExcludeLength = 500;
    public const int MaxKeep = 10_000;
    public const int MaxLimitKib = 100_000_000;
    public const int MaxOverdueHours = 24 * 366;
    public const int MaxRepositoryLength = 200;
    public const int MaxKeyLength = 1024;

    /// <summary>The overdue limit without a setting: three times the interval, but at least this.</summary>
    public static readonly TimeSpan MinimumOverdue = TimeSpan.FromDays(2);

    public static BackupJobSettings Check(BackupJobSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var name = (settings.Name ?? "").Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            throw Invalid("name");
        }
        var sources = CheckSources(settings.Sources);
        var schedule = CheckCron(settings.Schedule, "schedule");
        var retention = settings.Retention ?? throw Invalid("retention");
        int[] keeps = [retention.Last, retention.Hourly, retention.Daily, retention.Weekly, retention.Monthly, retention.Yearly];
        if (keeps.Any(keep => keep is < 0 or > MaxKeep))
        {
            throw Invalid("retention");
        }
        var excludes = (settings.Excludes ?? []).Select(pattern => (pattern ?? "").Trim()).Where(pattern => pattern.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (excludes.Count > MaxExcludes || excludes.Any(pattern => pattern.Length > MaxExcludeLength || pattern.Any(char.IsControl)))
        {
            throw Invalid("excludes");
        }
        var checkSchedule = string.IsNullOrWhiteSpace(settings.CheckSchedule) ? null : CheckCron(settings.CheckSchedule, "checkSchedule");
        if (settings.CheckReadPercent is < 0 or > 100)
        {
            throw Invalid("checkReadPercent");
        }
        if (settings.UploadLimitKib is < 0 or > MaxLimitKib)
        {
            throw Invalid("uploadLimitKib");
        }
        if (settings.DownloadLimitKib is < 0 or > MaxLimitKib)
        {
            throw Invalid("downloadLimitKib");
        }
        if (settings.OverdueHours is < 1 or > MaxOverdueHours)
        {
            throw Invalid("overdueHours");
        }
        return settings with
        {
            Name = name,
            Sources = sources,
            Schedule = schedule,
            Excludes = excludes,
            CheckSchedule = checkSchedule,
        };
    }

    /// <summary>
    /// The repository's folder below the target's path: relative, with <c>/</c> between its parts, no quotes,
    /// backslashes, control characters, colons, empty parts, <c>.</c> or <c>..</c>.
    /// </summary>
    public static string CheckRepository(string? input)
    {
        var repository = (input ?? "").Trim().Trim('/');
        if (repository.Length is 0 or > MaxRepositoryLength
            || repository.Any(character => char.IsControl(character) || character is '"' or '\\' or ':')
            || repository.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw Invalid("repository");
        }
        return repository;
    }

    /// <summary>The key of an existing repository, as the administrator pasted it.</summary>
    public static string CheckKey(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var key = input.Trim();
        if (key.Length is 0 or > MaxKeyLength || key.Any(char.IsControl))
        {
            throw Invalid("existingKey");
        }
        return key;
    }

    /// <summary>Where the repository is on the server: the target's path and the job's folder.</summary>
    public static string RemotePath(string targetPath, string repository) => targetPath switch
    {
        "" => repository,
        "/" => "/" + repository,
        _ => targetPath + "/" + repository,
    };

    /// <summary>
    /// How long a job may go without a successful run: the setting, or three times the time between two runs of its
    /// schedule, at least <see cref="MinimumOverdue"/>.
    /// </summary>
    public static TimeSpan OverdueLimit(string schedule, int? overdueHours, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (overdueHours is { } hours)
        {
            return TimeSpan.FromHours(hours);
        }
        var cron = CronSchedule.Parse(schedule);
        if (cron.Next(now, zone) is not { } first || cron.Next(first, zone) is not { } second)
        {
            return MinimumOverdue;
        }
        var interval = second - first;
        return interval * 3 > MinimumOverdue ? interval * 3 : MinimumOverdue;
    }

    /// <summary>A folder below a shared folder: relative, with <c>/</c> between its parts; empty for the whole folder.</summary>
    public static string CheckSourcePath(string? input)
    {
        var path = (input ?? "").Trim().Trim('/');
        if (path.Length == 0)
        {
            return "";
        }
        if (path.Length > 1000 || path.Any(char.IsControl) || path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw Invalid("sources");
        }
        return path;
    }

    /// <summary>The mounted backup caches of <c>zfs list -H -o name,mounted,mountpoint -t filesystem</c>.</summary>
    public static IReadOnlyList<string> CacheRoots(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        var result = new List<string>();
        foreach (var line in standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length == 3 && fields[0].EndsWith("/pithyx/backup-cache", StringComparison.Ordinal) && fields[0].Count(c => c == '/') == 2
                && fields[1] == "yes" && fields[2].StartsWith('/'))
            {
                result.Add(fields[2]);
            }
        }
        return result;
    }

    public static PithyxException Invalid(string field) =>
        new(ErrorCodes.BackupJobInvalid, new Dictionary<string, object?> { ["field"] = field });

    private static List<BackupSource> CheckSources(IReadOnlyList<BackupSource>? sources)
    {
        if (sources is not { Count: > 0 and <= MaxSources })
        {
            throw Invalid("sources");
        }
        var result = new List<BackupSource>();
        foreach (var source in sources)
        {
            if (source is null || source.ShareId <= 0)
            {
                throw Invalid("sources");
            }
            var checkedSource = new BackupSource(source.ShareId, CheckSourcePath(source.Path));
            if (result.Contains(checkedSource))
            {
                continue;
            }
            result.Add(checkedSource);
        }
        return result;
    }

    private static string CheckCron(string? cron, string field)
    {
        var value = (cron ?? "").Trim();
        return CronSyntax.IsValid(value) ? value : throw Invalid(field);
    }
}
