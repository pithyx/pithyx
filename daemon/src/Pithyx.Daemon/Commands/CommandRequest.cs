namespace Pithyx.Daemon.Commands;

/// <param name="FileName">Program to run, resolved through PATH.</param>
/// <param name="Arguments">Arguments passed without a shell.</param>
/// <param name="Timeout">Kill the command after this time. Defaults to one minute.</param>
/// <param name="SensitiveArgumentIndexes">Positions in <paramref name="Arguments"/> that are masked in logs.</param>
/// <param name="StandardInput">Written to the command's stdin, which is then closed. Never logged: it carries passwords.</param>
public sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan? Timeout = null,
    IReadOnlySet<int>? SensitiveArgumentIndexes = null,
    string? StandardInput = null)
{
    /// <summary>Records print every member; stdin may hold a password.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"FileName = {FileName}, Arguments = [{string.Join(' ', LoggableArguments)}]");
        return true;
    }

    /// <summary>The arguments as they may appear in logs.</summary>
    public IReadOnlyList<string> LoggableArguments =>
        SensitiveArgumentIndexes is not { Count: > 0 } sensitive
            ? Arguments
            : [.. Arguments.Select((argument, index) => sensitive.Contains(index) ? Hosting.SecretMaskingEnricher.Mask : argument)];
}
