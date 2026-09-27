namespace Pithyx.Daemon.Storage;

/// <summary>
/// A storage system the daemon manages pools with. ZFS is the first; mdadm and btrfs follow through the same interface
/// (D111). Reads return the whole state; changes arrive as a stream so nothing polls.
/// </summary>
public interface IStorageBackend
{
    /// <summary>Reads every pool and dataset.</summary>
    Task<PoolList> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Yields once per change the backend reports, until cancelled or the event source stops.</summary>
    IAsyncEnumerable<string> WatchAsync(CancellationToken cancellationToken);
}
