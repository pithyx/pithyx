using System.Globalization;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Updates;

/// <summary>A Debian package <c>apt-get --simulate upgrade</c> would install.</summary>
/// <param name="Name">The package.</param>
/// <param name="From">The installed version; null for a package the upgrade adds.</param>
/// <param name="To">The version it would install.</param>
/// <param name="Security">Whether it comes from a security archive (<c>Debian-Security:13/stable-security</c>).</param>
public sealed record DebianUpdate(string Name, string? From, string To, bool Security);

/// <summary>One md array of <c>/proc/mdstat</c>.</summary>
/// <param name="Device">The kernel name, for example <c>md127</c>.</param>
/// <param name="Members">The member devices that are there and not failed.</param>
/// <param name="Failed">Members marked failed (<c>(F)</c>).</param>
/// <param name="Expected">The members the array is built for (<c>[2/1]</c>: 2).</param>
/// <param name="Working">The members that work (<c>[2/1]</c>: 1).</param>
/// <param name="Syncing">Whether a resync or recovery runs.</param>
public sealed record MdArray(string Device, IReadOnlyList<string> Members, IReadOnlyList<string> Failed, int Expected, int Working, bool Syncing)
{
    /// <summary>A member is missing or failed; a single-disk system (one member of one) is not degraded.</summary>
    public bool Degraded => Working < Expected;
}

/// <summary>Pure parsers for the update and system disk checks.</summary>
public static partial class SystemParsers
{
    /// <summary>
    /// The <c>Inst</c> lines of <c>apt-get --simulate upgrade</c> (fixtures recorded with <c>LC_ALL=C.UTF-8</c>):
    /// <c>Inst name [installed] (new origin [arch])</c>, without the brackets for a new package.
    /// </summary>
    public static IReadOnlyList<DebianUpdate> ParseSimulatedUpgrade(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var updates = new List<DebianUpdate>();
        foreach (var line in output.Split('\n'))
        {
            var match = InstLine().Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                updates.Add(new DebianUpdate(
                    match.Groups["name"].Value,
                    match.Groups["from"].Success ? match.Groups["from"].Value : null,
                    match.Groups["to"].Value,
                    match.Groups["origin"].Value.Split(',').Any(origin => origin.Contains("-security", StringComparison.OrdinalIgnoreCase))));
            }
        }
        return updates;
    }

    /// <summary>The arrays of <c>/proc/mdstat</c>.</summary>
    public static IReadOnlyList<MdArray> ParseMdstat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var arrays = new List<MdArray>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var head = ArrayLine().Match(lines[index]);
            if (!head.Success)
            {
                continue;
            }
            var members = new List<string>();
            var failed = new List<string>();
            foreach (Match member in MemberToken().Matches(head.Groups["members"].Value))
            {
                (member.Groups["failed"].Success ? failed : members).Add(member.Groups["device"].Value);
            }
            int expected = 0, working = 0;
            var syncing = false;
            for (var next = index + 1; next < lines.Length && lines[next].StartsWith(' '); next++)
            {
                if (Counts().Match(lines[next]) is { Success: true } counts)
                {
                    expected = int.Parse(counts.Groups["expected"].Value, CultureInfo.InvariantCulture);
                    working = int.Parse(counts.Groups["working"].Value, CultureInfo.InvariantCulture);
                }
                syncing |= Sync().IsMatch(lines[next]);
            }
            arrays.Add(new MdArray(head.Groups["device"].Value, members, failed, expected, working, syncing));
        }
        return arrays;
    }

    [GeneratedRegex(@"^Inst (?<name>\S+) (?:\[(?<from>[^\]]+)\] )?\((?<to>\S+) (?<origin>[^\[]*?) ?\[[^\]]+\]\)")]
    private static partial Regex InstLine();

    [GeneratedRegex(@"^(?<device>md\d+) : \S+ \S+ (?<members>.*)$")]
    private static partial Regex ArrayLine();

    [GeneratedRegex(@"(?<device>[A-Za-z0-9_-]+)\[\d+\](?<failed>\(F\))?")]
    private static partial Regex MemberToken();

    [GeneratedRegex(@"\[(?<expected>\d+)/(?<working>\d+)\]")]
    private static partial Regex Counts();

    [GeneratedRegex(@"\b(resync|recovery|reshape|check)\s*=")]
    private static partial Regex Sync();
}
