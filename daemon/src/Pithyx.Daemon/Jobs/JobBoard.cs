using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Jobs;

/// <summary>
/// The jobs held in memory: queued and running ones, and those finished within <see cref="Retention"/>. It is the
/// state holder of the <c>jobs/+</c> topics and needs nothing that depends on <see cref="TopicHub"/>.
/// </summary>
public sealed class JobBoard(TimeProvider time)
{
    public const string Pattern = "jobs/+";
    public const string ActiveTopic = "jobs";
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

    private const string TopicPrefix = "jobs/";

    private readonly ConcurrentDictionary<long, JobEntry> _jobs = new();

    public static string TopicOf(long jobId) => TopicPrefix + jobId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a user may see a job of <paramref name="ownerId"/>: owners their own, administrators every job.</summary>
    public static bool MaySee(ClaimsPrincipal user, long ownerId)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }
        return user.IsInRole(SessionAuthenticationHandler.AdminRole)
            || (long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id == ownerId);
    }

    /// <summary>
    /// The per-user topic of the jobs a user has queued or running: its snapshot lists them, and every change of one
    /// of them reaches its owner alone, the final one included. Administrators get only their own jobs here.
    /// </summary>
    public TopicDefinition ActiveDefinition() => new(
        ActiveTopic,
        typeof(JobList),
        typeof(Job),
        (_, _) => ValueTask.FromResult<object>(new OwnedJobs([.. _jobs.Values.Select(entry => new OwnedJob(entry.UserId, entry.Snapshot()))])),
        TopicDefinition.AnyUser,
        ForUser: (user, data) =>
        {
            var userId = long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1;
            return data switch
            {
                OwnedJobs all => new JobList([.. all.Jobs.Where(job => job.UserId == userId && !JobEntry.IsFinal(job.Job.State)).Select(job => job.Job).OrderBy(job => job.Id)]),
                OwnedJob change when change.UserId == userId => change.Job,
                _ => null,
            };
        });

    public TopicDefinition Definition() => new(
        Pattern,
        typeof(Job),
        typeof(Job),
        (topic, _) => ValueTask.FromResult<object>(
            IdOf(topic) is { } id && Find(id) is { } entry ? entry.Snapshot() : throw new PithyxException(ErrorCodes.JobsJobNotFound)),
        IsAllowed,
        MinInterval: PublishInterval,
        CurrentTopics: CurrentTopics);

    internal void Add(JobEntry entry)
    {
        Prune();
        _jobs[entry.Id] = entry;
    }

    /// <summary>Takes every job of a user out of memory and returns them.</summary>
    internal IReadOnlyList<JobEntry> RemoveUser(long userId)
    {
        var removed = new List<JobEntry>();
        foreach (var (id, entry) in _jobs)
        {
            if (entry.UserId == userId && _jobs.TryRemove(id, out _))
            {
                removed.Add(entry);
            }
        }
        return removed;
    }

    internal JobEntry? Find(long jobId) => _jobs.TryGetValue(jobId, out var entry) ? entry : null;

    private IEnumerable<string> CurrentTopics()
    {
        Prune();
        return [.. _jobs.Keys.Order().Select(TopicOf)];
    }

    // Jobs that are not in memory (finished long ago, or unknown) are only visible to administrators; delivery checks
    // every event again, so a subscription to a future job id never reaches another user's job.
    private bool IsAllowed(ClaimsPrincipal user, string topic)
    {
        if (IdOf(topic) is { } id && Find(id) is { } entry)
        {
            return MaySee(user, entry.UserId);
        }
        return user.Identity?.IsAuthenticated == true && user.IsInRole(SessionAuthenticationHandler.AdminRole);
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - Retention;
        foreach (var (id, entry) in _jobs)
        {
            if (entry.FinishedBefore(cutoff) && _jobs.TryRemove(id, out _))
            {
                entry.Dispose();
            }
        }
    }

    private static long? IdOf(string topic) =>
        topic.StartsWith(TopicPrefix, StringComparison.Ordinal)
            && long.TryParse(topic.AsSpan(TopicPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}

/// <summary>A job change as published on <see cref="JobBoard.ActiveTopic"/>; only its owner receives it.</summary>
internal sealed record OwnedJob(long UserId, Job Job);

internal sealed record OwnedJobs(IReadOnlyList<OwnedJob> Jobs);
