namespace Pithyx.Daemon.Commands;

/// <param name="FileName">Program to run, resolved through PATH.</param>
/// <param name="Arguments">Arguments passed without a shell.</param>
/// <param name="Timeout">Kill the command after this time. Defaults to one minute.</param>
/// <param name="SensitiveArgumentIndexes">Positions in <paramref name="Arguments"/> that are masked in logs.</param>
/// <param name="StandardInput">Written to the command's stdin, which is then closed. Never logged: it carries passwords.</param>
/// <param name="SecretOutput">The command prints secrets: its stdout and stderr are masked in logs.</param>
public sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan? Timeout = null,
    IReadOnlySet<int>? SensitiveArgumentIndexes = null,
    string? StandardInput = null,
    bool SecretOutput = false)
{
    private const int MaxLoggedOutputLength = 4096;

    /// <summary>
    /// Programs whose output is masked whatever the caller says: <c>pdbedit -L -w</c> prints every NT hash, and the
    /// password tools echo what they were given on failure.
    /// </summary>
    private static readonly HashSet<string> _secretOutputPrograms = new(StringComparer.Ordinal) { "pdbedit", "smbpasswd", "chpasswd" };

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

    /// <summary>Whether the runner masks this command's stdout and stderr in logs.</summary>
    public bool HidesOutput => SecretOutput || _secretOutputPrograms.Contains(Path.GetFileName(FileName));

    /// <summary>The command's stdout or stderr as it may appear in logs: masked or truncated.</summary>
    public string LoggableOutput(string output) =>
        HidesOutput && output.Length > 0 ? Hosting.SecretMaskingEnricher.Mask
        : output.Length <= MaxLoggedOutputLength ? output
        : output[..MaxLoggedOutputLength] + "...";
}
