namespace Pithyx.Daemon.Commands;

/// <summary>The log line of every finished command, shared by the real and the simulated runner so both mask alike.</summary>
internal static partial class CommandLog
{
    public static void Finished(ILogger logger, CommandRequest request, CommandResult result)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            var loggedOutput = request.LoggableOutput(result.StandardOutput);
            var loggedError = request.LoggableOutput(result.StandardError);
            LogCommandFinished(
                logger,
                request.FileName,
                request.LoggableArguments,
                result.ExitCode,
                result.Duration.TotalMilliseconds,
                loggedOutput,
                loggedError);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Command {Command} {Arguments} exited with {ExitCode} after {DurationMs} ms. stdout: {StandardOutput} stderr: {StandardError}")]
    private static partial void LogCommandFinished(
        ILogger logger, string command, IReadOnlyList<string> arguments, int exitCode, double durationMs, string standardOutput, string standardError);
}
