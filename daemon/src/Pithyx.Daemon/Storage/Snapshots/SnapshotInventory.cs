namespace Pithyx.Daemon.Storage.Snapshots;

/// <summary>Every snapshot as last read by <see cref="SnapshotService"/>. Reads never run a command.</summary>
public sealed class SnapshotInventory
{
    private IReadOnlyList<Snapshot> _current = [];

    public IReadOnlyList<Snapshot> Current => Volatile.Read(ref _current);

    /// <summary>False until the first successful read.</summary>
    public bool Available { get; private set; }

    public void Replace(IReadOnlyList<Snapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        Volatile.Write(ref _current, snapshots);
        Available = true;
    }

    /// <summary>The snapshots of one dataset, oldest first (by transaction group).</summary>
    public IReadOnlyList<Snapshot> Of(string dataset) =>
        [.. Current.Where(snapshot => snapshot.Dataset == dataset).OrderBy(snapshot => snapshot.CreateTxg).ThenBy(snapshot => snapshot.Name, StringComparer.Ordinal)];

    public Snapshot? Find(string dataset, string name) =>
        Current.FirstOrDefault(snapshot => snapshot.Dataset == dataset && snapshot.Name == name);
}
