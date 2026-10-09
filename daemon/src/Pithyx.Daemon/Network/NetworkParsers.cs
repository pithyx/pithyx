using System.Globalization;
using System.Net;

namespace Pithyx.Daemon.Network;

/// <summary>A default route the kernel has.</summary>
/// <param name="Interface">The interface it leaves through.</param>
/// <param name="Gateway">The gateway, or null for a route without one.</param>
/// <param name="Metric">Lower wins.</param>
public sealed record DefaultRoute(string Interface, IPAddress? Gateway, long Metric);

/// <summary>Pure parsers for the kernel's and systemd-networkd's network state files.</summary>
public static class NetworkParsers
{
    /// <summary><c>/sys/class/net/&lt;name&gt;/uevent</c>: the <c>DEVTYPE</c> (<c>bond</c>, <c>vlan</c>, <c>bridge</c>, <c>wlan</c>), or null for a plain card.</summary>
    public static string? DevType(string uevent)
    {
        ArgumentNullException.ThrowIfNull(uevent);
        return Pairs(uevent).GetValueOrDefault("DEVTYPE");
    }

    /// <summary><c>/proc/net/route</c>: the IPv4 default routes.</summary>
    public static IReadOnlyList<DefaultRoute> Ipv4DefaultRoutes(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var routes = new List<DefaultRoute>();
        foreach (var line in content.Split('\n').Skip(1))
        {
            // Iface Destination Gateway Flags RefCnt Use Metric Mask MTU Window IRTT, hexadecimal in host byte order.
            var parts = line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || parts[1] != "00000000" || parts[7] != "00000000"
                || !uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var gateway)
                || !long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var metric))
            {
                continue;
            }
            routes.Add(new DefaultRoute(parts[0], gateway == 0 ? null : new IPAddress(BitConverter.IsLittleEndian ? gateway : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(gateway)), metric));
        }
        return routes;
    }

    /// <summary><c>/proc/net/ipv6_route</c>: the IPv6 default routes.</summary>
    public static IReadOnlyList<DefaultRoute> Ipv6DefaultRoutes(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var routes = new List<DefaultRoute>();
        foreach (var line in content.Split('\n'))
        {
            // destination, its prefix, source, its prefix, next hop, metric, refcount, use, flags, interface.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10 || parts[0] != new string('0', 32) || parts[1] != "00" || parts[9] == "lo"
                || !long.TryParse(parts[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var metric)
                || parts[4].Length != 32)
            {
                continue;
            }
            var bytes = Convert.FromHexString(parts[4]);
            var gateway = new IPAddress(bytes);
            routes.Add(new DefaultRoute(parts[9], gateway.Equals(IPAddress.IPv6Any) ? null : gateway, metric));
        }
        return routes;
    }

    /// <summary><c>/proc/net/vlan/config</c>: each VLAN with its id and parent.</summary>
    public static IReadOnlyDictionary<string, (int Id, string Parent)> Vlans(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var vlans = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && parts[0].Length > 0 && parts[2].Length > 0)
            {
                vlans[parts[0]] = (id, parts[2]);
            }
        }
        return vlans;
    }

    /// <summary><c>/run/systemd/netif/links/&lt;ifindex&gt;</c>: networkd's state of a link as key and value.</summary>
    public static IReadOnlyDictionary<string, string> NetworkdLink(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Pairs(content);
    }

    /// <summary>The name servers of a networkd link state (<c>DNS=</c>, separated by spaces, maybe with port or interface).</summary>
    public static IReadOnlyList<string> NetworkdDns(IReadOnlyDictionary<string, string> link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return [.. (link.GetValueOrDefault("DNS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(server => server.Split('%')[0])
            .Where(server => IPAddress.TryParse(server, out _))];
    }

    /// <summary>A sysfs number such as <c>speed</c> or <c>mtu</c>; null when missing, unknown (<c>-1</c>) or not a number.</summary>
    public static int? Number(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;

    /// <summary>
    /// <c>/etc/hosts</c> with the line of <c>127.0.1.1</c> naming <paramref name="hostName"/>, the way Debian's installer
    /// writes it; the line is added after localhost when missing.
    /// </summary>
    public static string HostsWithName(string content, string hostName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        var lines = content.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        var entry = $"127.0.1.1\t{hostName}";
        var index = lines.FindIndex(line => line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries) is [var address, ..] && address == "127.0.1.1");
        if (index >= 0)
        {
            lines[index] = entry;
        }
        else
        {
            var localhost = lines.FindIndex(line => line.StartsWith("127.0.0.1", StringComparison.Ordinal));
            lines.Insert(localhost + 1, entry);
        }
        return string.Join('\n', lines) + "\n";
    }

    private static Dictionary<string, string> Pairs(string content)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n'))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && !line.StartsWith('#'))
            {
                pairs[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }
        return pairs;
    }
}
