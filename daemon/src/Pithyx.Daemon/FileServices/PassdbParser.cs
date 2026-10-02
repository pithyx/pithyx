using System.Globalization;

namespace Pithyx.Daemon.FileServices;

/// <summary>A Samba account as <c>pdbedit -L -w</c> lists it; the hashes never leave the parser.</summary>
/// <param name="Name">The user name.</param>
/// <param name="Uid">The Unix uid.</param>
/// <param name="HasPassword">Whether an NT password hash is set.</param>
/// <param name="Disabled">Whether the account flags contain <c>D</c>.</param>
public sealed record PassdbEntry(string Name, long Uid, bool HasPassword, bool Disabled);

/// <summary>Parses the smbpasswd format of <c>pdbedit -L -w</c>: <c>name:uid:LM:NT:[flags]:LCT-...:</c>.</summary>
public static class PassdbParser
{
    public static IReadOnlyList<PassdbEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<PassdbEntry>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split(':');
            if (fields.Length < 5 || fields[0].Length == 0
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                || !fields[4].StartsWith('[') || !fields[4].EndsWith(']'))
            {
                continue;
            }
            var nt = fields[3];
            var hasPassword = nt.Length == 32 && !nt.All(c => c == 'X') && !nt.StartsWith("NO PASSWORD", StringComparison.Ordinal);
            entries.Add(new PassdbEntry(fields[0], uid, hasPassword, fields[4].Contains('D', StringComparison.Ordinal)));
        }
        return entries;
    }
}
