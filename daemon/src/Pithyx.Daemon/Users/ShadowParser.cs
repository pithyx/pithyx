using System.Globalization;

namespace Pithyx.Daemon.Users;

/// <summary>
/// Reads the account expiry and whether a password is set from <c>/etc/shadow</c>. The password field leaves the parser
/// only through <see cref="Hashes"/>, for the encrypted configuration backup.
/// </summary>
public static class ShadowParser
{
    /// <summary>The names of accounts with an expiry date set (field 8), which Pithyx sets only to deactivate an account.</summary>
    public static IReadOnlySet<string> Expiring(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split(':');
            if (fields.Length == 9 && fields[0].Length > 0 && !fields[0].StartsWith('#')
                && long.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                names.Add(fields[0]);
            }
        }
        return names;
    }

    /// <summary>
    /// The names of accounts with a password Linux accepts: field 2 is neither empty nor locked (<c>!</c>, <c>*</c>).
    /// Only the first character is looked at.
    /// </summary>
    public static IReadOnlySet<string> WithPassword(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split(':');
            if (fields.Length == 9 && fields[0].Length > 0 && !fields[0].StartsWith('#') && fields[1].Length > 0 && fields[1][0] is not ('!' or '*'))
            {
                names.Add(fields[0]);
            }
        }
        return names;
    }

    /// <summary>
    /// The password field of each named account, as <c>chpasswd -e</c> takes it back. Only for the configuration backup,
    /// which encrypts it; accounts without a usable password are left out.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Hashes(string text, IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split(':');
            if (fields.Length == 9 && names.Contains(fields[0]) && fields[1].Length > 0 && fields[1][0] is not ('!' or '*'))
            {
                hashes[fields[0]] = fields[1];
            }
        }
        return hashes;
    }
}
