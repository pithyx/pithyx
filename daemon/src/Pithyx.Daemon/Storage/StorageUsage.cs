namespace Pithyx.Daemon.Storage;

/// <summary>
/// Tells <see cref="SystemStateService"/> that files were written or deleted on a pool, so it reads the used and free
/// space again. Writes raise no <c>zpool</c> event, so the file service is the event source (D209): an upload, a
/// finished file job and the recycle bin's cleanup call <see cref="Written"/>. Writes that bypass the file service
/// (SMB, SFTP) show with the next pool read.
/// </summary>
public sealed class StorageUsage
{
    /// <summary>Raised by <see cref="Written"/>.</summary>
    public event Action? Changed;

    public void Written() => Changed?.Invoke();
}
