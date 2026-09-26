namespace Vyxis.Daemon.Commands;

/// <summary>
/// The only way the daemon runs external tools. Implementations always use <c>LC_ALL=C.UTF-8</c>.
/// </summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken);
}
