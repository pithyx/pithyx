using System.Threading.Channels;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Network;

/// <summary>
/// Publishes <c>system/network</c> when links, addresses or routes change, from the kernel's netlink events
/// (<c>ip -o monitor</c>), and only while an administrator watches the topic. A burst of events (DHCP brings an
/// address, routes and name servers at once) is published once.
/// </summary>
public sealed partial class NetworkMonitor(TopicHub hub, ICommandRunner commands, NetworkService network, TimeProvider time, ILogger<NetworkMonitor> logger) : BackgroundService
{
    private static readonly CommandRequest _monitor = new("ip", ["-o", "monitor", "link", "address", "route"]);
    private static readonly TimeSpan _settle = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await hub.WaitForSubscribersAsync(NetworkService.Topic, stoppingToken);
                using var watching = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                void Removed()
                {
                    if (!hub.HasSubscribers(NetworkService.Topic))
                    {
                        watching.Cancel();
                    }
                }
                hub.SubscribersRemoved += Removed;
                try
                {
                    await WatchAsync(watching.Token);
                    // ip ended on its own; do not start it again at once.
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
                }
                finally
                {
                    hub.SubscribersRemoved -= Removed;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The last subscriber left.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                LogMonitorFailed(exception);
                await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
            }
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var publisher = Task.Run(async () =>
        {
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken))
            {
                await Task.Delay(_settle, time, cancellationToken);
                changes.Reader.TryRead(out _);
                await network.PublishAsync(cancellationToken);
            }
        }, cancellationToken);
        try
        {
            await foreach (var _ in commands.StreamLinesAsync(_monitor, cancellationToken))
            {
                changes.Writer.TryWrite(true);
            }
        }
        finally
        {
            changes.Writer.TryComplete();
            try
            {
                await publisher;
            }
            catch (OperationCanceledException)
            {
                // Stopped with the stream.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watching the network for changes failed; trying again")]
    private partial void LogMonitorFailed(Exception exception);
}
