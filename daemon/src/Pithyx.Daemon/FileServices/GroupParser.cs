namespace Pithyx.Daemon.FileServices;

/// <summary>One line of <c>/etc/group</c>.</summary>
public sealed record GroupEntry(string Name, long Gid, IReadOnlyList<string> Members);

/// <summary>Parses <c>/etc/group</c>; comments, blank and malformed lines are skipped.</summary>
public static class GroupParser
{
    public static IReadOnlyList<GroupEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<GroupEntry>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split(':');
            if (fields.Length != 4 || fields[0].Length == 0 || fields[0].StartsWith('#')
                || !long.TryParse(fields[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var gid))
            {
                continue;
            }
            entries.Add(new GroupEntry(fields[0], gid, fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries)));
        }
        return entries;
    }
}
