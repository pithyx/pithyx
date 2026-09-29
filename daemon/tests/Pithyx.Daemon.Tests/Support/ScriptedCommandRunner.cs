using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Tests.Support;

/// <summary>Answers commands with output the test sets, and streams lines the test pushes, per program.</summary>
public sealed class ScriptedCommandRunner : ICommandRunner
{
    private readonly ConcurrentDictionary<string, Channel<string>> _streams = new(StringComparer.Ordinal);

    /// <summary>Results by program name, or by program and first argument (<c>"zpool status"</c>), which wins.</summary>
    public Dictionary<string, CommandResult> Results { get; } = [];

    public List<CommandRequest> Calls { get; } = [];

    /// <summary>Answers a command before <see cref="Results"/> are looked at, for example per device; null passes it on.</summary>
    public Func<CommandRequest, CommandResult?>? Handler { get; set; }

    /// <summary>Streams a line to the running <c>udevadm monitor</c>, or to another streaming program.</summary>
    public void Push(string line, string program = "udevadm") => Stream(program).Writer.TryWrite(line);

    public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(request);
        }
        if (Handler?.Invoke(request) is { } handled)
        {
            return Task.FromResult(handled);
        }
        var specific = request.Arguments.Count > 0 ? $"{request.FileName} {request.Arguments[0]}" : request.FileName;
        return Task.FromResult(
            Results.TryGetValue(specific, out var exact) ? exact
            : Results.TryGetValue(request.FileName, out var result) ? result
            : new CommandResult(127, "", "not scripted", TimeSpan.Zero));
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(request);
        }
        await foreach (var line in Stream(request.FileName).Reader.ReadAllAsync(cancellationToken))
        {
            yield return line;
        }
    }

    private Channel<string> Stream(string program) => _streams.GetOrAdd(program, _ => Channel.CreateUnbounded<string>());
}
