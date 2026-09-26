using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Auth;

/// <summary>
/// Rules for user names, display names and passwords. Every user becomes a Linux and Samba user later, so user names
/// follow the portable Linux rules and are stored in lower case.
/// </summary>
public static partial class CredentialRules
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 1024;
    public const int MaxDisplayNameLength = 64;

    // Accounts that exist on Debian or are created by packages Vyxis installs. The daemon additionally checks
    // /etc/passwd when it provisions Linux users (M3).
    private static readonly FrozenSet<string> _reserved = FrozenSet.Create(
        StringComparer.Ordinal,
        "root", "daemon", "bin", "sys", "sync", "games", "man", "lp", "mail", "news", "uucp", "proxy", "www-data",
        "backup", "list", "irc", "gnats", "nobody", "nogroup", "_apt", "messagebus", "sshd", "polkitd", "avahi",
        "statd", "tss", "dnsmasq", "libvirt-qemu", "vyxis", "docker", "sudo", "adm", "staff", "users");

    /// <summary>Lower-cases and validates a user name.</summary>
    public static string NormalizeUsername(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var username = input.Trim().ToLowerInvariant();
        if (!UsernamePattern().IsMatch(username))
        {
            throw new VyxisException(ErrorCodes.AuthUsernameInvalid);
        }
        if (_reserved.Contains(username) || username.StartsWith("systemd-", StringComparison.Ordinal))
        {
            throw new VyxisException(ErrorCodes.AuthUsernameReserved, new Dictionary<string, object?> { ["username"] = username });
        }
        return username;
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
            throw new VyxisException(ErrorCodes.AuthDisplayNameInvalid, new Dictionary<string, object?> { ["maxLength"] = MaxDisplayNameLength });
        }
        return displayName;
    }

    public static void ValidateNewPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinPasswordLength)
        {
            throw new VyxisException(ErrorCodes.AuthPasswordTooShort, new Dictionary<string, object?> { ["minLength"] = MinPasswordLength });
        }
        if (password.Length > MaxPasswordLength)
        {
            throw new VyxisException(ErrorCodes.AuthPasswordTooLong, new Dictionary<string, object?> { ["maxLength"] = MaxPasswordLength });
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
