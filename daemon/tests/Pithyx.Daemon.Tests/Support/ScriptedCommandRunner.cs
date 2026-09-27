using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Tests.Support;

/// <summary>Answers commands with output the test sets, and streams lines the test pushes.</summary>
public sealed class ScriptedCommandRunner : ICommandRunner
{
    private readonly Channel<string> _streamLines = Channel.CreateUnbounded<string>();

    public Dictionary<string, CommandResult> Results { get; } = [];

    public List<CommandRequest> Calls { get; } = [];

    public void Push(string line) => _streamLines.Writer.TryWrite(line);

    public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(request);
        }
        return Task.FromResult(Results.TryGetValue(request.FileName, out var result) ? result : new CommandResult(127, "", "not scripted", TimeSpan.Zero));
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in _streamLines.Reader.ReadAllAsync(cancellationToken))
        {
            yield return line;
        }
    }
}
