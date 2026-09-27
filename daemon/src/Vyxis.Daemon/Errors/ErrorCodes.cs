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
    /// <summary>The app lacks a permission the call needs, or an administrator revoked it; <c>params.permission</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AppPermissionDenied = "app.permission.denied";

    /// <summary>A value an app stores is larger than its limit; <c>params.maxKb</c> is the limit.</summary>
    [ErrorStatus(HttpStatusCode.RequestEntityTooLarge)]
    public const string AppStorageTooLarge = "app.storage.too_large";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthChallengeExpired = "auth.challenge.expired";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthCredentialsInvalid = "auth.credentials.invalid";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AuthCsrfHeaderMissing = "auth.csrf.header_missing";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthDisplayNameInvalid = "auth.display_name.invalid";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthPasswordTooCommon = "auth.password.too_common";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthPasswordTooLong = "auth.password.too_long";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthPasswordTooShort = "auth.password.too_short";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AuthPermissionDenied = "auth.permission.denied";

    [ErrorStatus(HttpStatusCode.TooManyRequests)]
    public const string AuthRateLimited = "auth.rate.limited";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthRecoveryInvalid = "auth.recovery.invalid";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthSessionExpired = "auth.session.expired";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AuthSetupAlreadyDone = "auth.setup.already_done";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AuthTotpAlreadyEnabled = "auth.totp.already_enabled";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthTotpInvalid = "auth.totp.invalid";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AuthTotpNotEnrolled = "auth.totp.not_enrolled";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthUsernameInvalid = "auth.username.invalid";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthUsernameReserved = "auth.username.reserved";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string DesktopDeviceNotFound = "desktop.device.not_found";

    /// <summary>A change of a desktop item named an older version (If-Match); <c>params.version</c> is the current one.</summary>
    [ErrorStatus(HttpStatusCode.PreconditionFailed)]
    public const string DesktopItemConflict = "desktop.item.conflict";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string DesktopItemInvalid = "desktop.item.invalid";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string DesktopItemLimitReached = "desktop.item.limit_reached";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string DesktopItemNotFound = "desktop.item.not_found";

    /// <summary>A layout save named an older version (If-Match); <c>params.version</c> is the current one.</summary>
    [ErrorStatus(HttpStatusCode.PreconditionFailed)]
    public const string DesktopLayoutConflict = "desktop.layout.conflict";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string DesktopLayoutInvalid = "desktop.layout.invalid";

    [ErrorStatus(HttpStatusCode.RequestEntityTooLarge)]
    public const string DesktopLayoutTooLarge = "desktop.layout.too_large";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string DesktopNoteTooLong = "desktop.note.too_long";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string RealtimeMessageInvalid = "realtime.message.invalid";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string RealtimeTopicInvalid = "realtime.topic.invalid";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string RealtimeTopicUnknown = "realtime.topic.unknown";

    [ErrorStatus(HttpStatusCode.UpgradeRequired)]
    public const string RealtimeUpgradeRequired = "realtime.upgrade.required";

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

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string SystemOriginForbidden = "system.origin.forbidden";

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
