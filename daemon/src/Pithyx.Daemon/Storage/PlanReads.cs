using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Storage;

/// <summary>
/// Reads of the ZFS state that a plan request waits for. ZFS answers them in well under a second, but a pool waiting for
/// missing disks can block every <c>zpool</c> and <c>zfs</c> command for good. A plan waits at most <see cref="Limit"/>,
/// below the SDK's request timeout, and then answers <c>storage.zfs.unavailable</c> instead of letting the call time out.
/// </summary>
public static class PlanReads
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Runs the read and waits for it at most <see cref="Limit"/>; the read itself goes on in the background.</summary>
    public static async Task WithinLimitAsync(Func<CancellationToken, Task> read, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            await read(cancellationToken).WaitAsync(Limit, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new PithyxException(ErrorCodes.StorageZfsUnavailable);
        }
    }
}
