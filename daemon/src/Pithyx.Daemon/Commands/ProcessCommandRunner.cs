using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Pithyx.Daemon.Commands;

public sealed partial class ProcessCommandRunner(ILogger<ProcessCommandRunner> logger) : ICommandRunner
{
    /// <summary>The timeout of a <see cref="CommandRequest"/> that sets none.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(1);

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PipeTo is not null)
        {
            return await RunPipeAsync(request, request.PipeTo, cancellationToken);
        }
        var startInfo = CreateStartInfo(request);

        var timeout = request.Timeout ?? DefaultTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        if (request.StandardInput is { } input)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), timeoutSource.Token);
        }
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
        CommandLog.Finished(logger, request, result);
        return result;
    }

    /// <summary>
    /// Runs <paramref name="producer"/> with its stdout copied into <paramref name="consumer"/>'s stdin. Each command is
    /// logged on its own; the result carries the consumer's output and the first failing exit code.
    /// </summary>
    private async Task<CommandResult> RunPipeAsync(CommandRequest producer, CommandRequest consumer, CancellationToken cancellationToken)
    {
        if (producer.StandardInput is not null || consumer.StandardInput is not null || consumer.PipeTo is not null)
        {
            throw new ArgumentException("A pipe has two commands without stdin of their own.", nameof(producer));
        }
        var timeout = producer.Timeout ?? DefaultTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var first = new Process { StartInfo = CreateStartInfo(producer) };
        using var second = new Process { StartInfo = CreateStartInfo(consumer) };
        var stopwatch = Stopwatch.StartNew();
        second.Start();
        try
        {
            first.Start();
        }
        catch
        {
            second.Kill(entireProcessTree: true);
            throw;
        }
        first.StandardInput.Close();
        var firstError = first.StandardError.ReadToEndAsync(CancellationToken.None);
        var secondOutput = second.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var secondError = second.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            try
            {
                await first.StandardOutput.BaseStream.CopyToAsync(second.StandardInput.BaseStream, timeoutSource.Token);
            }
            catch (IOException)
            {
                // The consumer exited early and broke the pipe; both exit codes tell why.
            }
            finally
            {
                second.StandardInput.Close();
            }
            await first.WaitForExitAsync(timeoutSource.Token);
            await second.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            first.Kill(entireProcessTree: true);
            second.Kill(entireProcessTree: true);
            LogCommandAborted(producer.FileName, producer.LoggableArguments, stopwatch.Elapsed);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{producer.FileName} did not finish within {timeout}.");
        }
        var produced = new CommandResult(first.ExitCode, "", await firstError, stopwatch.Elapsed);
        CommandLog.Finished(logger, producer, produced);
        var consumed = new CommandResult(second.ExitCode, await secondOutput, await secondError, stopwatch.Elapsed);
        CommandLog.Finished(logger, consumer, consumed);
        return consumed with
        {
            ExitCode = first.ExitCode != 0 ? first.ExitCode : second.ExitCode,
            StandardError = first.ExitCode != 0 ? produced.StandardError : consumed.StandardError,
        };
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
            var error = request.LoggableOutput(await standardError);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Streaming command {Command} {Arguments} started")]
    private partial void LogStreamStarted(string command, IReadOnlyList<string> arguments);

    [LoggerMessage(Level = LogLevel.Information, Message = "Streaming command {Command} {Arguments} ended with {ExitCode} after {DurationMs} ms. stderr: {StandardError}")]
    private partial void LogStreamEnded(string command, IReadOnlyList<string> arguments, int exitCode, double durationMs, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {Command} {Arguments} was aborted after {Duration}")]
    private partial void LogCommandAborted(string command, IReadOnlyList<string> arguments, TimeSpan duration);
}
