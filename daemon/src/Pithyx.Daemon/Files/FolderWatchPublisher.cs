using System.Globalization;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Files;

/// <summary>
/// Publishes folder changes on <c>files/&lt;share&gt;/&lt;folder&gt;</c> and stops watching a folder as soon as its last
/// subscriber is gone.
/// </summary>
public sealed class FolderWatchPublisher(TopicHub hub, FolderWatches watches) : BackgroundService
{
    public const string Pattern = "files/+/+";

    public static TopicDefinition Definition(FolderWatches watches, ShareService shares)
    {
        ArgumentNullException.ThrowIfNull(watches);
        ArgumentNullException.ThrowIfNull(shares);
        return new TopicDefinition(
            Pattern,
            typeof(FolderWatchState),
            typeof(FolderChanges),
            (topic, _) => ValueTask.FromResult<object>(new FolderWatchState(watches.Ensure(topic))),
            (user, topic) => ShareOf(topic) is { } share && shares.CanRead(user, share),
            CurrentTopics: () => watches.Topics);
    }

    private static long? ShareOf(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length == 3 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var share) ? share : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Release() => watches.Release(hub.HasSubscribers);
        hub.SubscribersRemoved += Release;
        try
        {
            await foreach (var change in watches.Events.ReadAllAsync(stoppingToken))
            {
                hub.Publish(change.Topic, change.Changes);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            hub.SubscribersRemoved -= Release;
        }
    }
}
