using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>One problem with an app package, as an error code of <see cref="ErrorCodes"/> with its params.</summary>
/// <param name="Code">An <c>app.*</c> code.</param>
/// <param name="Params">Strings and numbers only, the same as the CLI reports for the same problem.</param>
public sealed record ValidationIssue(string Code, IReadOnlyDictionary<string, object?> Params)
{
    public ValidationIssue(string code, params (string Name, object? Value)[] parameters)
        : this(code, parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value))
    {
    }

    public PithyxException ToException() => new(Code, Params);
}
