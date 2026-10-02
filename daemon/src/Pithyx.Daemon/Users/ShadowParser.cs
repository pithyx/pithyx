using System.Globalization;

namespace Pithyx.Daemon.Users;

/// <summary>Reads the account expiry from <c>/etc/shadow</c>. Only the name and the expiry field are looked at; the password field never leaves the parser.</summary>
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
}
