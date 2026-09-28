namespace Pithyx.Daemon;

/// <summary>
/// Exit codes that tell systemd and the dev scripts why the daemon did not start. Codes a restart cannot fix are
/// listed in the unit's <c>RestartPreventExitStatus</c>.
/// </summary>
public static class DaemonExitCode
{
    /// <summary>The database could not be opened or a migration failed.</summary>
    public const int DatabaseFailed = 1;

    /// <summary>A newer daemon wrote the database (EX_DATAERR). Only another daemon build or database fixes it.</summary>
    public const int DatabaseNewerThanDaemon = 65;
}
