using System.Diagnostics;
using System.Text.Json;

namespace Vyxis.Daemon.Jobs;

/// <summary>
/// A job held in memory while it is queued or running and for a while after it finished. <see cref="JobService"/>
/// changes it under <see cref="Lock"/>; everyone else reads it through <see cref="Snapshot"/>.
/// </summary>
internal sealed class JobEntry(long id, long userId, string kind, JsonElement? parameters, string requestId, DateTimeOffset createdAt, Func<JobContext, Task> work)
    : IDisposable
{
    public long Id { get; } = id;
    public long UserId { get; } = userId;
    public string Kind { get; } = kind;
    public string RequestId { get; } = requestId;

    public Lock Lock { get; } = new();

    /// <summary>Cancelled by a user's cancel while the job runs.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Serializes the job's database writes, so an older progress write never lands after a newer state.</summary>
    public SemaphoreSlim Writes { get; } = new(1, 1);

    public Func<JobContext, Task>? Work { get; set; } = work;
    public JobState State { get; set; } = JobState.Queued;
    public JobProgress Progress { get; set; } = JobProgress.None;
    public JsonElement? Result { get; set; }
    public string? ErrorCode { get; set; }
    public IReadOnlyDictionary<string, object?>? ErrorParams { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool CancelRequested { get; set; }

    /// <summary>The activity the work runs in, so progress published from a timer carries the job's request id.</summary>
    public Activity? Activity { get; set; }

    public TrailingThrottle? PublishThrottle { get; set; }
    public TrailingThrottle? PersistThrottle { get; set; }

    public bool FinishedBefore(DateTimeOffset cutoff)
    {
        lock (Lock)
        {
            return FinishedAt is { } finishedAt && finishedAt <= cutoff;
        }
    }

    public static bool IsFinal(JobState state) => state is not (JobState.Queued or JobState.Running);

    public Job Snapshot()
    {
        lock (Lock)
        {
            return new Job(Id, Kind, State, Progress, parameters, Result, ErrorCode, ErrorParams, RequestId, createdAt, StartedAt, FinishedAt);
        }
    }

    public void Dispose()
    {
        PublishThrottle?.Dispose();
        PersistThrottle?.Dispose();
        Cancellation.Dispose();
        Writes.Dispose();
    }
}

/// <summary>
/// Runs an action at most once per interval: right away when the last run is at least an interval ago, otherwise
/// once when the interval is over. A signal in between costs a lock; the action reads the newest state itself.
/// </summary>
internal sealed class TrailingThrottle(TimeSpan interval, TimeProvider time, Action action, DateTimeOffset lastRun) : IDisposable
{
    private readonly Lock _lock = new();
    private DateTimeOffset _lastRun = lastRun;
    private ITimer? _timer;
    private bool _disposed;

    public void Signal()
    {
        lock (_lock)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }
            var now = time.GetUtcNow();
            var wait = _lastRun + interval - now;
            if (wait > TimeSpan.Zero)
            {
                _timer = time.CreateTimer(_ => Elapsed(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }
            _lastRun = now;
        }
        action();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Elapsed()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed)
            {
                return;
            }
            _lastRun = time.GetUtcNow();
        }
        action();
    }
}
