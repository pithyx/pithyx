using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Jobs;

/// <summary>
/// Long operations of users (copy, move, delete, ...) as persisted jobs with progress. A module starts a job with its
/// work; <see cref="JobRunner"/> runs queued jobs in order, at most <see cref="JobRunner.MaxConcurrentJobs"/> at once.
/// Every change is published on the job's topic <c>jobs/{id}</c> and to its owner on <c>jobs</c> (see
/// <see cref="JobBoard"/>). A job that ends creates a notification for its owner.
/// </summary>
public sealed partial class JobService
{
    /// <summary>Running jobs write their progress to the database at most this often; state changes right away.</summary>
    public static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(1);

    /// <summary>Finished jobs are deleted this long after they finished.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Jobs returned by <see cref="ListAsync"/>.</summary>
    public const int ListLimit = 100;

    /// <summary>Notification codes of a job that ended; <c>params.kind</c> is the job's kind, <c>params.jobId</c> its id.</summary>
    public const string JobSucceededCode = "jobs.job.succeeded";

    /// <summary>Also carries <c>params.errorCode</c> and <c>params.errorParams</c> (an object) of the error.</summary>
    public const string JobFailedCode = "jobs.job.failed";

    public const string JobCancelledCode = "jobs.job.cancelled";

    /// <summary>The daemon stopped while the job was queued or running.</summary>
    public const string JobInterruptedCode = "jobs.job.interrupted";

    private const string SelectColumns =
        """
        SELECT id AS Id, user_id AS UserId, kind AS Kind, state AS State, params AS Params, result AS Result,
            error_code AS ErrorCode, error_params AS ErrorParams, items_done AS ItemsDone, items_total AS ItemsTotal,
            bytes_done AS BytesDone, bytes_total AS BytesTotal, current_item AS CurrentItem, request_id AS RequestId,
            created_at AS CreatedAt, started_at AS StartedAt, finished_at AS FinishedAt
        FROM jobs
        """;

    private readonly Channel<JobEntry> _queue = Channel.CreateUnbounded<JobEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SqliteConnectionFactory _connections;
    private readonly JobBoard _board;
    private readonly TopicHub _hub;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger<JobService> _logger;
    private readonly JsonSerializerOptions _storage;

    public JobService(
        SqliteConnectionFactory connections,
        JobBoard board,
        TopicHub hub,
        NotificationService notifications,
        TimeProvider time,
        IOptions<JsonOptions> json,
        ILogger<JobService> logger)
    {
        ArgumentNullException.ThrowIfNull(json);
        _connections = connections;
        _board = board;
        _hub = hub;
        _notifications = notifications;
        _time = time;
        _logger = logger;
        // The API's conventions (camelCase, enums as strings), stored as written: the default escaping would turn
        // every non-ASCII character of a path into six bytes.
        _storage = new JsonSerializerOptions(json.Value.SerializerOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    }

    internal ChannelReader<JobEntry> Queue => _queue.Reader;

    /// <summary>
    /// Persists a queued job for <paramref name="userId"/> and returns it. <see cref="JobRunner"/> runs
    /// <paramref name="work"/> later inside an activity with the current request id. Work that throws a
    /// <see cref="PithyxException"/> fails with its code; <see cref="OperationCanceledException"/> after a cancel ends
    /// the job as cancelled; any other exception fails it with <see cref="ErrorCodes.SystemInternalUnexpected"/>.
    /// <paramref name="parameters"/> are serialized to JSON and returned with the job, for example the paths of a copy.
    /// </summary>
    public async Task<Job> StartAsync(long userId, string kind, object? parameters, Func<JobContext, Task> work, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(work);
        var requestId = Tracing.CurrentRequestId is { Length: > 0 } current ? current : ActivityTraceId.CreateRandom().ToHexString();
        var parametersJson = parameters is null ? null : JsonSerializer.Serialize(parameters, _storage);
        var now = _time.GetUtcNow();
        long id;
        await using (var connection = await _connections.OpenAsync(cancellationToken))
        {
            id = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO jobs (user_id, kind, state, params, request_id, created_at)
                VALUES (@userId, @kind, 'queued', @parametersJson, @requestId, @createdAt)
                RETURNING id
                """,
                new { userId, kind, parametersJson, requestId, createdAt = SessionService.Format(now) });
        }

        var entry = new JobEntry(id, userId, kind, Element(parametersJson), requestId, now, work);
        _board.Add(entry);
        var job = entry.Snapshot();
        LogQueued(id, kind, userId);
        Publish(entry, job);
        // Unbounded: writing only fails once the daemon stops, and then the next start marks the job as interrupted.
        _queue.Writer.TryWrite(entry);
        return job;
    }

    /// <summary>A job of the user, or any job for administrators. Others get <see cref="ErrorCodes.JobsJobNotFound"/>.</summary>
    public async Task<Job> GetAsync(ClaimsPrincipal user, long jobId, CancellationToken cancellationToken) =>
        (await FindVisibleAsync(user, jobId, cancellationToken)).Job;

    /// <summary>The user's <see cref="ListLimit"/> most recent jobs, newest first.</summary>
    public async Task<IReadOnlyList<Job>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userId = CurrentUser.Id(user);
        IEnumerable<JobRow> rows;
        await using (var connection = await _connections.OpenAsync(cancellationToken))
        {
            rows = await connection.QueryAsync<JobRow>(
                SelectColumns + " WHERE user_id = @userId ORDER BY created_at DESC, id DESC LIMIT @limit",
                new { userId, limit = ListLimit });
        }
        // Jobs in memory have fresher progress than their row.
        return [.. rows.Select(row => _board.Find(row.Id)?.Snapshot() ?? ToJob(row))];
    }

    /// <summary>
    /// Cancels a queued or running job of the user (administrators: any job). A queued job is cancelled right away; a
    /// running one when its work gives up. Cancelling a finished job changes nothing.
    /// </summary>
    public async Task CancelAsync(ClaimsPrincipal user, long jobId, CancellationToken cancellationToken)
    {
        await FindVisibleAsync(user, jobId, cancellationToken);
        if (_board.Find(jobId) is not { } entry)
        {
            // Only finished jobs leave memory.
            return;
        }
        await CancelEntryAsync(entry);
    }

    /// <summary>
    /// Cancels every queued or running job of a deleted user and forgets its jobs, so nothing of it stays in memory.
    /// The database drops the user's job rows with the user.
    /// </summary>
    public async Task ForgetUserAsync(long userId)
    {
        foreach (var entry in _board.RemoveUser(userId))
        {
            await CancelEntryAsync(entry);
        }
    }

    private async Task CancelEntryAsync(JobEntry entry)
    {
        var jobId = entry.Id;
        bool wasQueued;
        lock (entry.Lock)
        {
            switch (entry.State)
            {
                case JobState.Queued when !entry.CancelRequested:
                    // Without its work the runner skips the job; it turns cancelled once its row says so.
                    wasQueued = true;
                    entry.CancelRequested = true;
                    entry.Work = null;
                    break;
                case JobState.Running when !entry.CancelRequested:
                    wasQueued = false;
                    entry.CancelRequested = true;
                    break;
                default:
                    return;
            }
        }

        LogCancelRequested(jobId, entry.Kind);
        if (wasQueued)
        {
            await CompleteAsync(entry, entry.Snapshot() with { State = JobState.Cancelled, FinishedAt = _time.GetUtcNow() });
            LogFinished(jobId, entry.Kind, JobState.Cancelled);
        }
        else
        {
            await entry.Cancellation.CancelAsync();
        }
    }

    /// <summary>Deletes jobs that finished more than <see cref="Lifetime"/> ago. Returns how many were deleted.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(
            "DELETE FROM jobs WHERE finished_at IS NOT NULL AND finished_at <= @cutoff",
            new { cutoff = SessionService.Format(_time.GetUtcNow() - Lifetime) });
    }

    /// <summary>Marks jobs left queued or running by an earlier daemon process as interrupted. Runs before the runner starts.</summary>
    internal async Task InterruptUnfinishedAsync(CancellationToken cancellationToken)
    {
        List<(long Id, long UserId, string Kind)> interrupted;
        await using (var connection = await _connections.OpenAsync(cancellationToken))
        {
            interrupted = [.. await connection.QueryAsync<(long Id, long UserId, string Kind)>(
                "UPDATE jobs SET state = 'interrupted', finished_at = @now WHERE state IN ('queued', 'running') RETURNING id, user_id, kind",
                new { now = SessionService.Format(_time.GetUtcNow()) })];
        }
        if (interrupted.Count > 0)
        {
            LogInterrupted(interrupted.Count);
        }
        foreach (var job in interrupted.OrderBy(job => job.Id))
        {
            await NotifyFinishedAsync(job.UserId, job.Id, job.Kind, JobState.Interrupted, null, null);
        }
    }

    /// <summary>Runs a queued job to its final state. Never throws; a job cancelled while queued is skipped.</summary>
    internal async Task RunAsync(JobEntry entry, CancellationToken stopping)
    {
        Func<JobContext, Task> work;
        DateTimeOffset startedAt;
        lock (entry.Lock)
        {
            // A job still queued when the daemon stops stays queued and becomes interrupted with the next start.
            if (entry.State != JobState.Queued || entry.Work is null || stopping.IsCancellationRequested)
            {
                return;
            }
            work = entry.Work;
            startedAt = _time.GetUtcNow();
            entry.State = JobState.Running;
            entry.StartedAt = startedAt;
        }

        // The job continues the trace of the request that started it, so its logs and commands carry that request id.
        Activity.Current = null;
        var parent = new ActivityContext(ActivityTraceId.CreateFromString(entry.RequestId), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        using var activity = Tracing.Source.StartActivity("job.run", ActivityKind.Internal, parent);
        activity?.SetTag("pithyx.job.id", entry.Id);
        activity?.SetTag("pithyx.job.kind", entry.Kind);

        var state = JobState.Failed;
        var errorCode = ErrorCodes.SystemInternalUnexpected;
        IReadOnlyDictionary<string, object?>? errorParams = null;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, stopping);
            lock (entry.Lock)
            {
                entry.Activity = activity;
                entry.PublishThrottle = new TrailingThrottle(JobBoard.PublishInterval, _time, () => PublishProgress(entry), startedAt);
                entry.PersistThrottle = new TrailingThrottle(PersistInterval, _time, () => _ = Task.Run(() => PersistProgressAsync(entry), CancellationToken.None), startedAt);
            }
            await WriteStateAsync(entry);
            Publish(entry, entry.Snapshot());
            LogStarted(entry.Id, entry.Kind);

            try
            {
                await work(new JobContext(this, entry, cancellation.Token));
                (state, errorCode) = (JobState.Succeeded, null);
            }
            catch (PithyxException exception)
            {
                (errorCode, errorParams) = (exception.Code, exception.Parameters);
            }
            catch (OperationCanceledException) when (CancelRequested(entry))
            {
                (state, errorCode) = (JobState.Cancelled, null);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                (state, errorCode) = (JobState.Interrupted, null);
            }
        }
        catch (Exception exception)
        {
            LogUnexpected(exception, entry.Id, entry.Kind);
        }

        await FinishAsync(entry, state, errorCode, errorParams);
    }

    internal static void Report(JobEntry entry, JobProgress progress)
    {
        TrailingThrottle? publish;
        TrailingThrottle? persist;
        lock (entry.Lock)
        {
            if (entry.State != JobState.Running)
            {
                return;
            }
            entry.Progress = progress;
            (publish, persist) = (entry.PublishThrottle, entry.PersistThrottle);
        }
        publish?.Signal();
        persist?.Signal();
    }

    internal void SetResult(JobEntry entry, object result)
    {
        var json = Element(JsonSerializer.Serialize(result, _storage));
        lock (entry.Lock)
        {
            if (!JobEntry.IsFinal(entry.State))
            {
                entry.Result = json;
            }
        }
    }

    private async Task FinishAsync(JobEntry entry, JobState state, string? errorCode, IReadOnlyDictionary<string, object?>? errorParams)
    {
        try
        {
            // Stored and returned in the same form, whether the job is read from memory or from its row.
            var parameters = errorParams is null ? null : ErrorParamsFrom(JsonSerializer.Serialize(errorParams, _storage));
            lock (entry.Lock)
            {
                entry.Work = null;
                entry.Activity = null;
                entry.PublishThrottle?.Dispose();
                entry.PersistThrottle?.Dispose();
            }
            await CompleteAsync(entry, entry.Snapshot() with
            {
                State = state,
                FinishedAt = _time.GetUtcNow(),
                ErrorCode = errorCode,
                ErrorParams = errorCode is null ? null : parameters ?? new Dictionary<string, object?>(),
            });
            if (state == JobState.Failed)
            {
                LogFailed(entry.Id, entry.Kind, errorCode ?? "");
            }
            else
            {
                LogFinished(entry.Id, entry.Kind, state);
            }
        }
        catch (Exception exception)
        {
            LogFinishFailed(exception, entry.Id, entry.Kind);
        }
    }

    private static bool CancelRequested(JobEntry entry)
    {
        lock (entry.Lock)
        {
            return entry.CancelRequested;
        }
    }

    private async Task<(long OwnerId, Job Job)> FindVisibleAsync(ClaimsPrincipal user, long jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        (long OwnerId, Job Job)? found = null;
        if (_board.Find(jobId) is { } entry)
        {
            found = (entry.UserId, entry.Snapshot());
        }
        else
        {
            await using var connection = await _connections.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<JobRow>(SelectColumns + " WHERE id = @jobId", new { jobId });
            if (row is not null)
            {
                found = (row.UserId, ToJob(row));
            }
        }
        // Someone else's job answers like a missing one, so job ids of other users cannot be probed.
        if (found is not { } visible || !JobBoard.MaySee(user, visible.OwnerId))
        {
            throw new PithyxException(ErrorCodes.JobsJobNotFound);
        }
        return visible;
    }

    private void PublishProgress(JobEntry entry)
    {
        var previous = Activity.Current;
        lock (entry.Lock)
        {
            Activity.Current = entry.Activity;
        }
        try
        {
            Publish(entry, entry.Snapshot());
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    private async Task PersistProgressAsync(JobEntry entry)
    {
        try
        {
            await entry.Writes.WaitAsync(CancellationToken.None);
            try
            {
                var job = entry.Snapshot();
                if (job.State != JobState.Running)
                {
                    return;
                }
                await using var connection = await _connections.OpenAsync(CancellationToken.None);
                await connection.ExecuteAsync(
                    """
                    UPDATE jobs SET items_done = @ItemsDone, items_total = @ItemsTotal, bytes_done = @BytesDone,
                        bytes_total = @BytesTotal, current_item = @CurrentItem
                    WHERE id = @Id AND state = 'running'
                    """,
                    new { job.Id, job.Progress.ItemsDone, job.Progress.ItemsTotal, job.Progress.BytesDone, job.Progress.BytesTotal, job.Progress.CurrentItem });
            }
            finally
            {
                entry.Writes.Release();
            }
        }
        catch (Exception exception)
        {
            // A busy database only delays the progress in the row; the next write or the final state catches up.
            LogProgressFailed(exception, entry.Id);
        }
    }

    /// <summary>
    /// Writes the final state to the row, and only then shows it in memory and to subscribers, so a job that reads as
    /// finished is finished in the database too. If the write fails, the job still ends in memory; its row stays
    /// unfinished and becomes interrupted with the next start.
    /// </summary>
    private async Task CompleteAsync(JobEntry entry, Job final)
    {
        try
        {
            await WriteStateAsync(entry, final);
        }
        finally
        {
            lock (entry.Lock)
            {
                entry.State = final.State;
                entry.FinishedAt = final.FinishedAt;
                entry.ErrorCode = final.ErrorCode;
                entry.ErrorParams = final.ErrorParams;
            }
            Publish(entry, entry.Snapshot());
        }
        await NotifyFinishedAsync(entry.UserId, entry.Id, entry.Kind, final.State, final.ErrorCode, final.ErrorParams);
    }

    private void Publish(JobEntry entry, Job job)
    {
        _hub.Publish(JobBoard.TopicOf(entry.Id), job);
        _hub.Publish(JobBoard.ActiveTopic, new OwnedJob(entry.UserId, job));
    }

    /// <summary>
    /// Tells the owner how a job ended. Never throws: a failed notification is logged, the job's own state is what
    /// counts.
    /// </summary>
    private async Task NotifyFinishedAsync(long userId, long jobId, string kind, JobState state, string? errorCode, IReadOnlyDictionary<string, object?>? errorParams)
    {
        var (code, tone) = state switch
        {
            JobState.Succeeded => (JobSucceededCode, NotificationTone.Success),
            JobState.Failed => (JobFailedCode, NotificationTone.Warning),
            JobState.Cancelled => (JobCancelledCode, NotificationTone.Info),
            _ => (JobInterruptedCode, NotificationTone.Warning),
        };
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = kind, ["jobId"] = jobId };
        if (errorCode is not null)
        {
            parameters["errorCode"] = errorCode;
            parameters["errorParams"] = errorParams ?? new Dictionary<string, object?>();
        }
        try
        {
            await _notifications.CreateAsync(userId, new NotificationDraft(code, parameters, tone), CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogNotifyFailed(exception, jobId, kind);
        }
    }

    private Task WriteStateAsync(JobEntry entry) => WriteStateAsync(entry, entry.Snapshot());

    private async Task WriteStateAsync(JobEntry entry, Job job)
    {
        await entry.Writes.WaitAsync(CancellationToken.None);
        try
        {
            await using var connection = await _connections.OpenAsync(CancellationToken.None);
            await connection.ExecuteAsync(
                """
                UPDATE jobs SET state = @State, result = @Result, error_code = @ErrorCode, error_params = @ErrorParams,
                    items_done = @ItemsDone, items_total = @ItemsTotal, bytes_done = @BytesDone, bytes_total = @BytesTotal,
                    current_item = @CurrentItem, started_at = @StartedAt, finished_at = @FinishedAt
                WHERE id = @Id
                """,
                new
                {
                    job.Id,
                    State = StateText(job.State),
                    Result = job.Result?.GetRawText(),
                    job.ErrorCode,
                    ErrorParams = job.ErrorParams is null ? null : JsonSerializer.Serialize(job.ErrorParams, _storage),
                    job.Progress.ItemsDone,
                    job.Progress.ItemsTotal,
                    job.Progress.BytesDone,
                    job.Progress.BytesTotal,
                    job.Progress.CurrentItem,
                    StartedAt = job.StartedAt is { } started ? SessionService.Format(started) : null,
                    FinishedAt = job.FinishedAt is { } finished ? SessionService.Format(finished) : null,
                });
        }
        finally
        {
            entry.Writes.Release();
        }
    }

    private static Job ToJob(JobRow row) => new(
        row.Id,
        row.Kind,
        ParseState(row.State),
        new JobProgress(row.ItemsDone, row.ItemsTotal, row.BytesDone, row.BytesTotal, row.CurrentItem),
        Element(row.Params),
        Element(row.Result),
        row.ErrorCode,
        row.ErrorParams is null ? null : ErrorParamsFrom(row.ErrorParams),
        row.RequestId,
        SessionService.Parse(row.CreatedAt),
        row.StartedAt is null ? null : SessionService.Parse(row.StartedAt),
        row.FinishedAt is null ? null : SessionService.Parse(row.FinishedAt));

    private static JsonElement? Element(string? json)
    {
        if (json is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Dictionary<string, object?> ErrorParamsFrom(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
    }

    private static string StateText(JobState state) => state switch
    {
        JobState.Queued => "queued",
        JobState.Running => "running",
        JobState.Succeeded => "succeeded",
        JobState.Failed => "failed",
        JobState.Cancelled => "cancelled",
        JobState.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static JobState ParseState(string text) => text switch
    {
        "queued" => JobState.Queued,
        "running" => JobState.Running,
        "succeeded" => JobState.Succeeded,
        "failed" => JobState.Failed,
        "cancelled" => JobState.Cancelled,
        "interrupted" => JobState.Interrupted,
        _ => throw new InvalidOperationException($"Unknown job state {text}."),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} {JobKind} queued for user {UserId}")]
    private partial void LogQueued(long jobId, string jobKind, long userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} {JobKind} started")]
    private partial void LogStarted(long jobId, string jobKind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} {JobKind} finished as {JobState}")]
    private partial void LogFinished(long jobId, string jobKind, JobState jobState);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} {JobKind} failed with {ErrorCode}")]
    private partial void LogFailed(long jobId, string jobKind, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancel requested for job {JobId} {JobKind}")]
    private partial void LogCancelRequested(long jobId, string jobKind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId} {JobKind} failed unexpectedly")]
    private partial void LogUnexpected(Exception exception, long jobId, string jobKind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving the final state of job {JobId} {JobKind} failed")]
    private partial void LogFinishFailed(Exception exception, long jobId, string jobKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saving the progress of job {JobId} failed")]
    private partial void LogProgressFailed(Exception exception, long jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifying the owner of job {JobId} {JobKind} failed")]
    private partial void LogNotifyFailed(Exception exception, long jobId, string jobKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marked {Count} jobs of an earlier daemon run as interrupted")]
    private partial void LogInterrupted(int count);

    // Properties instead of a constructor: Dapper cannot map a constructor when a nullable column is NULL in every row.
    private sealed class JobRow
    {
        public long Id { get; init; }
        public long UserId { get; init; }
        public string Kind { get; init; } = "";
        public string State { get; init; } = "";
        public string? Params { get; init; }
        public string? Result { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorParams { get; init; }
        public long ItemsDone { get; init; }
        public long ItemsTotal { get; init; }
        public long BytesDone { get; init; }
        public long BytesTotal { get; init; }
        public string? CurrentItem { get; init; }
        public string RequestId { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string? StartedAt { get; init; }
        public string? FinishedAt { get; init; }
    }
}
