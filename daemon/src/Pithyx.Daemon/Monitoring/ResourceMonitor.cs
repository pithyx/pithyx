using System.Security.Claims;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Monitoring;

/// <summary>
/// Publishes <c>system/resources</c> and <c>system/processes</c> once per second, each only while an administrator
/// is subscribed to it. Without subscribers it waits for the next subscription: no timer, no file read. The samples
/// of <c>system/resources</c> go into the in-memory <see cref="ResourceRing"/> of the last hour.
/// </summary>
public sealed class ResourceMonitor(TopicHub hub, ResourceSampler resources, ProcessSampler processes, ResourceRing ring, TimeProvider time) : BackgroundService
{
    public const string ResourcesTopic = "system/resources";
    public const string ProcessesTopic = "system/processes";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Without the monitor itself: the topic hub, which the monitor needs, is built from the definitions.</summary>
    public static TopicDefinition ResourcesDefinition(ResourceSampler resources, ResourceRing ring)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(ring);
        return new(
            ResourcesTopic,
            typeof(ResourceSnapshot),
            typeof(ResourceSample),
            (_, _) => ValueTask.FromResult<object>(Snapshot(resources, ring)),
            AdministratorsOnly,
            MinInterval: Interval);
    }

    public static TopicDefinition ProcessesDefinition(ProcessSampler processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return new(
            ProcessesTopic,
            typeof(ProcessList),
            typeof(ProcessList),
            (_, _) => ValueTask.FromResult<object>(processes.Sample()),
            AdministratorsOnly,
            MinInterval: Interval);
    }

    public static bool AdministratorsOnly(ClaimsPrincipal user, string topic) =>
        user?.Identity?.IsAuthenticated == true && user.IsInRole(SessionAuthenticationHandler.AdminRole);

    public static ResourceSnapshot Snapshot(ResourceSampler resources, ResourceRing ring)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(ring);
        var (cpuCount, memoryTotal) = resources.Describe();
        return new ResourceSnapshot(cpuCount, memoryTotal, ring.History(), ring.Latest());
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            RunAsync(ResourcesTopic, resources.Reset, () => resources.Sample() is { } sample ? Keep(sample) : null, stoppingToken),
            RunAsync(ProcessesTopic, processes.Reset, processes.Sample, stoppingToken));

    private ResourceSample Keep(ResourceSample sample)
    {
        ring.Add(sample);
        return sample;
    }

    /// <summary>Samples once when someone subscribes, then every second until the last subscriber is gone.</summary>
    private async Task RunAsync(string topic, Action reset, Func<object?> sample, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await hub.WaitForSubscribersAsync(topic, stoppingToken);
            reset();
            sample();
            using var timer = new PeriodicTimer(Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken) && hub.HasSubscribers(topic))
            {
                if (sample() is { } data)
                {
                    hub.Publish(topic, data);
                }
            }
        }
    }
}
