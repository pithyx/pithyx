namespace Vyxis.Daemon.Layouts;

/// <summary>Deletes expired desktop layouts once an hour.</summary>
public sealed partial class LayoutCleanupService(LayoutService layouts, TimeProvider time, ILogger<LayoutCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var deleted = await layouts.DeleteExpiredAsync(stoppingToken);
                if (deleted > 0)
                {
                    LogDeleted(logger, deleted);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired desktop layouts")]
    private static partial void LogDeleted(ILogger logger, int count);
}
