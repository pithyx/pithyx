using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Network;

/// <summary>Where Pithyx may change the network (D292, D337).</summary>
/// <param name="AnyCard">Every card (a box, simulation); otherwise only <paramref name="TestNics"/>.</param>
/// <param name="TestNics">On a development machine the cards in <c>/etc/pithyx/test-nics</c>.</param>
/// <param name="GlobalChangeable">Host name, name servers, search domains and the default route; never on a development machine.</param>
public sealed record NetworkPolicy(bool AnyCard, IReadOnlySet<string> TestNics, bool GlobalChangeable)
{
    public static NetworkPolicy Everything { get; } = new(true, new HashSet<string>(), true);

    public bool CardChangeable(string name) => AnyCard || TestNics.Contains(name);

    /// <summary>A card by its name, a bond when all its cards are, a VLAN when its parent is.</summary>
    public bool Changeable(InterfaceConfig item, NetworkConfig config)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(config);
        return item.Kind switch
        {
            InterfaceKind.Bond => item.Bond is { Members.Count: > 0 } bond && bond.Members.All(CardChangeable),
            InterfaceKind.Vlan => item.Vlan is { } vlan && (config.Interfaces.FirstOrDefault(other => other.Name == vlan.Parent && other.Kind == InterfaceKind.Bond) is { } parent
                ? Changeable(parent, config)
                : CardChangeable(vlan.Parent)),
            _ => CardChangeable(item.Name),
        };
    }
}

/// <summary>
/// Checks a network configuration before anything changes and brings it into its stored form. Every refusal is a
/// <see cref="PithyxException"/> with a <c>network.*</c> code naming the interface and field. Pure.
/// </summary>
public static partial class NetworkValidator
{
    public const int MaxDnsServers = 4;
    public const int MaxSearchDomains = 6;
    public const int MinMtu = 1280;
    public const int MinMtuWithoutIpv6 = 576;
    public const int MaxMtu = 9000;
    public const int MaxNameLength = 15;

    /// <summary>Names Docker, the apps' networks (<c>px-*</c>, nftables <c>apps-input</c>), systemd's containers and VMs use.</summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes = ["px-", "veth", "docker", "br-", "ve-", "vb-", "vz-", "vt-", "virbr", "tun", "tap", "wg"];

    /// <summary>
    /// The configuration in canonical form. <paramref name="links"/> are the interfaces the system has now,
    /// <paramref name="current"/> what is stored; a card that is gone may keep its unchanged settings.
    /// </summary>
    public static NetworkConfig Validate(NetworkConfig requested, IReadOnlyList<NetworkLink> links, NetworkConfig current, NetworkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        var items = (requested.Interfaces ?? []).Select(Canonical).OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        var global = CanonicalGlobal(requested.Global);
        var config = new NetworkConfig(items, global);
        var byName = new Dictionary<string, InterfaceConfig>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!byName.TryAdd(item.Name, item))
            {
                throw Fail(ErrorCodes.NetworkInterfaceDuplicate, ("name", item.Name));
            }
        }
        var linkByName = links.ToDictionary(link => link.Name, StringComparer.Ordinal);
        var previous = current.Interfaces.ToDictionary(item => item.Name, StringComparer.Ordinal);
        bool IsCard(string name) => linkByName.TryGetValue(name, out var link) ? link.Kind == InterfaceKind.Ethernet : previous.TryGetValue(name, out var stored) && stored.Kind == InterfaceKind.Ethernet;
        var members = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case InterfaceKind.Ethernet:
                    if (item.Bond is not null || item.Vlan is not null)
                    {
                        throw Fail(ErrorCodes.NetworkInterfaceKindInvalid, ("name", item.Name));
                    }
                    var known = linkByName.TryGetValue(item.Name, out var card);
                    if (known ? card!.Kind != InterfaceKind.Ethernet : !NetworkJson.Same(previous.GetValueOrDefault(item.Name), item))
                    {
                        throw Fail(known ? ErrorCodes.NetworkInterfaceKindInvalid : ErrorCodes.NetworkInterfaceUnknown, ("name", item.Name));
                    }
                    break;
                case InterfaceKind.Bond:
                    CheckNewName(item, linkByName, previous);
                    if (item.Bond is not { } bond || item.Vlan is not null)
                    {
                        throw Fail(ErrorCodes.NetworkInterfaceKindInvalid, ("name", item.Name));
                    }
                    if (bond.Members.Count == 0)
                    {
                        throw Fail(ErrorCodes.NetworkBondMembersMissing, ("name", item.Name));
                    }
                    foreach (var member in bond.Members)
                    {
                        var reason = !IsCard(member) ? "not_card"
                            : byName.ContainsKey(member) ? "configured"
                            : members.ContainsKey(member) ? "other_bond"
                            : items.Any(other => other.Vlan?.Parent == member) ? "vlan_parent"
                            : null;
                        if (reason is not null)
                        {
                            throw Fail(ErrorCodes.NetworkBondMemberInvalid, ("name", item.Name), ("member", member), ("reason", reason));
                        }
                        members[member] = item.Name;
                    }
                    if (bond.Primary is { } primary && !bond.Members.Contains(primary))
                    {
                        throw Fail(ErrorCodes.NetworkBondPrimaryInvalid, ("name", item.Name));
                    }
                    break;
                case InterfaceKind.Vlan:
                    CheckNewName(item, linkByName, previous);
                    if (item.Vlan is not { } vlan || item.Bond is not null)
                    {
                        throw Fail(ErrorCodes.NetworkInterfaceKindInvalid, ("name", item.Name));
                    }
                    if (vlan.Id is < 1 or > 4094)
                    {
                        throw Fail(ErrorCodes.NetworkVlanIdInvalid, ("name", item.Name));
                    }
                    var parentOk = byName.TryGetValue(vlan.Parent, out var parent)
                        ? parent.Kind is InterfaceKind.Ethernet or InterfaceKind.Bond
                        : IsCard(vlan.Parent);
                    if (!parentOk || items.Any(other => other.Bond?.Members.Contains(vlan.Parent) == true))
                    {
                        throw Fail(ErrorCodes.NetworkVlanParentInvalid, ("name", item.Name), ("parent", vlan.Parent));
                    }
                    if (items.Any(other => other != item && other.Vlan is { } same && same.Parent == vlan.Parent && same.Id == vlan.Id))
                    {
                        throw Fail(ErrorCodes.NetworkVlanDuplicate, ("parent", vlan.Parent), ("id", vlan.Id));
                    }
                    break;
            }
            CheckAddresses(item);
            if (item.Mtu is { } mtu)
            {
                var min = item.Ipv6 == Ipv6Mode.Off ? MinMtuWithoutIpv6 : MinMtu;
                if (mtu < min || mtu > MaxMtu)
                {
                    throw Fail(ErrorCodes.NetworkMtuInvalid, ("name", item.Name), ("min", min), ("max", MaxMtu));
                }
            }
        }

        foreach (var item in items.Concat(current.Interfaces.Where(old => !byName.ContainsKey(old.Name))))
        {
            var stored = previous.GetValueOrDefault(item.Name);
            var unchanged = NetworkJson.Same(stored, item) && byName.ContainsKey(item.Name);
            if (!unchanged && !(policy.Changeable(item, byName.ContainsKey(item.Name) ? config : current) && (stored is null || policy.Changeable(stored, current))))
            {
                throw Fail(ErrorCodes.NetworkInterfaceNotChangeable, ("name", item.Name));
            }
        }

        if (!NetworkJson.Same(global, current.Global) && !policy.GlobalChangeable)
        {
            throw Fail(ErrorCodes.NetworkGlobalNotChangeable);
        }
        if (global.DefaultInterface is { } preferred
            && (!byName.TryGetValue(preferred, out var chosen) || !chosen.Enabled || !HasGateway(chosen)))
        {
            throw Fail(ErrorCodes.NetworkDefaultInterfaceInvalid, ("name", preferred));
        }

        // Somebody must still reach the box: an interface with settings that give an address, or a card that keeps DHCP.
        var reachable = items.Any(item => item.HasAddress)
            || links.Any(link => link.Kind == InterfaceKind.Ethernet && !byName.ContainsKey(link.Name) && !members.ContainsKey(link.Name));
        if (!reachable)
        {
            throw Fail(ErrorCodes.NetworkConfigUnreachable);
        }
        return config;
    }

    /// <summary>A host name of one label: letters, digits and hyphens, 1 to 63, not starting or ending with a hyphen; lower case.</summary>
    public static string HostName(string? name)
    {
        var trimmed = name?.Trim().ToLowerInvariant() ?? "";
        return HostNamePattern().IsMatch(trimmed) ? trimmed : throw Fail(ErrorCodes.NetworkHostNameInvalid);
    }

    /// <summary>The static addresses an interface gets, without prefix.</summary>
    public static IReadOnlyList<string> StaticAddresses(InterfaceConfig item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.Enabled)
        {
            return [];
        }
        return [.. new[] { item.Ipv4 == Ipv4Mode.Static ? item.Ipv4Address : null, item.Ipv6 == Ipv6Mode.Static ? item.Ipv6Address : null }
            .OfType<string>()
            .Select(address => address.Split('/')[0])];
    }

    /// <summary>Whether two addresses of one family share the first <paramref name="prefix"/> bits.</summary>
    public static bool SameNetwork(IPAddress a, IPAddress b, int prefix)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var left = a.GetAddressBytes();
        var right = b.GetAddressBytes();
        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = 0x80 >> (bit % 8);
            if ((left[bit / 8] & mask) != (right[bit / 8] & mask))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasGateway(InterfaceConfig item) =>
        item.Ipv4 == Ipv4Mode.Dhcp || item.Ipv6 == Ipv6Mode.Auto || item.Ipv4Gateway is not null || item.Ipv6Gateway is not null;

    private static InterfaceConfig Canonical(InterfaceConfig item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var name = (item.Name ?? "").Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(c => c is '/' or ':' || char.IsWhiteSpace(c)) || name is "." or "..")
        {
            throw Fail(ErrorCodes.NetworkInterfaceNameInvalid, ("name", name));
        }
        string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        string? Address(string? value) => Trim(value) is { } text && text.Split('/') is [var address, var prefix]
            && IPAddress.TryParse(address, out var parsed) && !address.Contains('%', StringComparison.Ordinal)
            && int.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            ? $"{parsed}/{length.ToString(CultureInfo.InvariantCulture)}"
            : Trim(value);
        string? Host(string? value) => Trim(value) is { } text && IPAddress.TryParse(text, out var parsed) && !text.Contains('%', StringComparison.Ordinal) ? parsed.ToString() : Trim(value);
        var bond = item.Bond is { } settings
            ? new BondSettings(
                [.. (settings.Members ?? []).Select(member => member.Trim()).Where(member => member.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                settings.Mode,
                settings.Mode == BondMode.ActiveBackup ? Trim(settings.Primary) : null,
                settings.Mode == BondMode.Lacp ? settings.LacpRate : LacpRate.Slow)
            : null;
        var vlan = item.Vlan is { } tagged ? new VlanSettings((tagged.Parent ?? "").Trim(), tagged.Id) : null;
        return item with
        {
            Name = name,
            Ipv4Address = item.Ipv4 == Ipv4Mode.Static ? Address(item.Ipv4Address) : null,
            Ipv4Gateway = item.Ipv4 == Ipv4Mode.Static ? Host(item.Ipv4Gateway) : null,
            Ipv6Address = item.Ipv6 == Ipv6Mode.Static ? Address(item.Ipv6Address) : null,
            Ipv6Gateway = item.Ipv6 == Ipv6Mode.Static ? Host(item.Ipv6Gateway) : null,
            Bond = bond,
            Vlan = vlan,
        };
    }

    private static void CheckNewName(InterfaceConfig item, Dictionary<string, NetworkLink> links, Dictionary<string, InterfaceConfig> previous)
    {
        var name = item.Name;
        if (!NamePattern().IsMatch(name) || ReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) || name is "lo" or "bonding_masters")
        {
            throw Fail(ErrorCodes.NetworkInterfaceNameInvalid, ("name", name));
        }
        // The name of a card or of something Pithyx did not create is taken.
        if (links.TryGetValue(name, out var link) && (link.Kind != item.Kind || !previous.ContainsKey(name)))
        {
            throw Fail(ErrorCodes.NetworkInterfaceDuplicate, ("name", name));
        }
    }

    private static void CheckAddresses(InterfaceConfig item)
    {
        if (item.Ipv4 == Ipv4Mode.Static)
        {
            var (network, prefix) = Prefixed(item, item.Ipv4Address, AddressFamily.InterNetwork, "ipv4Address");
            Gateway(item, item.Ipv4Gateway, AddressFamily.InterNetwork, network, prefix, "ipv4Gateway");
        }
        if (item.Ipv6 == Ipv6Mode.Static)
        {
            var (network, prefix) = Prefixed(item, item.Ipv6Address, AddressFamily.InterNetworkV6, "ipv6Address");
            Gateway(item, item.Ipv6Gateway, AddressFamily.InterNetworkV6, network, prefix, "ipv6Gateway");
        }
    }

    private static (IPAddress Address, int Prefix) Prefixed(InterfaceConfig item, string? input, AddressFamily family, string field)
    {
        var parts = (input ?? "").Split('/');
        var max = family == AddressFamily.InterNetwork ? 31 : 128;
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var parsed)
            || parsed.AddressFamily != family
            || parts[0].Contains('%', StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            || prefix < 1 || prefix > max
            || IPAddress.IsLoopback(parsed)
            || (family == AddressFamily.InterNetwork && (parsed.GetAddressBytes()[0] is 0 or >= 224))
            || (family == AddressFamily.InterNetworkV6 && (parsed.IsIPv6Multicast || parsed.IsIPv6LinkLocal || parsed.Equals(IPAddress.IPv6Any))))
        {
            throw Fail(ErrorCodes.NetworkAddressInvalid, ("name", item.Name), ("field", field));
        }
        return (parsed, prefix);
    }

    private static void Gateway(InterfaceConfig item, string? input, AddressFamily family, IPAddress address, int prefix, string field)
    {
        if (input is null)
        {
            return;
        }
        if (!IPAddress.TryParse(input, out var gateway)
            || gateway.AddressFamily != family
            || gateway.Equals(address)
            || !(SameNetwork(address, gateway, prefix) || (family == AddressFamily.InterNetworkV6 && gateway.IsIPv6LinkLocal && !input.Contains('%', StringComparison.Ordinal))))
        {
            throw Fail(ErrorCodes.NetworkGatewayInvalid, ("name", item.Name), ("field", field));
        }
    }

    private static GlobalNetworkConfig CanonicalGlobal(GlobalNetworkConfig? global)
    {
        if (global is null)
        {
            throw Fail(ErrorCodes.NetworkHostNameInvalid);
        }
        var dns = (global.DnsServers ?? []).Select(server => server.Trim()).Where(server => server.Length > 0).ToList();
        if (dns.FirstOrDefault(server => !IPAddress.TryParse(server, out var parsed) || server.Contains('%', StringComparison.Ordinal) || IPAddress.Any.Equals(parsed) || IPAddress.IPv6Any.Equals(parsed)) is { } invalid)
        {
            throw Fail(ErrorCodes.NetworkDnsInvalid, ("server", invalid));
        }
        var servers = dns.Select(server => IPAddress.Parse(server).ToString()).Distinct(StringComparer.Ordinal).ToList();
        if (servers.Count > MaxDnsServers)
        {
            throw Fail(ErrorCodes.NetworkDnsTooMany, ("max", MaxDnsServers));
        }
        var domains = (global.SearchDomains ?? []).Select(domain => domain.Trim().TrimEnd('.').ToLowerInvariant()).Where(domain => domain.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (domains.FirstOrDefault(domain => domain.Length > 253 || !domain.Split('.').All(label => HostNamePattern().IsMatch(label))) is { } wrong)
        {
            throw Fail(ErrorCodes.NetworkSearchDomainInvalid, ("domain", wrong));
        }
        if (domains.Count > MaxSearchDomains)
        {
            throw Fail(ErrorCodes.NetworkSearchDomainTooMany, ("max", MaxSearchDomains));
        }
        var preferred = string.IsNullOrWhiteSpace(global.DefaultInterface) ? null : global.DefaultInterface.Trim();
        return new GlobalNetworkConfig(HostName(global.HostName), servers, domains, preferred);
    }

    private static PithyxException Fail(string code, params (string Key, object? Value)[] parameters) =>
        new(code, parameters.ToDictionary(pair => pair.Key, pair => pair.Value));

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostNamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
