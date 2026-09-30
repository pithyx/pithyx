using System.Globalization;

namespace Pithyx.Daemon.Users;

/// <summary>One line of <c>/etc/passwd</c>.</summary>
public sealed record PasswdEntry(string Name, long Uid, long Gid, string Comment, string Home, string Shell);

/// <summary>Parses <c>/etc/passwd</c>; comments, blank and malformed lines are skipped.</summary>
public static class PasswdParser
{
    public static IReadOnlyList<PasswdEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<PasswdEntry>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split(':');
            if (fields.Length != 7 || fields[0].Length == 0 || fields[0].StartsWith('#')
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
            {
                continue;
            }
            entries.Add(new PasswdEntry(fields[0], uid, gid, fields[4], fields[5], fields[6]));
        }
        return entries;
    }
}
