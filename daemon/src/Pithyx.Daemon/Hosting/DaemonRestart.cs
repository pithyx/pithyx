namespace Pithyx.Daemon.Hosting;

/// <summary>
/// Restarts the daemon a few seconds after the answer went out: it stops and exits with
/// <see cref="DaemonExitCode.RestartRequested"/>, and systemd starts it again (<c>Restart=on-failure</c>). The new process
/// reads everything from SQLite and reconciles the system with it. Simulation only remembers the request.
/// </summary>
public sealed class DaemonRestart(IHostApplicationLifetime lifetime, DaemonMode mode, TimeProvider time)
{
    /// <summary>A restart was asked for.</summary>
    public bool Requested { get; private set; }

    /// <summary>The daemon stops for a restart; the process exits with <see cref="DaemonExitCode.RestartRequested"/>.</summary>
    public bool Restarting { get; private set; }

    public void Request()
    {
        Requested = true;
        if (mode != DaemonMode.Real)
        {
            return;
        }
        Restarting = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), time);
            lifetime.StopApplication();
        });
    }
}
