using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Shares;

/// <summary>Snapshot and event of <c>shares/list</c>.</summary>
/// <param name="Revision">Changes whenever a user's list of usable shares may have changed; compare it, do not count on it.</param>
public sealed record ShareListRevision(long Revision);

/// <summary>
/// The revision of the share list for the <c>shares/list</c> topic, which tells every signed-in user that their list
/// of usable shares may have changed (created, renamed, described, access changed, deleted), so the Files app reads
/// it again. The topic carries nothing but the revision: a user never learns a name or id of a share they cannot use.
/// A dependency-free state holder (the topic registry depends on it); <see cref="ShareChangePublisher"/> publishes.
/// </summary>
public sealed class ShareChanges
{
    public const string Topic = "shares/list";

    // A random start, so a restarted daemon does not repeat a revision a client saw before.
    private long _revision = Random.Shared.Next(1, int.MaxValue);

    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>Raised after <see cref="Announce"/> with the new revision.</summary>
    public event Action<long>? Changed;

    public void Announce()
    {
        var revision = Interlocked.Increment(ref _revision);
        Changed?.Invoke(revision);
    }

    public TopicDefinition Definition() => new(
        Topic,
        typeof(ShareListRevision),
        typeof(ShareListRevision),
        (_, _) => ValueTask.FromResult<object>(new ShareListRevision(Revision)),
        TopicDefinition.AnyUser);
}

/// <summary>
/// Publishes every <see cref="ShareChanges.Announce"/> on <c>shares/list</c>.
/// Also announces a new revision when a share dataset's availability changed (its pool went or came back, or it was
/// mounted or unmounted), so Files shows the change and reads a share again once it is back.
/// </summary>
public sealed class ShareChangePublisher(TopicHub hub, ShareChanges changes, PoolInventory pools) : IHostedService
{
    private string _availability = "";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _availability = ShareAvailability.Fingerprint(pools.Current);
        changes.Changed += Publish;
        pools.Changed += OnPoolsChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        changes.Changed -= Publish;
        pools.Changed -= OnPoolsChanged;
        return Task.CompletedTask;
    }

    private void OnPoolsChanged()
    {
        var availability = ShareAvailability.Fingerprint(pools.Current);
        if (Interlocked.Exchange(ref _availability, availability) != availability)
        {
            changes.Announce();
        }
    }

    private void Publish(long revision) => hub.Publish(ShareChanges.Topic, new ShareListRevision(revision));
}
