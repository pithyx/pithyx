namespace Pithyx.Daemon.Notifications;

/// <summary>Deletes notifications older than <see cref="NotificationService.Lifetime"/>, once an hour.</summary>
public sealed partial class NotificationCleanupService(NotificationService notifications, TimeProvider time, ILogger<NotificationCleanupService> logger) : BackgroundService
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
                    var deleted = await notifications.DeleteExpiredAsync(stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired notifications")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deleting expired notifications failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
