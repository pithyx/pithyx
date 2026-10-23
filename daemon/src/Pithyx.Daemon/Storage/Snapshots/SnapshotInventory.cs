using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Storage.Snapshots;

/// <summary>Snapshot and event of <c>storage/snapshots</c>.</summary>
/// <param name="Revision">Changes whenever a snapshot came, went or changed; compare it, do not count on it.</param>
/// <param name="Datasets">The datasets whose snapshots changed; empty in the snapshot of the topic.</param>
public sealed record SnapshotChanges(long Revision, IReadOnlyList<string> Datasets);

/// <summary>
/// Every snapshot as last read by <see cref="SnapshotService"/>. Reads never run a command. A dependency-free state
/// holder (the topic registry depends on it); <see cref="SnapshotChangePublisher"/> publishes its changes.
/// </summary>
public sealed class SnapshotInventory
{
    public const string Topic = "storage/snapshots";

    private IReadOnlyList<Snapshot> _current = [];

    // A random start, so a restarted daemon does not repeat a revision a client saw before.
    private long _revision = Random.Shared.Next(1, int.MaxValue);

    public IReadOnlyList<Snapshot> Current => Volatile.Read(ref _current);

    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>False until the first successful read.</summary>
    public bool Available { get; private set; }

    /// <summary>Raised after <see cref="Replace"/> changed the snapshots of at least one dataset.</summary>
    public event Action<SnapshotChanges>? Changed;

    public void Replace(IReadOnlyList<Snapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var previous = Interlocked.Exchange(ref _current, snapshots);
        Available = true;
        var changed = ChangedDatasets(previous, snapshots);
        if (changed.Count > 0)
        {
            Changed?.Invoke(new SnapshotChanges(Interlocked.Increment(ref _revision), changed));
        }
    }

    /// <summary>The snapshots of one dataset, oldest first (by transaction group).</summary>
    public IReadOnlyList<Snapshot> Of(string dataset) =>
        [.. Current.Where(snapshot => snapshot.Dataset == dataset).OrderBy(snapshot => snapshot.CreateTxg).ThenBy(snapshot => snapshot.Name, StringComparer.Ordinal)];

    public Snapshot? Find(string dataset, string name) =>
        Current.FirstOrDefault(snapshot => snapshot.Dataset == dataset && snapshot.Name == name);

    /// <summary>Administrators only, as every snapshot endpoint: the names list every dataset on the box.</summary>
    public TopicDefinition Definition() => new(
        Topic,
        typeof(SnapshotChanges),
        typeof(SnapshotChanges),
        (_, _) => ValueTask.FromResult<object>(new SnapshotChanges(Revision, [])),
        PoolInventory.IsAllowed);

    /// <summary>The datasets, in name order, whose set of snapshots differs between the two reads.</summary>
    internal static IReadOnlyList<string> ChangedDatasets(IReadOnlyList<Snapshot> before, IReadOnlyList<Snapshot> after)
    {
        var old = before.ToHashSet();
        var now = after.ToHashSet();
        return [.. old.Except(now).Concat(now.Except(old)).Select(snapshot => snapshot.Dataset).Distinct().Order(StringComparer.Ordinal)];
    }
}

/// <summary>Publishes every change of the <see cref="SnapshotInventory"/> on <c>storage/snapshots</c>.</summary>
public sealed class SnapshotChangePublisher(TopicHub hub, SnapshotInventory inventory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        inventory.Changed += Publish;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        inventory.Changed -= Publish;
        return Task.CompletedTask;
    }

    private void Publish(SnapshotChanges changes) => hub.Publish(SnapshotInventory.Topic, changes);
}
