namespace Pithyx.Daemon.Commands;

/// <summary>
/// The only way the daemon runs external tools. Implementations always use <c>LC_ALL=C.UTF-8</c>.
/// </summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a long-lived command (for example <c>udevadm monitor</c>) and yields its stdout line by line until it exits
    /// or the token is cancelled, which kills it. The request timeout does not apply.
    /// </summary>
    IAsyncEnumerable<string> StreamLinesAsync(CommandRequest request, CancellationToken cancellationToken);
}
