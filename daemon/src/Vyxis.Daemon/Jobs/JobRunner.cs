using Vyxis.Daemon.Hosting;

namespace Vyxis.Daemon.Jobs;

/// <summary>
/// Runs queued jobs in the order they were queued, at most <see cref="MaxConcurrentJobs"/> at once. Before it starts,
/// jobs an earlier daemon process left queued or running become interrupted; they are never resumed.
/// </summary>
public sealed class JobRunner(JobService jobs) : BackgroundService
{
    public const int MaxConcurrentJobs = 2;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Hosted services start before the server accepts requests, so no new job can be caught by this.
        if (!BuildTimeDocument.IsGenerating)
        {
            await jobs.InterruptUnfinishedAsync(cancellationToken);
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(MaxConcurrentJobs, MaxConcurrentJobs);
        var running = new List<Task>();
        try
        {
            while (await jobs.Queue.WaitToReadAsync(stoppingToken))
            {
                // A slot first, then the next job: the queue order is the start order.
                await slots.WaitAsync(stoppingToken);
                if (!jobs.Queue.TryRead(out var entry))
                {
                    slots.Release();
                    continue;
                }
                running.RemoveAll(task => task.IsCompleted);
                running.Add(Task.Run(
                    async () =>
                    {
                        try
                        {
                            await jobs.RunAsync(entry, stoppingToken);
                        }
                        finally
                        {
                            slots.Release();
                        }
                    },
                    CancellationToken.None));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Running jobs see the same token and end as interrupted.
        }
        await Task.WhenAll(running);
    }
}
