using System.Globalization;

namespace Pithyx.Daemon.Users;

/// <summary>Reads the account expiry and whether a password is set from <c>/etc/shadow</c>; the password field never leaves the parser.</summary>
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
}
