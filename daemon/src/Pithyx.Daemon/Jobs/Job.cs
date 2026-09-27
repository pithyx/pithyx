using System.Text.Json;

namespace Pithyx.Daemon.Jobs;

/// <summary>Where a job is in its life. The last four are final and never change again.</summary>
public enum JobState
{
    /// <summary>Waiting for a free slot; jobs start in the order they were queued.</summary>
    Queued,

    Running,

    Succeeded,

    /// <summary>The work failed; <see cref="Job.ErrorCode"/> and <see cref="Job.ErrorParams"/> say why.</summary>
    Failed,

    /// <summary>Cancelled by the user, before it started or while it ran.</summary>
    Cancelled,

    /// <summary>The daemon stopped while the job was queued or running. Jobs are never resumed.</summary>
    Interrupted,
}

/// <summary>How far a job is. Totals are 0 while unknown.</summary>
/// <param name="ItemsDone">Items (for example files) finished so far.</param>
/// <param name="ItemsTotal">Items in the whole job.</param>
/// <param name="BytesDone">Bytes processed so far.</param>
/// <param name="BytesTotal">Bytes in the whole job.</param>
/// <param name="CurrentItem">What the job works on right now, for example a path.</param>
public sealed record JobProgress(long ItemsDone, long ItemsTotal, long BytesDone, long BytesTotal, string? CurrentItem)
{
    public static JobProgress None { get; } = new(0, 0, 0, 0, null);
}

/// <summary>A long operation of a user, such as copying files. Also the data of the <c>jobs/{id}</c> topics.</summary>
/// <param name="Id">Also names the job's topic, <c>jobs/{id}</c>.</param>
/// <param name="Kind">What the job does, chosen by the module that started it (for example <c>files.copy</c>).</param>
/// <param name="State">Where the job is in its life.</param>
/// <param name="Progress">Live in the topic; in REST answers of running jobs at most about a second old.</param>
/// <param name="Parameters">What the job was started with, as given by the module that started it.</param>
/// <param name="Result">What the job produced, if it set a result.</param>
/// <param name="ErrorCode">Error code of a failed job.</param>
/// <param name="ErrorParams">Parameters of the error message of a failed job.</param>
/// <param name="RequestId">Request id of the request that started the job; the job's logs carry it.</param>
/// <param name="CreatedAt">When the job was queued (UTC).</param>
/// <param name="StartedAt">When it started running (UTC).</param>
/// <param name="FinishedAt">When it reached a final state (UTC).</param>
public sealed record Job(
    long Id,
    string Kind,
    JobState State,
    JobProgress Progress,
    JsonElement? Parameters,
    JsonElement? Result,
    string? ErrorCode,
    IReadOnlyDictionary<string, object?>? ErrorParams,
    string RequestId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
