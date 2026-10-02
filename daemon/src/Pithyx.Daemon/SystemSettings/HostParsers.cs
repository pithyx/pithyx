using System.Text.RegularExpressions;

namespace Pithyx.Daemon.SystemSettings;

/// <summary>Pure parsers for the host files the Info Center, Network and Regional settings read.</summary>
public static partial class HostParsers
{
    /// <summary><c>PRETTY_NAME</c> of <c>/etc/os-release</c>, else <c>NAME VERSION</c>.</summary>
    public static string? ParseOsRelease(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"');
        }
        if (values.TryGetValue("PRETTY_NAME", out var pretty) && pretty.Length > 0)
        {
            return pretty;
        }
        return values.TryGetValue("NAME", out var name) ? $"{name} {values.GetValueOrDefault("VERSION", "")}".Trim() : null;
    }

    /// <summary>The first <c>model name</c> of <c>/proc/cpuinfo</c> (x86), or the <c>Model</c> line (arm64 boards).</summary>
    public static string? ParseCpuModel(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var line in content.Split('\n'))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator].Trim();
            if (key is "model name" or "Model")
            {
                return line[(separator + 1)..].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// How each interface gets its address from <c>/etc/network/interfaces</c> (ifupdown): <c>dhcp</c>, <c>static</c>
    /// or <c>manual</c> per <c>iface &lt;name&gt; inet|inet6 &lt;method&gt;</c> line; IPv4 wins over IPv6.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseInterfacesFile(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var methods = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var match = IfaceLine().Match(raw.Trim());
            if (!match.Success)
            {
                continue;
            }
            var name = match.Groups["name"].Value;
            if (match.Groups["family"].Value == "inet" || !methods.ContainsKey(name))
            {
                methods[name] = match.Groups["method"].Value;
            }
        }
        return methods;
    }

    /// <summary>The time servers of a timesyncd drop-in (<c>NTP=</c> in <c>[Time]</c>).</summary>
    public static IReadOnlyList<string> ParseTimesyncdServers(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var servers = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("NTP=", StringComparison.Ordinal))
            {
                servers = [.. trimmed[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries)];
            }
        }
        return servers;
    }

    /// <summary>The IANA name of a <c>/etc/localtime</c> link target such as <c>/usr/share/zoneinfo/Europe/Berlin</c>.</summary>
    public static string? TimeZoneOfLink(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        const string Marker = "zoneinfo/";
        var index = target.LastIndexOf(Marker, StringComparison.Ordinal);
        return index < 0 ? null : target[(index + Marker.Length)..];
    }

    /// <summary>The drop-in Pithyx writes for timesyncd; no servers means the distribution's defaults.</summary>
    public static string RenderTimesyncdDropIn(IReadOnlyList<string> servers) =>
        $"# Managed by pithyx. Changes are overwritten; put your own settings in another file of this folder.\n[Time]\nNTP={string.Join(' ', servers)}\n";

    /// <summary>A host name or an IPv4 or IPv6 address, as timesyncd takes it.</summary>
    public static bool IsTimeServer(string value) =>
        value is not null && value.Length is > 0 and <= 253 && (HostName().IsMatch(value) || System.Net.IPAddress.TryParse(value, out _));

    [GeneratedRegex(@"^iface\s+(?<name>\S+)\s+(?<family>inet6?)\s+(?<method>\S+)")]
    private static partial Regex IfaceLine();

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();
}
