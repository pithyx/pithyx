using System.Text.Json;

namespace Pithyx.Daemon.Errors;

/// <summary>
/// RFC 9457 problem details as returned by every failing API call. The detail text is an English fallback;
/// clients show the translation of the error code instead.
/// </summary>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Code,
    IReadOnlyDictionary<string, JsonElement> Params,
    string RequestId);
