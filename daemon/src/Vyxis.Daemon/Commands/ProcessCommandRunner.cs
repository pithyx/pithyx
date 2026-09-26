using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Vyxis.Daemon.Commands;

public sealed partial class ProcessCommandRunner(ILogger<ProcessCommandRunner> logger) : ICommandRunner
{
    private const int MaxLoggedOutputLength = 4096;
    private static readonly TimeSpan _defaultTimeout = TimeSpan.FromMinutes(1);

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = CreateStartInfo(request);

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
            LogCommandAborted(request.FileName, request.LoggableArguments, stopwatch.Elapsed);
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
            LogCommandFinished(request.FileName, request.LoggableArguments, result.ExitCode, result.Duration.TotalMilliseconds, loggedOutput, loggedError);
        }
        return result;
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var process = new Process { StartInfo = CreateStartInfo(request) };
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.StandardInput.Close();
        // stderr is drained so the process never blocks on a full pipe; it is logged when the stream ends.
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        LogStreamStarted(request.FileName, request.LoggableArguments);
        try
        {
            while (await ReadLineOrNullAsync(process.StandardOutput, cancellationToken) is { } line)
            {
                yield return line;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            var error = Truncate(await standardError);
            LogStreamEnded(request.FileName, request.LoggableArguments, process.ExitCode, stopwatch.Elapsed.TotalMilliseconds, error);
        }
    }

    private static async Task<string?> ReadLineOrNullAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static ProcessStartInfo CreateStartInfo(CommandRequest request)
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
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity)
        {
            startInfo.Environment["TRACEPARENT"] = activity.Id;
        }
        return startInfo;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Streaming command {Command} {Arguments} started")]
    private partial void LogStreamStarted(string command, IReadOnlyList<string> arguments);

    [LoggerMessage(Level = LogLevel.Information, Message = "Streaming command {Command} {Arguments} ended with {ExitCode} after {DurationMs} ms. stderr: {StandardError}")]
    private partial void LogStreamEnded(string command, IReadOnlyList<string> arguments, int exitCode, double durationMs, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {Command} {Arguments} was aborted after {Duration}")]
    private partial void LogCommandAborted(string command, IReadOnlyList<string> arguments, TimeSpan duration);
}
