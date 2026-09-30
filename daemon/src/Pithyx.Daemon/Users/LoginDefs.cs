using System.Globalization;

namespace Pithyx.Daemon.Users;

/// <summary>
/// Reads the uid and gid ranges from <c>/etc/login.defs</c>. <c>adduser</c> and <c>useradd</c> outside Pithyx must never
/// allocate in 20000 to 49999; the image sets the ranges, the daemon only checks them and warns.
/// </summary>
public static class LoginDefs
{
    private const long PithyxFirst = 20000;
    private const long PithyxLast = 49999;

    /// <summary>The settings whose range reaches into the Pithyx ranges, for example <c>UID_MIN..UID_MAX</c>.</summary>
    public static IReadOnlyList<string> Overlaps(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && !fields[0].StartsWith('#') && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                values[fields[0]] = value;
            }
        }
        var overlaps = new List<string>();
        foreach (var (kind, minimum, maximum) in new[] { ("UID", 1000L, 60000L), ("GID", 1000L, 60000L), ("SYS_UID", 100L, 999L), ("SYS_GID", 100L, 999L) })
        {
            var low = values.GetValueOrDefault($"{kind}_MIN", minimum);
            var high = values.GetValueOrDefault($"{kind}_MAX", maximum);
            if (low <= PithyxLast && high >= PithyxFirst)
            {
                overlaps.Add($"{kind}_MIN..{kind}_MAX");
            }
        }
        return overlaps;
    }
}
