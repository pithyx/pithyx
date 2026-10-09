namespace Pithyx.Daemon.Network;

/// <summary>
/// The network of a simulated box: three cards (two with a cable), the files Pithyx would write, and the interfaces
/// that follow from its settings, with made-up DHCP addresses. Nothing on the host changes.
/// </summary>
public sealed class SimulatedNetwork
{
    private static readonly (string Name, string Mac, bool Carrier)[] _cards =
    [
        ("enp1s0", "52:54:00:a1:00:01", true),
        ("enp2s0", "52:54:00:a1:00:02", true),
        ("enp3s0", "52:54:00:a1:00:03", false),
    ];

    private readonly Lock _lock = new();
    private SortedDictionary<string, string> _files = new(StringComparer.Ordinal);
    private NetworkConfig _config = new([], new GlobalNetworkConfig(Environment.MachineName, [], []));

    public string HostName { get; set; } = Environment.MachineName;

    public IReadOnlyDictionary<string, string> Files
    {
        get
        {
            lock (_lock)
            {
                return new SortedDictionary<string, string>(_files, StringComparer.Ordinal);
            }
        }
    }

    public void Apply(NetworkdPlan plan, NetworkConfig config)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(config);
        lock (_lock)
        {
            var files = new SortedDictionary<string, string>(_files, StringComparer.Ordinal);
            foreach (var path in plan.Delete)
            {
                files.Remove(path);
            }
            foreach (var (path, content) in plan.Write)
            {
                files[path] = content;
            }
            _files = files;
            _config = config;
        }
    }

    public IReadOnlyList<NetworkLink> Links()
    {
        NetworkConfig config;
        lock (_lock)
        {
            config = _config;
        }
        var byName = config.Interfaces.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var masters = config.Interfaces.Where(item => item.Bond is not null)
            .SelectMany(item => item.Bond!.Members.Select(member => (member, item.Name)))
            .ToDictionary(pair => pair.member, pair => pair.Name, StringComparer.Ordinal);
        var links = new List<NetworkLink>();
        for (var index = 0; index < _cards.Length; index++)
        {
            var (name, mac, carrier) = _cards[index];
            var item = byName.GetValueOrDefault(name) ?? new InterfaceConfig(name, InterfaceKind.Ethernet);
            if (masters.TryGetValue(name, out var bond))
            {
                var mtu = byName.GetValueOrDefault(bond)?.Mtu ?? 1500;
                links.Add(new NetworkLink(name, InterfaceKind.Ethernet, mac, carrier, carrier, carrier ? 1000 : null, carrier ? "full" : null, mtu, [], [], [], false, bond, null, null, "off", false, false));
                continue;
            }
            links.Add(Link(item, mac, carrier, carrier ? 1000 : null, $"192.168.{index + 1}", $"fd00:{index + 1}", config));
        }
        var number = 0;
        foreach (var item in config.Interfaces.Where(item => item.Kind == InterfaceKind.Bond))
        {
            number++;
            var members = _cards.Where(card => item.Bond!.Members.Contains(card.Name)).ToList();
            var up = members.Any(card => card.Carrier);
            var speed = members.Count(card => card.Carrier) * (item.Bond!.Mode == BondMode.ActiveBackup ? 0 : 1000);
            links.Add(Link(item, members.FirstOrDefault().Mac, up, up ? Math.Max(speed, 1000) : null, $"10.10.{number}", $"fd10:{number}", config));
        }
        foreach (var item in config.Interfaces.Where(item => item.Kind == InterfaceKind.Vlan))
        {
            var parent = links.FirstOrDefault(link => link.Name == item.Vlan!.Parent);
            var up = parent?.Carrier ?? false;
            links.Add(Link(item, parent?.MacAddress, up, parent?.SpeedMbps, $"10.42.{item.Vlan!.Id % 256}", $"fd42:{item.Vlan.Id}", config) with
            {
                VlanParent = item.Vlan.Parent,
                VlanId = item.Vlan.Id,
            });
        }
        var preferred = config.Global.DefaultInterface ?? links.FirstOrDefault(link => link.Gateways.Count > 0)?.Name;
        return [.. links.Select(link => link with { DefaultRoute = link.Name == preferred && link.Gateways.Count > 0 }).OrderBy(link => link.Name, StringComparer.Ordinal)];
    }

    private static NetworkLink Link(InterfaceConfig item, string? mac, bool carrier, int? speed, string ipv4Network, string ipv6Network, NetworkConfig config)
    {
        var up = item.Enabled && carrier;
        var addresses = new List<string>();
        var gateways = new List<string>();
        if (up)
        {
            switch (item.Ipv4)
            {
                case Ipv4Mode.Dhcp:
                    addresses.Add($"{ipv4Network}.20/24");
                    gateways.Add($"{ipv4Network}.1");
                    break;
                case Ipv4Mode.Static:
                    addresses.Add(item.Ipv4Address!);
                    if (item.Ipv4Gateway is { } gateway)
                    {
                        gateways.Add(gateway);
                    }
                    break;
            }
            switch (item.Ipv6)
            {
                case Ipv6Mode.Auto:
                    addresses.Add($"{ipv6Network}::20/64");
                    gateways.Add("fe80::1");
                    break;
                case Ipv6Mode.Static:
                    addresses.Add(item.Ipv6Address!);
                    if (item.Ipv6Gateway is { } gateway)
                    {
                        gateways.Add(gateway);
                    }
                    break;
            }
        }
        var dns = !up ? [] : config.Global.DnsServers.Count > 0 ? config.Global.DnsServers : item.Ipv4 == Ipv4Mode.Dhcp ? [$"{ipv4Network}.1"] : (IReadOnlyList<string>)[];
        var method = !item.Enabled ? "off" : MethodOf(item.Ipv4);
        return new NetworkLink(item.Name, item.Kind, mac, up, carrier && item.Enabled, up ? speed : null, up && speed is not null ? "full" : null, item.Mtu ?? 1500,
            addresses, gateways, dns, false, null, null, null, method, false, false);
    }

    public static string MethodOf(Ipv4Mode mode) => mode switch
    {
        Ipv4Mode.Dhcp => "dhcp",
        Ipv4Mode.Static => "static",
        _ => "off",
    };
}
