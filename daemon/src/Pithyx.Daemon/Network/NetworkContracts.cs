namespace Pithyx.Daemon.Network;

/// <summary>What an interface is: a network card, a bond of cards or a VLAN on a card or bond.</summary>
public enum InterfaceKind
{
    Ethernet,
    Bond,
    Vlan,
}

/// <summary>How an interface gets its IPv4 address.</summary>
public enum Ipv4Mode
{
    Dhcp,
    Static,
    Off,
}

/// <summary>How an interface gets its IPv6 address: automatically (router advertisements and DHCPv6), static or none.</summary>
public enum Ipv6Mode
{
    Auto,
    Static,
    Off,
}

/// <summary>How a bond uses its cards.</summary>
public enum BondMode
{
    /// <summary>One card carries the traffic, another takes over when its link goes down. Works with every switch.</summary>
    ActiveBackup,

    /// <summary>IEEE 802.3ad (LACP): all cards together; the switch ports must form an LACP group.</summary>
    Lacp,

    /// <summary>Adaptive load balancing: all cards send and receive without switch support.</summary>
    BalanceAlb,
}

/// <summary>How often LACP partners exchange their state: every 30 seconds or every second.</summary>
public enum LacpRate
{
    Slow,
    Fast,
}

/// <param name="Members">The network cards in the bond, at least one; none of them has its own configuration.</param>
/// <param name="Mode">How the bond uses its cards.</param>
/// <param name="Primary">For active-backup: the card that carries the traffic while its link is up.</param>
/// <param name="LacpRate">For LACP: how often the partners exchange their state.</param>
public sealed record BondSettings(IReadOnlyList<string> Members, BondMode Mode, string? Primary = null, LacpRate LacpRate = LacpRate.Slow);

/// <param name="Parent">The network card or bond that carries the VLAN's tagged frames.</param>
/// <param name="Id">The 802.1Q VLAN id, 1 to 4094.</param>
public sealed record VlanSettings(string Parent, int Id);

/// <summary>The settings Pithyx keeps for one interface. A network card without them gets its address by DHCP.</summary>
/// <param name="Name">The interface: the kernel's name of a card, or the name of a bond or VLAN (1 to 15 characters).</param>
/// <param name="Kind">Card, bond or VLAN.</param>
/// <param name="Enabled">False keeps the link down.</param>
/// <param name="Ipv4">DHCP, static or off.</param>
/// <param name="Ipv4Address">For static: the address with prefix length, for example <c>192.168.1.10/24</c>.</param>
/// <param name="Ipv4Gateway">For static: the gateway, optional, inside the address's network.</param>
/// <param name="Ipv6">Auto (router advertisements and DHCPv6), static or off.</param>
/// <param name="Ipv6Address">For static: the address with prefix length, for example <c>2001:db8::10/64</c>.</param>
/// <param name="Ipv6Gateway">For static: the gateway, optional, inside the address's network or link-local.</param>
/// <param name="Mtu">The MTU in bytes, 1280 to 9000 (576 without IPv6); null keeps the default.</param>
/// <param name="Bond">For a bond: its cards and mode.</param>
/// <param name="Vlan">For a VLAN: its parent and id.</param>
public sealed record InterfaceConfig(
    string Name,
    InterfaceKind Kind,
    bool Enabled = true,
    Ipv4Mode Ipv4 = Ipv4Mode.Dhcp,
    string? Ipv4Address = null,
    string? Ipv4Gateway = null,
    Ipv6Mode Ipv6 = Ipv6Mode.Auto,
    string? Ipv6Address = null,
    string? Ipv6Gateway = null,
    int? Mtu = null,
    BondSettings? Bond = null,
    VlanSettings? Vlan = null)
{
    /// <summary>Whether the interface ends up with an address of its own.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAddress => Enabled && (Ipv4 != Ipv4Mode.Off || Ipv6 != Ipv6Mode.Off);
}

/// <summary>Settings of the whole box.</summary>
/// <param name="HostName">The host name: letters, digits and hyphens, 1 to 63 characters. Samba uses its first 15 as the NetBIOS name.</param>
/// <param name="DnsServers">Name servers, at most four; when set they replace those DHCP and router advertisements announce.</param>
/// <param name="SearchDomains">Domains tried for short names, at most six.</param>
/// <param name="DefaultInterface">The interface whose gateway carries the default route when several have one; null leaves it to the metrics.</param>
public sealed record GlobalNetworkConfig(
    string HostName,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<string> SearchDomains,
    string? DefaultInterface = null);

/// <summary>Everything Pithyx configures in the network. Cards that are not listed get their address by DHCP.</summary>
/// <param name="Interfaces">Cards with their own settings, bonds and VLANs.</param>
/// <param name="Global">Host name, name servers, search domains, default route.</param>
public sealed record NetworkConfig(IReadOnlyList<InterfaceConfig> Interfaces, GlobalNetworkConfig Global);

/// <summary>An interface as the system has it now.</summary>
/// <param name="Name">The kernel's name, for example <c>enp1s0</c>.</param>
/// <param name="Kind">Card, bond or VLAN.</param>
/// <param name="MacAddress">The hardware address.</param>
/// <param name="Up">Whether the interface is up and its link works.</param>
/// <param name="Carrier">Whether a cable (or the other end) is connected.</param>
/// <param name="SpeedMbps">The link speed in Mbit/s, when the driver reports it.</param>
/// <param name="Duplex"><c>full</c> or <c>half</c>, when the driver reports it.</param>
/// <param name="Mtu">The MTU in bytes.</param>
/// <param name="Addresses">IPv4 and global IPv6 addresses with prefix length.</param>
/// <param name="Gateways">The default gateways reached through this interface.</param>
/// <param name="DnsServers">The name servers this interface announced or was given.</param>
/// <param name="DefaultRoute">Whether this interface carries the default route (the lowest metric).</param>
/// <param name="Master">The bond this card belongs to.</param>
/// <param name="VlanParent">For a VLAN: the interface that carries it.</param>
/// <param name="VlanId">For a VLAN: its id.</param>
/// <param name="Method"><c>dhcp</c>, <c>static</c>, <c>off</c> or <c>unknown</c>: how its IPv4 address comes.</param>
/// <param name="Configured">Whether Pithyx keeps settings for it (otherwise a card gets DHCP).</param>
/// <param name="Changeable">Whether Pithyx may change it here; on a development machine only the interfaces in <c>/etc/pithyx/test-nics</c>.</param>
public sealed record NetworkLink(
    string Name,
    InterfaceKind Kind,
    string? MacAddress,
    bool Up,
    bool Carrier,
    int? SpeedMbps,
    string? Duplex,
    int Mtu,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    bool DefaultRoute,
    string? Master,
    string? VlanParent,
    int? VlanId,
    string Method,
    bool Configured,
    bool Changeable);

/// <summary>Where a change of the network came from.</summary>
public enum NetworkChangeOrigin
{
    Web,
    Console,
}

/// <summary>A change of the network waiting for its confirmation.</summary>
/// <param name="Id">Pass to confirm or revert it.</param>
/// <param name="Deadline">When it is rolled back unless confirmed, UTC.</param>
/// <param name="Interfaces">The interfaces it changed.</param>
/// <param name="Addresses">Static addresses (without prefix) it set; open one of them when the old address went away.</param>
/// <param name="HostName">The new host name, when it changed.</param>
/// <param name="Origin">Settings in a browser or the menu on the console.</param>
public sealed record PendingNetworkChange(
    Guid Id,
    DateTimeOffset Deadline,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> Addresses,
    string? HostName,
    NetworkChangeOrigin Origin);

/// <summary>The network of the box. Administrators only.</summary>
/// <param name="Config">What Pithyx configured.</param>
/// <param name="Links">Every card, bond and VLAN; container bridges and virtual cables are left out.</param>
/// <param name="HostName">The host name the system has now.</param>
/// <param name="GlobalChangeable">Whether Pithyx may change host name, name servers and the default route here; not on a development machine.</param>
/// <param name="Pending">The change waiting for its confirmation.</param>
/// <param name="ConfirmSeconds">How long a change waits for its confirmation before it is rolled back.</param>
/// <param name="ConnectionInterface">The interface this browser's connection comes in on, if known; empty in the live topic.</param>
public sealed record NetworkState(
    NetworkConfig Config,
    IReadOnlyList<NetworkLink> Links,
    string HostName,
    bool GlobalChangeable,
    PendingNetworkChange? Pending,
    int ConfirmSeconds,
    string? ConnectionInterface);

/// <summary>What a change would do, before it is applied.</summary>
/// <param name="Config">The settings as they would be stored (canonical form).</param>
/// <param name="Interfaces">The interfaces whose configuration changes.</param>
/// <param name="HostNameChanged">Whether the host name changes.</param>
/// <param name="ConnectionAffected">Whether the interface this browser's connection comes in on changes; the page warns.</param>
/// <param name="Addresses">Static addresses (without prefix) of the changed interfaces, to open after the change.</param>
public sealed record NetworkCheck(
    NetworkConfig Config,
    IReadOnlyList<string> Interfaces,
    bool HostNameChanged,
    bool ConnectionAffected,
    IReadOnlyList<string> Addresses);

/// <summary>The JSON form of the network settings in SQLite, and comparison by value (records with lists compare by reference).</summary>
public static class NetworkJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("Stored network settings are empty.");

    public static bool Same<T>(T? a, T? b) => Serialize(a) == Serialize(b);
}
