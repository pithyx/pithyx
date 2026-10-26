namespace Pithyx.Daemon.Auth;

/// <summary>The signed-in user.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Username">Login name.</param>
/// <param name="DisplayName">Name shown, if set.</param>
/// <param name="IsAdmin">Whether the user has the administrator right, its own or through a group.</param>
/// <param name="TotpEnabled">Whether the user signs in with a second factor.</param>
/// <param name="Permissions">What the user may do; the topic <c>session/permissions</c> sends changes.</param>
/// <param name="Groups">The user's groups by name, without the built-in group every user is in; apps get them in their context.</param>
public sealed record UserInfo(long Id, string Username, string? DisplayName, bool IsAdmin, bool TotpEnabled, Permissions.SessionPermissions Permissions, IReadOnlyList<GroupRef> Groups);

/// <summary>A group a user is in.</summary>
/// <param name="Id">Stable id (the group's Linux gid).</param>
/// <param name="Name">The group name.</param>
public sealed record GroupRef(long Id, string Name);

/// <param name="Required">True until the first administrator exists.</param>
/// <param name="Hostname">Shown on the setup and sign-in screens, so users with several servers know where they sign in.</param>
/// <param name="Language">The system language, for the sign-in page and everyone whose browser asks for a language Pithyx does not have.</param>
/// <param name="Region">The ISO 3166 country code whose formats every user sees, or null to follow each user's language.</param>
/// <param name="Installer">True on the live system of the ISO: the desktop shows the installer (<c>/api/installer</c>) instead of the sign-in.</param>
public sealed record SetupStatus(bool Required, string Hostname, string Language, string? Region, bool Installer);

public sealed record SetupRequest(string Username, string Password, string? DisplayName = null);

/// <param name="User">The administrator, already signed in.</param>
/// <param name="RecoveryKey">Shown exactly once. It is the only way to reset the administrator password.</param>
public sealed record SetupResponse(UserInfo User, string RecoveryKey);

public sealed record LoginRequest(string Username, string Password);

/// <param name="User">Set when the login is complete.</param>
/// <param name="TotpRequired">True when a TOTP or recovery code must follow.</param>
/// <param name="Challenge">Pass to <c>/api/auth/login/totp</c> within five minutes.</param>
public sealed record LoginResponse(UserInfo? User, bool TotpRequired, string? Challenge);

/// <param name="Challenge">From the login response.</param>
/// <param name="Code">Six-digit code from the authenticator app.</param>
/// <param name="RecoveryCode">One of the recovery codes shown when TOTP was enabled, instead of a code.</param>
public sealed record TotpLoginRequest(string Challenge, string? Code = null, string? RecoveryCode = null);

/// <param name="Secret">Base32 secret for manual entry.</param>
/// <param name="Uri">otpauth:// URI for the QR code.</param>
public sealed record TotpEnrollment(string Secret, string Uri);

public sealed record TotpConfirmRequest(string Code);

public sealed record TotpDisableRequest(string Password);

/// <param name="Password">The signed-in user's password.</param>
/// <param name="TotpCode">A current authenticator code, when two-factor sign-in is on.</param>
public sealed record StepUpRequest(string Password, string? TotpCode = null);

/// <param name="Until">Until when the session holds a step-up (UTC), or null.</param>
public sealed record StepUpState(DateTimeOffset? Until);

/// <param name="Codes">Shown exactly once. Each code replaces a TOTP code for one login.</param>
public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

public sealed record RecoverRequest(string Username, string RecoveryKey, string NewPassword);

/// <param name="RecoveryKey">The new recovery key. The old one no longer works.</param>
public sealed record RecoverResponse(string RecoveryKey);

/// <summary>A passkey of the signed-in user (D380).</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Name">The name the user gave it, such as the device it lives on.</param>
/// <param name="RpId">The name of the box it belongs to: it works only where the box is opened under this name.</param>
/// <param name="BackedUp">Whether the authenticator syncs it to other devices.</param>
/// <param name="CreatedAt">UTC.</param>
/// <param name="LastUsedAt">UTC, or null before its first sign-in.</param>
public sealed record PasskeyInfo(long Id, string Name, string RpId, bool BackedUp, string CreatedAt, string? LastUsedAt);

public sealed record PasskeyList(IReadOnlyList<PasskeyInfo> Items);

/// <param name="Ceremony">Pass back with the authenticator's answer within five minutes; it is used once.</param>
/// <param name="Options">The WebAuthn options as JSON (<c>PublicKeyCredentialCreationOptionsJSON</c> or <c>PublicKeyCredentialRequestOptionsJSON</c>).</param>
public sealed record PasskeyOptions(string Ceremony, System.Text.Json.JsonElement Options);

/// <param name="Password">The current password: a new passkey signs in without it.</param>
public sealed record PasskeyOptionsRequest(string Password);

/// <param name="Ceremony">From the options.</param>
/// <param name="Name">1 to 64 characters.</param>
/// <param name="Credential">The answer of <c>navigator.credentials.create</c> as JSON (<c>RegistrationResponseJSON</c>).</param>
public sealed record PasskeyCreateRequest(string Ceremony, string Name, System.Text.Json.JsonElement Credential);

/// <param name="Ceremony">From the sign-in options.</param>
/// <param name="Credential">The answer of <c>navigator.credentials.get</c> as JSON (<c>AuthenticationResponseJSON</c>).</param>
public sealed record PasskeySignInRequest(string Ceremony, System.Text.Json.JsonElement Credential);
