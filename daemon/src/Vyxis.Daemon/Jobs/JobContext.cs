namespace Vyxis.Daemon.Jobs;

/// <summary>What the work of a job gets from the runner.</summary>
public sealed class JobContext
{
    private readonly JobService _service;
    private readonly JobEntry _entry;

    internal JobContext(JobService service, JobEntry entry, CancellationToken cancellation)
    {
        _service = service;
        _entry = entry;
        Cancellation = cancellation;
    }

    public long JobId => _entry.Id;

    public long UserId => _entry.UserId;

    /// <summary>Cancelled when the user cancels the job or the daemon stops. Throw <see cref="OperationCanceledException"/> then.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>
    /// Replaces the job's progress. Cheap enough to call for every file: subscribers get it at most every 250 ms and
    /// the database at most once per second.
    /// </summary>
    public void Report(JobProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        JobService.Report(_entry, progress);
    }

    /// <summary>Sets the job's result, serialized to JSON right away. A later call replaces it.</summary>
    public void SetResult(object result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _service.SetResult(_entry, result);
    }
}
