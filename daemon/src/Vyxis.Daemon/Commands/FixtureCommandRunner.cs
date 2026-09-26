using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vyxis.Daemon.Commands;

/// <summary>
/// Simulation mode: answers commands with output recorded by <c>tools/fixtures/record.sh</c>.
/// A scenario is a folder of <c>&lt;id&gt;.json</c> files with <c>&lt;id&gt;.stdout</c> and an optional <c>&lt;id&gt;.stderr</c>.
/// Long-lived commands have a <c>&lt;id&gt;.stream</c> file instead of stdout: its lines are replayed, then the stream stays
/// open without further output, like an event monitor on a quiet system.
/// </summary>
public sealed class FixtureCommandRunner : ICommandRunner
{
    private readonly Dictionary<string, CommandResult> _results;

    private FixtureCommandRunner(Dictionary<string, CommandResult> results) => _results = results;

    public static FixtureCommandRunner Load(string scenarioDirectory)
    {
        if (!Directory.Exists(scenarioDirectory))
        {
            throw new DirectoryNotFoundException($"Simulation fixtures not found at {scenarioDirectory}. Set VYXIS_FIXTURES.");
        }

        var results = new Dictionary<string, CommandResult>(StringComparer.Ordinal);
        foreach (var definitionPath in Directory.EnumerateFiles(scenarioDirectory, "*.json"))
        {
            var definition = JsonSerializer.Deserialize<FixtureDefinition>(File.ReadAllText(definitionPath), JsonSerializerOptions.Web)
                ?? throw new InvalidDataException($"Empty fixture {definitionPath}.");
            var basePath = Path.ChangeExtension(definitionPath, null);
            var standardError = File.Exists(basePath + ".stderr") ? File.ReadAllText(basePath + ".stderr") : "";
            var output = File.Exists(basePath + ".stream") ? File.ReadAllText(basePath + ".stream") : File.ReadAllText(basePath + ".stdout");
            var result = new CommandResult(definition.ExitCode, output, standardError, TimeSpan.Zero);
            if (!results.TryAdd(Key(definition.Command, definition.Args), result))
            {
                throw new InvalidDataException($"Fixture {definitionPath} duplicates another fixture for the same command.");
            }
        }
        return new FixtureCommandRunner(results);
    }

    public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (_results.TryGetValue(Key(request.FileName, request.Arguments), out var result))
        {
            return Task.FromResult(result);
        }
        throw new InvalidOperationException(
            $"No simulation fixture for: {request.FileName} {string.Join(' ', request.Arguments)}. Record one with tools/fixtures/record.sh.");
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await RunAsync(request, cancellationToken);
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return line;
        }
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The consumer stopped listening.
        }
    }

    private static string Key(string command, IEnumerable<string> arguments) =>
        string.Join('\0', arguments.Prepend(command));

    private sealed record FixtureDefinition(string Command, IReadOnlyList<string> Args, int ExitCode);
}
