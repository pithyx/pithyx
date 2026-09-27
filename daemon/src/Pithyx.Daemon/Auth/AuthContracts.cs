namespace Pithyx.Daemon.Auth;

public sealed record UserInfo(long Id, string Username, string? DisplayName, bool IsAdmin, bool TotpEnabled);

/// <param name="Required">True until the first administrator exists.</param>
/// <param name="Hostname">Shown on the setup and sign-in screens, so users with several servers know where they sign in.</param>
public sealed record SetupStatus(bool Required, string Hostname);

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

/// <param name="Codes">Shown exactly once. Each code replaces a TOTP code for one login.</param>
public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

public sealed record RecoverRequest(string Username, string RecoveryKey, string NewPassword);

/// <param name="RecoveryKey">The new recovery key. The old one no longer works.</param>
public sealed record RecoverResponse(string RecoveryKey);
