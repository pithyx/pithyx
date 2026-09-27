namespace Vyxis.Daemon.Jobs;

/// <summary>Deletes jobs that finished more than <see cref="JobService.Lifetime"/> ago, once an hour.</summary>
public sealed partial class JobCleanupService(JobService jobs, TimeProvider time, ILogger<JobCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var deleted = await jobs.DeleteExpiredAsync(stoppingToken);
                    if (deleted > 0)
                    {
                        LogDeleted(logger, deleted);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A busy or failing database must not stop the daemon; the next hour tries again.
                    LogFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} finished jobs")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deleting finished jobs failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
