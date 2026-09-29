using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>
/// Builds the scrub commands. A scrub only reads and repairs from redundancy; it touches no disk outside the pool.
/// <c>zpool scrub</c> returns once the scrub has started; its progress arrives through <c>zpool events</c>.
/// </summary>
public static class ZfsScrubPlans
{
    public static CommandRequest Start(string pool) => new("zpool", ["scrub", Checked(pool)]);

    /// <summary>Stops a running scrub; the next one starts from the beginning.</summary>
    public static CommandRequest Stop(string pool) => new("zpool", ["scrub", "-s", Checked(pool)]);

    /// <summary>Pauses a running scrub; <see cref="Start"/> resumes it where it stopped.</summary>
    public static CommandRequest Pause(string pool) => new("zpool", ["scrub", "-p", Checked(pool)]);

    // A pool name never starts with a dash, so it cannot be read as an option.
    private static string Checked(string pool)
    {
        ArgumentException.ThrowIfNullOrEmpty(pool);
        return pool.StartsWith('-') ? throw new ArgumentException("A pool name cannot start with a dash.", nameof(pool)) : pool;
    }
}
