using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Monitoring;

/// <summary>
/// Publishes <c>system/info</c> once per second, but only while someone is subscribed. Without subscribers it waits
/// for the next subscription instead of polling.
/// </summary>
public sealed class SystemInfoPublisher(TopicHub hub, SystemInfoSampler sampler, TimeProvider time) : BackgroundService
{
    public const string Topic = "system/info";
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(1);

    public static TopicDefinition Definition(SystemInfoSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        return new TopicDefinition(
            Topic,
            typeof(SystemInfo),
            typeof(SystemInfo),
            (_, _) => ValueTask.FromResult<object>(sampler.Sample()),
            TopicDefinition.AnyUser,
            MinInterval: _interval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await hub.WaitForSubscribersAsync(Topic, stoppingToken);
            using var timer = new PeriodicTimer(_interval, time);
            while (hub.HasSubscribers(Topic) && await timer.WaitForNextTickAsync(stoppingToken))
            {
                hub.Publish(Topic, sampler.Sample());
            }
        }
    }
}
