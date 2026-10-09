using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Network;

/// <summary>What has to happen on the host to go from one set of Pithyx's files to another.</summary>
/// <param name="Write">Files to write (new or changed), path to content.</param>
/// <param name="Delete">Files to remove.</param>
/// <param name="RemoveNetdevs">Bonds and VLANs to delete before networkd reloads: removed, or changed in a way networkd cannot apply to a running device.</param>
/// <param name="Release">Cards that leave a bond which stays.</param>
/// <param name="Reconfigure">Interfaces whose file changed; networkd configures them again after the reload.</param>
/// <param name="ResolvedChanged">The name servers or search domains changed.</param>
/// <param name="DefaultFileChanged">The settings of cards without their own file changed (their name servers).</param>
public sealed record NetworkdPlan(
    IReadOnlyDictionary<string, string> Write,
    IReadOnlyList<string> Delete,
    IReadOnlyList<string> RemoveNetdevs,
    IReadOnlyList<string> Release,
    IReadOnlyList<string> Reconfigure,
    bool ResolvedChanged,
    bool DefaultFileChanged)
{
    public bool IsEmpty => Write.Count == 0 && Delete.Count == 0;
}

/// <summary>
/// The systemd-networkd and systemd-resolved files of the network Pithyx configured (D110, D292, D337), rendered from
/// the settings in SQLite, and the plan to get from the files on the host to them. Pithyx owns only
/// <c>/etc/systemd/network/10-pithyx-*</c>, the name server drop-in of the image's DHCP file and its resolved drop-in;
/// cards without settings keep the image's <c>80-pithyx-ethernet.network</c> (DHCP). Pure, so golden files test it.
/// </summary>
public static partial class NetworkdConfig
{
    public const string Directory = "/etc/systemd/network";

    /// <summary>The image's file for every card without settings (DHCP, D294).</summary>
    public const string DefaultFile = Directory + "/80-pithyx-ethernet.network";

    /// <summary>Turns off the name servers DHCP announces on cards without settings while Pithyx sets its own.</summary>
    public const string DefaultDnsDropIn = DefaultFile + ".d/10-pithyx-dns.conf";

    public const string ResolvedDropIn = "/etc/systemd/resolved.conf.d/pithyx.conf";

    public const string Header = "# Managed by pithyx. Pithyx writes this file again on every change of the network in Settings.";

    /// <summary>The metric of the default route through the interface chosen to carry it.</summary>
    public const int PreferredMetric = 100;

    /// <summary>The metric of every other default route, the same as networkd gives DHCP routes.</summary>
    public const int OtherMetric = 1024;

    private const string Prefix = "10-pithyx-";

    public static string NetworkPath(string name) => $"{Directory}/{Prefix}{name}.network";

    public static string NetdevPath(string name) => $"{Directory}/{Prefix}{name}.netdev";

    /// <summary>Whether Pithyx owns the file at <paramref name="path"/>.</summary>
    public static bool IsOwned(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path == DefaultDnsDropIn || path == ResolvedDropIn || NameOf(path) is not null;
    }

    /// <summary>The interface of one of Pithyx's networkd files, or null for another path.</summary>
    public static string? NameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var match = OwnedFile().Match(path);
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>Every file the configuration needs, by path. The configuration must have passed <see cref="NetworkValidator"/>.</summary>
    public static SortedDictionary<string, string> Render(NetworkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var dns = config.Global.DnsServers.Count > 0;
        var vlansByParent = config.Interfaces
            .Where(item => item.Vlan is not null)
            .GroupBy(item => item.Vlan!.Parent, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Name).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var configured = config.Interfaces.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var item in config.Interfaces)
        {
            files[NetworkPath(item.Name)] = RenderNetwork(item, vlansByParent.GetValueOrDefault(item.Name) ?? [], Metric(config, item.Name), dns);
            if (item.Bond is { } bond)
            {
                files[NetdevPath(item.Name)] = RenderBond(item.Name, bond);
                foreach (var member in bond.Members)
                {
                    files[NetworkPath(member)] = RenderMember(member, item.Name, bond.Mode == BondMode.ActiveBackup && bond.Primary == member, item.Mtu);
                }
            }
            if (item.Vlan is { } vlan)
            {
                files[NetdevPath(item.Name)] = RenderVlan(item.Name, vlan.Id);
            }
        }
        // A VLAN needs a line in its parent's file; a card without settings gets one that keeps DHCP.
        foreach (var (parent, vlans) in vlansByParent.Where(pair => !configured.ContainsKey(pair.Key)))
        {
            files[NetworkPath(parent)] = RenderNetwork(new InterfaceConfig(parent, InterfaceKind.Ethernet), vlans, Metric(config, parent), dns);
        }
        if (dns)
        {
            files[DefaultDnsDropIn] = RenderDefaultDnsDropIn();
        }
        if (dns || config.Global.SearchDomains.Count > 0)
        {
            files[ResolvedDropIn] = RenderResolved(config.Global);
        }
        return files;
    }

    /// <summary>What to do on the host to replace <paramref name="current"/> with <paramref name="desired"/>.</summary>
    public static NetworkdPlan Plan(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        var write = desired.Where(pair => !current.TryGetValue(pair.Key, out var old) || old != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var delete = current.Keys.Where(path => !desired.ContainsKey(path)).Order(StringComparer.Ordinal).ToList();
        var changed = write.Keys.Concat(delete).ToList();
        // networkd does not change the kind, mode or id of a running bond or VLAN; it is deleted and created again.
        var removeNetdevs = changed.Where(path => path.EndsWith(".netdev", StringComparison.Ordinal) && current.ContainsKey(path))
            .Select(path => NameOf(path)!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var release = changed.Where(path => path.EndsWith(".network", StringComparison.Ordinal) && current.ContainsKey(path))
            .Where(path => BondOf(current[path]) is { } bond && bond != (desired.TryGetValue(path, out var next) ? BondOf(next) : null) && !removeNetdevs.Contains(bond))
            .Select(path => NameOf(path)!)
            .Order(StringComparer.Ordinal)
            .ToList();
        // A bond or VLAN created again needs its cards or its parent configured again to join it.
        var recreated = removeNetdevs.Where(name => desired.ContainsKey(NetdevPath(name))).ToHashSet(StringComparer.Ordinal);
        var stacked = desired.Where(pair => pair.Key.EndsWith(".network", StringComparison.Ordinal) && StackedLines().Matches(pair.Value).Any(match => recreated.Contains(match.Groups["name"].Value.Trim())))
            .Select(pair => pair.Key);
        var reconfigure = changed.Where(path => path.EndsWith(".network", StringComparison.Ordinal))
            .Concat(stacked)
            .Select(NameOf)
            .OfType<string>()
            .Where(name => !removeNetdevs.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new NetworkdPlan(
            write,
            delete,
            removeNetdevs,
            release,
            reconfigure,
            changed.Contains(ResolvedDropIn),
            changed.Contains(DefaultDnsDropIn));
    }

    /// <summary>The bond a card's file puts it in, if any.</summary>
    public static string? BondOf(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var match = BondLine().Match(content);
        return match.Success ? match.Groups["bond"].Value.Trim() : null;
    }

    private static int Metric(NetworkConfig config, string name) => config.Global.DefaultInterface == name ? PreferredMetric : OtherMetric;

    private static string RenderNetwork(InterfaceConfig item, IReadOnlyList<string> vlans, int metric, bool ownDns)
    {
        var text = Start(item.Name);
        var ipv4Dhcp = item.Enabled && item.Ipv4 == Ipv4Mode.Dhcp;
        var ipv6Auto = item.Enabled && item.Ipv6 == Ipv6Mode.Auto;
        if (item.Mtu is not null || !item.Enabled)
        {
            text.AppendLine("[Link]");
            if (item.Mtu is { } mtu)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"MTUBytes={mtu}");
            }
            if (!item.Enabled)
            {
                text.AppendLine("ActivationPolicy=down");
            }
            text.AppendLine();
        }
        text.AppendLine("[Network]");
        text.AppendLine($"DHCP={(ipv4Dhcp ? "ipv4" : "no")}");
        text.AppendLine($"IPv6AcceptRA={(ipv6Auto ? "yes" : "no")}");
        text.AppendLine($"LinkLocalAddressing={(item.Enabled && item.Ipv6 != Ipv6Mode.Off ? "ipv6" : "no")}");
        if (item.Enabled && item.Ipv4 == Ipv4Mode.Static)
        {
            text.AppendLine($"Address={item.Ipv4Address}");
        }
        if (item.Enabled && item.Ipv6 == Ipv6Mode.Static)
        {
            text.AppendLine($"Address={item.Ipv6Address}");
        }
        foreach (var vlan in vlans)
        {
            text.AppendLine($"VLAN={vlan}");
        }
        if (ipv4Dhcp)
        {
            text.AppendLine().AppendLine("[DHCPv4]");
            // The live system and the installed system ask for the same address (D294).
            text.AppendLine("ClientIdentifier=mac");
            text.AppendLine("UseHostname=no");
            if (ownDns)
            {
                text.AppendLine("UseDNS=no");
            }
            text.AppendLine(CultureInfo.InvariantCulture, $"RouteMetric={metric}");
        }
        if (ipv6Auto)
        {
            text.AppendLine().AppendLine("[IPv6AcceptRA]");
            if (ownDns)
            {
                text.AppendLine("UseDNS=no");
            }
            text.AppendLine(CultureInfo.InvariantCulture, $"RouteMetric={metric}");
            text.AppendLine().AppendLine("[DHCPv6]");
            text.AppendLine("UseHostname=no");
            if (ownDns)
            {
                text.AppendLine("UseDNS=no");
            }
        }
        foreach (var gateway in new[] { item.Ipv4 == Ipv4Mode.Static ? item.Ipv4Gateway : null, item.Ipv6 == Ipv6Mode.Static ? item.Ipv6Gateway : null })
        {
            if (item.Enabled && gateway is not null)
            {
                text.AppendLine().AppendLine("[Route]");
                text.AppendLine($"Gateway={gateway}");
                // Never 0: a route of Pithyx's must not tie with one the system has from elsewhere.
                text.AppendLine(CultureInfo.InvariantCulture, $"Metric={metric}");
            }
        }
        return text.ToString();
    }

    private static string RenderMember(string name, string bond, bool primary, int? mtu)
    {
        var text = Start(name);
        if (mtu is { } bytes)
        {
            text.AppendLine("[Link]").AppendLine(CultureInfo.InvariantCulture, $"MTUBytes={bytes}").AppendLine();
        }
        text.AppendLine("[Network]");
        text.AppendLine($"Bond={bond}");
        if (primary)
        {
            text.AppendLine("PrimarySlave=yes");
        }
        return text.ToString();
    }

    private static string RenderBond(string name, BondSettings bond)
    {
        var text = new StringBuilder();
        text.AppendLine(Header).AppendLine();
        text.AppendLine("[NetDev]").AppendLine($"Name={name}").AppendLine("Kind=bond").AppendLine();
        text.AppendLine("[Bond]");
        text.AppendLine($"Mode={bond.Mode switch
        {
            BondMode.ActiveBackup => "active-backup",
            BondMode.Lacp => "802.3ad",
            _ => "balance-alb",
        }}");
        text.AppendLine("MIIMonitorSec=100ms");
        if (bond.Mode == BondMode.Lacp)
        {
            text.AppendLine($"LACPTransmitRate={(bond.LacpRate == LacpRate.Fast ? "fast" : "slow")}");
            text.AppendLine("TransmitHashPolicy=layer3+4");
        }
        return text.ToString();
    }

    private static string RenderVlan(string name, int id)
    {
        var text = new StringBuilder();
        text.AppendLine(Header).AppendLine();
        text.AppendLine("[NetDev]").AppendLine($"Name={name}").AppendLine("Kind=vlan").AppendLine();
        text.AppendLine("[VLAN]").AppendLine(CultureInfo.InvariantCulture, $"Id={id}");
        return text.ToString();
    }

    private static string RenderDefaultDnsDropIn() =>
        new StringBuilder()
            .AppendLine(Header).AppendLine()
            .AppendLine("[DHCPv4]").AppendLine("UseDNS=no").AppendLine()
            .AppendLine("[IPv6AcceptRA]").AppendLine("UseDNS=no").AppendLine()
            .AppendLine("[DHCPv6]").AppendLine("UseDNS=no")
            .ToString();

    private static string RenderResolved(GlobalNetworkConfig global)
    {
        var text = new StringBuilder();
        text.AppendLine(Header).AppendLine();
        text.AppendLine("[Resolve]");
        if (global.DnsServers.Count > 0)
        {
            text.AppendLine($"DNS={string.Join(' ', global.DnsServers)}");
        }
        if (global.SearchDomains.Count > 0)
        {
            text.AppendLine($"Domains={string.Join(' ', global.SearchDomains)}");
        }
        return text.ToString();
    }

    private static StringBuilder Start(string name)
    {
        var text = new StringBuilder();
        text.AppendLine(Header).AppendLine();
        text.AppendLine("[Match]").AppendLine($"Name={name}").AppendLine();
        return text;
    }

    [GeneratedRegex(@"^/etc/systemd/network/10-pithyx-(?<name>[^/]+)\.(network|netdev)$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedFile();

    [GeneratedRegex(@"^Bond=(?<bond>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BondLine();

    [GeneratedRegex(@"^(Bond|VLAN)=(?<name>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StackedLines();
}
