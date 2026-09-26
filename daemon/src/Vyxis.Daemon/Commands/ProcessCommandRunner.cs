using System.Diagnostics;

namespace Vyxis.Daemon.Commands;

public sealed partial class ProcessCommandRunner(ILogger<ProcessCommandRunner> logger) : ICommandRunner
{
    private const int MaxLoggedOutputLength = 4096;
    private static readonly TimeSpan _defaultTimeout = TimeSpan.FromMinutes(1);

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["LC_ALL"] = "C.UTF-8";

        var timeout = request.Timeout ?? _defaultTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            LogCommandAborted(request.FileName, request.Arguments, stopwatch.Elapsed);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{request.FileName} did not finish within {timeout}.");
        }

        var result = new CommandResult(process.ExitCode, await standardOutput, await standardError, stopwatch.Elapsed);
        if (logger.IsEnabled(LogLevel.Information))
        {
            var loggedOutput = Truncate(result.StandardOutput);
            var loggedError = Truncate(result.StandardError);
            LogCommandFinished(request.FileName, request.Arguments, result.ExitCode, result.Duration.TotalMilliseconds, loggedOutput, loggedError);
        }
        return result;
    }

    private static string Truncate(string output) =>
        output.Length <= MaxLoggedOutputLength ? output : output[..MaxLoggedOutputLength] + "...";

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Command {Command} {Arguments} exited with {ExitCode} after {DurationMs} ms. stdout: {StandardOutput} stderr: {StandardError}")]
    private partial void LogCommandFinished(
        string command,
        IReadOnlyList<string> arguments,
        int exitCode,
        double durationMs,
        string standardOutput,
        string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {Command} {Arguments} was aborted after {Duration}")]
    private partial void LogCommandAborted(string command, IReadOnlyList<string> arguments, TimeSpan duration);
}
