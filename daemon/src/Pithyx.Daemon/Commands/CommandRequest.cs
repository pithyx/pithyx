namespace Pithyx.Daemon.Commands;

/// <param name="FileName">Program to run, resolved through PATH.</param>
/// <param name="Arguments">Arguments passed without a shell.</param>
/// <param name="Timeout">Kill the command after this time. Defaults to one minute.</param>
/// <param name="SensitiveArgumentIndexes">Positions in <paramref name="Arguments"/> that are masked in logs.</param>
public sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan? Timeout = null,
    IReadOnlySet<int>? SensitiveArgumentIndexes = null)
{
    /// <summary>The arguments as they may appear in logs.</summary>
    public IReadOnlyList<string> LoggableArguments =>
        SensitiveArgumentIndexes is not { Count: > 0 } sensitive
            ? Arguments
            : [.. Arguments.Select((argument, index) => sensitive.Contains(index) ? Hosting.SecretMaskingEnricher.Mask : argument)];
}
