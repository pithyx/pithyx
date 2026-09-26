using System.Collections.Frozen;
using System.Net;
using System.Reflection;

namespace Vyxis.Daemon.Errors;

/// <summary>
/// Every error code the API can return. Codes are part of the stable API contract: never rename or remove one
/// without a major version. Each code needs an English text in <c>i18n/en/errors.json</c>.
/// </summary>
public static class ErrorCodes
{
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string RequestBodyInvalid = "request.body.invalid";

    [ErrorStatus(HttpStatusCode.UnsupportedMediaType)]
    public const string RequestMediaTypeUnsupported = "request.media_type.unsupported";

    [ErrorStatus(HttpStatusCode.MethodNotAllowed)]
    public const string RequestMethodNotAllowed = "request.method.not_allowed";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string RequestRouteNotFound = "request.route.not_found";

    [ErrorStatus(HttpStatusCode.InternalServerError)]
    public const string SystemInternalUnexpected = "system.internal.unexpected";

    /// <summary>All codes with their HTTP status, ordered by code.</summary>
    public static FrozenDictionary<string, HttpStatusCode> All { get; } = typeof(ErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral)
        .ToFrozenDictionary(
            field => (string)field.GetRawConstantValue()!,
            field => field.GetCustomAttribute<ErrorStatusAttribute>()?.Status
                ?? throw new InvalidOperationException($"{field.Name} has no [ErrorStatus]."));

    public static HttpStatusCode StatusOf(string code) =>
        All.TryGetValue(code, out var status) ? status : throw new ArgumentException($"Unknown error code {code}.", nameof(code));
}
