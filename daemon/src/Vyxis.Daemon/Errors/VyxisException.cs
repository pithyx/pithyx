using System.Diagnostics.CodeAnalysis;

namespace Vyxis.Daemon.Errors;

/// <summary>An expected failure that is returned to the client as problem details with a stable error code.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Every instance must carry an error code.")]
public sealed class VyxisException : Exception
{
    public VyxisException(string code, IReadOnlyDictionary<string, object?>? parameters = null, Exception? innerException = null)
        : base(code, innerException)
    {
        ErrorCodes.StatusOf(code);
        Code = code;
        Parameters = parameters ?? new Dictionary<string, object?>();
    }

    public string Code { get; }

    public IReadOnlyDictionary<string, object?> Parameters { get; }
}
