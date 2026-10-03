using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Auth;

/// <summary>
/// Rules for user names, display names and passwords. Every user becomes a Linux and Samba user later, so user names
/// follow the portable Linux rules and are stored in lower case.
/// </summary>
public static partial class CredentialRules
{
    // The hard floor (NIST SP 800-63B). Anything longer is allowed; the desktop shows a strength estimate and warns
    // about weak passwords but never refuses them.
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 1024;
    public const int MaxDisplayNameLength = 64;

    // Accounts that exist on Debian or are created by packages Pithyx installs. The daemon additionally checks
    // /etc/passwd when it provisions Linux users (M3).
    private static readonly FrozenSet<string> _reserved = FrozenSet.Create(
        StringComparer.Ordinal,
        "root", "daemon", "bin", "sys", "sync", "games", "man", "lp", "mail", "news", "uucp", "proxy", "www-data",
        "backup", "list", "irc", "gnats", "nobody", "nogroup", "_apt", "messagebus", "sshd", "polkitd", "avahi",
        "statd", "tss", "dnsmasq", "libvirt-qemu", "pithyx", "docker", "sudo", "adm", "staff", "users");

    /// <summary>Lower-cases and validates a user name.</summary>
    public static string NormalizeUsername(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var username = input.Trim().ToLowerInvariant();
        if (!UsernamePattern().IsMatch(username))
        {
            throw new PithyxException(ErrorCodes.AuthUsernameInvalid);
        }
        if (_reserved.Contains(username) || username.StartsWith("systemd-", StringComparison.Ordinal))
        {
            throw new PithyxException(ErrorCodes.AuthUsernameReserved, new Dictionary<string, object?> { ["username"] = username });
        }
        return username;
    }

    /// <summary>
    /// Lower-cases and validates a group name: the rules of a user name, and nothing that starts with <c>pithyx-</c>,
    /// which the daemon keeps for its own groups.
    /// </summary>
    public static string NormalizeGroupName(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Trim().ToLowerInvariant();
        if (!UsernamePattern().IsMatch(name))
        {
            throw new PithyxException(ErrorCodes.UsersGroupNameInvalid);
        }
        if (_reserved.Contains(name) || name.StartsWith("systemd-", StringComparison.Ordinal) || name.StartsWith("pithyx-", StringComparison.Ordinal))
        {
            throw new PithyxException(ErrorCodes.UsersGroupNameReserved, new Dictionary<string, object?> { ["name"] = name });
        }
        return name;
    }

    /// <summary>Trims a display name; an empty one becomes null.</summary>
    public static string? NormalizeDisplayName(string? input)
    {
        var displayName = input?.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            return null;
        }
        if (displayName.Length > MaxDisplayNameLength || displayName.Any(char.IsControl))
        {
            throw new PithyxException(ErrorCodes.AuthDisplayNameInvalid, new Dictionary<string, object?> { ["maxLength"] = MaxDisplayNameLength });
        }
        return displayName;
    }

    /// <summary>
    /// The hard rules for every new password (setup, recovery, user management, password change): the length limits
    /// and the list of common passwords. The desktop checks the same list for its live feedback.
    /// </summary>
    public static void ValidateNewPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinPasswordLength)
        {
            throw new PithyxException(ErrorCodes.AuthPasswordTooShort, new Dictionary<string, object?> { ["minLength"] = MinPasswordLength });
        }
        if (password.Length > MaxPasswordLength)
        {
            throw new PithyxException(ErrorCodes.AuthPasswordTooLong, new Dictionary<string, object?> { ["maxLength"] = MaxPasswordLength });
        }
        if (_commonPasswords.Value.Contains(password.ToLowerInvariant()))
        {
            throw new PithyxException(ErrorCodes.AuthPasswordTooCommon);
        }
    }

    // The most used passwords from breach data (see Auth/common-passwords.LICENSE.txt), reduced to the ones long
    // enough to pass the length check. There are no composition rules; length plus this list is the hard policy.
    private static readonly Lazy<FrozenSet<string>> _commonPasswords = new(() =>
    {
        using var stream = typeof(CredentialRules).Assembly.GetManifestResourceStream("auth/common-passwords.txt")
            ?? throw new InvalidOperationException("Embedded resource auth/common-passwords.txt is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(StringComparer.Ordinal);
    });

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
