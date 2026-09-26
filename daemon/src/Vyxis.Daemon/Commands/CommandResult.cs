namespace Vyxis.Daemon.Commands;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);
