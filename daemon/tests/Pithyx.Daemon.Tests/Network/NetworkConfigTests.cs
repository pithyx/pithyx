using System.Text;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Network;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Network;

/// <summary>The networkd files Pithyx renders (golden files), the plan that applies them, and the checks before.</summary>
public sealed class NetworkConfigTests
{
    private static GlobalNetworkConfig Global(string? defaultInterface = null) => new("nas", [], [], defaultInterface);

    private static NetworkLink Card(string name, string? master = null) =>
        new(name, InterfaceKind.Ethernet, "52:54:00:00:00:01", true, true, 1000, "full", 1500, [], [], [], false, master, null, null, "dhcp", false, true);

    private static readonly IReadOnlyList<NetworkLink> _cards = [Card("enp1s0"), Card("enp2s0"), Card("enp3s0")];

    private static readonly NetworkConfig _empty = new([], Global());

    private static InterfaceConfig Static(string name = "enp1s0") => new(
        name, InterfaceKind.Ethernet,
        Ipv4: Ipv4Mode.Static, Ipv4Address: "192.168.1.10/24", Ipv4Gateway: "192.168.1.1",
        Ipv6: Ipv6Mode.Static, Ipv6Address: "2001:db8::10/64", Ipv6Gateway: "fe80::1");

    private static InterfaceConfig Bond(BondMode mode, string? primary = null, int? mtu = null) =>
        new("bond0", InterfaceKind.Bond, Mtu: mtu, Bond: new BondSettings(["enp1s0", "enp2s0"], mode, primary, LacpRate.Fast));

    private static NetworkConfig Valid(params InterfaceConfig[] items) => Valid(Global(), items);

    private static NetworkConfig Valid(GlobalNetworkConfig global, params InterfaceConfig[] items) =>
        NetworkValidator.Validate(new NetworkConfig(items, global), _cards, _empty, NetworkPolicy.Everything);

    /// <summary>Every file of a configuration in one text, so one golden file shows a whole case.</summary>
    private static string Files(NetworkConfig config)
    {
        var text = new StringBuilder();
        foreach (var (path, content) in NetworkdConfig.Render(config))
        {
            text.Append("==> ").AppendLine(path).Append(content).AppendLine();
        }
        return text.ToString();
    }

    private static void AssertGolden(string actual, string name)
    {
        var path = Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "network", name + ".golden");
        if (Environment.GetEnvironmentVariable("PITHYX_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
        }
        Assert.Equal(File.ReadAllText(path), actual);
    }

    public static TheoryData<string> Cases => new(["static", "dhcp", "ipv6-only", "ipv6-off", "disabled", "mtu", "active-backup", "lacp", "balance-alb", "vlan-on-bond", "vlan-on-card", "dns-and-default-route"]);

    [Theory]
    [MemberData(nameof(Cases))]
    public void RendersEachCaseAsItsGoldenFiles(string name)
    {
        var config = name switch
        {
            "static" => Valid(Static()),
            "dhcp" => Valid(new InterfaceConfig("enp1s0", InterfaceKind.Ethernet)),
            "ipv6-only" => Valid(new InterfaceConfig("enp1s0", InterfaceKind.Ethernet, Ipv4: Ipv4Mode.Off, Ipv6: Ipv6Mode.Static, Ipv6Address: "2001:db8:1::5/64", Ipv6Gateway: "2001:db8:1::1")),
            "ipv6-off" => Valid(new InterfaceConfig("enp1s0", InterfaceKind.Ethernet, Ipv6: Ipv6Mode.Off)),
            "disabled" => Valid(new InterfaceConfig("enp3s0", InterfaceKind.Ethernet, Enabled: false)),
            "mtu" => Valid(new InterfaceConfig("enp1s0", InterfaceKind.Ethernet, Mtu: 9000)),
            "active-backup" => Valid(Bond(BondMode.ActiveBackup, "enp2s0", mtu: 9000)),
            "lacp" => Valid(Bond(BondMode.Lacp)),
            "balance-alb" => Valid(Bond(BondMode.BalanceAlb)),
            "vlan-on-bond" => Valid(
                Bond(BondMode.ActiveBackup) with { Ipv4 = Ipv4Mode.Off, Ipv6 = Ipv6Mode.Off },
                new InterfaceConfig("vlan42", InterfaceKind.Vlan, Ipv4: Ipv4Mode.Static, Ipv4Address: "10.42.0.10/24", Ipv6: Ipv6Mode.Off, Vlan: new VlanSettings("bond0", 42))),
            "vlan-on-card" => Valid(new InterfaceConfig("enp1s0.7", InterfaceKind.Vlan, Vlan: new VlanSettings("enp1s0", 7))),
            "dns-and-default-route" => Valid(
                new GlobalNetworkConfig("nas", ["9.9.9.9", "2620:fe::fe"], ["home.arpa"], "enp2s0"),
                Static(),
                new InterfaceConfig("enp2s0", InterfaceKind.Ethernet)),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        AssertGolden(Files(config), name);
    }

    [Fact]
    public void PlansOnlyWhatChanged()
    {
        var bond = NetworkdConfig.Render(Valid(Bond(BondMode.ActiveBackup)));
        Assert.True(NetworkdConfig.Plan(bond, bond).IsEmpty);

        // A new mode needs the bond deleted and created again; its cards join the new one.
        var lacp = NetworkdConfig.Plan(bond, NetworkdConfig.Render(Valid(Bond(BondMode.Lacp))));
        Assert.Equal(["bond0"], lacp.RemoveNetdevs);
        Assert.Equal(["enp1s0", "enp2s0"], lacp.Reconfigure);
        Assert.Empty(lacp.Release);

        // A card leaving a bond that stays is released from it.
        var smaller = Valid(new InterfaceConfig("bond0", InterfaceKind.Bond, Bond: new BondSettings(["enp1s0"], BondMode.ActiveBackup)));
        var leave = NetworkdConfig.Plan(bond, NetworkdConfig.Render(smaller));
        Assert.Equal(["enp2s0"], leave.Release);
        Assert.Equal([NetworkdConfig.NetworkPath("enp2s0")], leave.Delete);
        Assert.Contains("enp2s0", leave.Reconfigure);
        Assert.Empty(leave.RemoveNetdevs);

        // Without the bond everything goes, and the cards fall back to the image's DHCP file.
        var gone = NetworkdConfig.Plan(bond, NetworkdConfig.Render(_empty));
        Assert.Equal(["bond0"], gone.RemoveNetdevs);
        Assert.Equal(["enp1s0", "enp2s0"], gone.Reconfigure);
        Assert.Equal(4, gone.Delete.Count);

        var dns = NetworkdConfig.Plan(new Dictionary<string, string>(), NetworkdConfig.Render(Valid(Global() with { DnsServers = ["9.9.9.9"] })));
        Assert.True(dns.ResolvedChanged);
        Assert.True(dns.DefaultFileChanged);
        Assert.Empty(dns.Reconfigure);
    }

    [Fact]
    public void OwnsOnlyItsOwnFiles()
    {
        Assert.Equal("enp1s0.7", NetworkdConfig.NameOf("/etc/systemd/network/10-pithyx-enp1s0.7.network"));
        Assert.Equal("bond0", NetworkdConfig.NameOf("/etc/systemd/network/10-pithyx-bond0.netdev"));
        Assert.Null(NetworkdConfig.NameOf(NetworkdConfig.DefaultFile));
        Assert.Null(NetworkdConfig.NameOf("/etc/systemd/network/20-own.network"));
        Assert.True(NetworkdConfig.IsOwned(NetworkdConfig.ResolvedDropIn));
        Assert.False(NetworkdConfig.IsOwned("/etc/systemd/resolved.conf"));
        Assert.Equal("bond0", NetworkdConfig.BondOf("[Network]\nBond=bond0\n"));
    }

    public static TheoryData<InterfaceConfig[], GlobalNetworkConfig?, string, string?> Refused => new()
    {
        { [Static() with { Ipv4Address = "192.168.1.10" }], null, "network.address.invalid", "ipv4Address" },
        { [Static() with { Ipv4Address = "192.168.1.300/24" }], null, "network.address.invalid", "ipv4Address" },
        { [Static() with { Ipv4Address = "2001:db8::1/64" }], null, "network.address.invalid", "ipv4Address" },
        { [Static() with { Ipv4Address = "127.0.0.2/8" }], null, "network.address.invalid", "ipv4Address" },
        { [Static() with { Ipv4Address = "224.0.0.1/4" }], null, "network.address.invalid", "ipv4Address" },
        { [Static() with { Ipv4Address = "10.0.0.1/32" }], null, "network.address.invalid", "ipv4Address" },
        { [Static() with { Ipv6Address = "fe80::1/64" }], null, "network.address.invalid", "ipv6Address" },
        { [Static() with { Ipv4Gateway = "10.0.0.1" }], null, "network.gateway.invalid", "ipv4Gateway" },
        { [Static() with { Ipv4Gateway = "192.168.1.10" }], null, "network.gateway.invalid", "ipv4Gateway" },
        { [Static() with { Ipv6Gateway = "2001:db9::1" }], null, "network.gateway.invalid", "ipv6Gateway" },
        { [Static() with { Mtu = 1000 }], null, "network.mtu.invalid", null },
        { [Static() with { Mtu = 9001 }], null, "network.mtu.invalid", null },
        { [new InterfaceConfig("enp9s0", InterfaceKind.Ethernet)], null, "network.interface.unknown", null },
        { [Static(), Static()], null, "network.interface.duplicate", null },
        { [new InterfaceConfig("px-1", InterfaceKind.Bond, Bond: new BondSettings(["enp1s0"], BondMode.ActiveBackup))], null, "network.interface.name_invalid", null },
        { [new InterfaceConfig("enp1s0", InterfaceKind.Bond, Bond: new BondSettings(["enp2s0"], BondMode.ActiveBackup))], null, "network.interface.duplicate", null },
        { [new InterfaceConfig("bond0", InterfaceKind.Bond)], null, "network.interface.kind_invalid", null },
        { [new InterfaceConfig("bond0", InterfaceKind.Bond, Bond: new BondSettings([], BondMode.Lacp))], null, "network.bond.members_missing", null },
        { [Bond(BondMode.ActiveBackup), Static()], null, "network.bond.member_invalid", null },
        { [Bond(BondMode.ActiveBackup), Bond(BondMode.ActiveBackup) with { Name = "bond1" }], null, "network.bond.member_invalid", null },
        { [new InterfaceConfig("bond0", InterfaceKind.Bond, Bond: new BondSettings(["enp1s0"], BondMode.ActiveBackup, "enp3s0"))], null, "network.bond.primary_invalid", null },
        { [new InterfaceConfig("vlan1", InterfaceKind.Vlan, Vlan: new VlanSettings("enp1s0", 0))], null, "network.vlan.id_invalid", null },
        { [new InterfaceConfig("vlan1", InterfaceKind.Vlan, Vlan: new VlanSettings("enp1s0", 4095))], null, "network.vlan.id_invalid", null },
        { [new InterfaceConfig("vlan1", InterfaceKind.Vlan, Vlan: new VlanSettings("nothing", 1))], null, "network.vlan.parent_invalid", null },
        { [Bond(BondMode.ActiveBackup), new InterfaceConfig("vlan1", InterfaceKind.Vlan, Vlan: new VlanSettings("enp1s0", 1))], null, "network.bond.member_invalid", null },
        { [new InterfaceConfig("a", InterfaceKind.Vlan, Vlan: new VlanSettings("enp1s0", 5)), new InterfaceConfig("b", InterfaceKind.Vlan, Vlan: new VlanSettings("enp1s0", 5))], null, "network.vlan.duplicate", null },
        { [], new GlobalNetworkConfig("-nas", [], []), "network.host_name.invalid", null },
        { [], new GlobalNetworkConfig("nas.local", [], []), "network.host_name.invalid", null },
        { [], new GlobalNetworkConfig("nas", ["dns.example"], []), "network.dns.invalid", null },
        { [], new GlobalNetworkConfig("nas", ["1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9"], []), "network.dns.too_many", null },
        { [], new GlobalNetworkConfig("nas", [], ["bad_domain"]), "network.search_domain.invalid", null },
        { [], new GlobalNetworkConfig("nas", [], [], "enp1s0"), "network.default_interface.invalid", null },
        { [Static() with { Ipv4Gateway = null, Ipv6Gateway = null }], new GlobalNetworkConfig("nas", [], [], "enp1s0"), "network.default_interface.invalid", null },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void RefusesWhatCannotWork(InterfaceConfig[] items, GlobalNetworkConfig? global, string code, string? field)
    {
        var refused = Assert.Throws<PithyxException>(() => Valid(global ?? Global(), items));
        Assert.Equal(code, refused.Code);
        if (field is not null)
        {
            Assert.Equal(field, refused.Parameters?["field"]);
        }
    }

    [Fact]
    public void RefusesAConfigurationThatLeavesNoAddress()
    {
        var off = new InterfaceConfig("x", InterfaceKind.Ethernet, Ipv4: Ipv4Mode.Off, Ipv6: Ipv6Mode.Off);
        var refused = Assert.Throws<PithyxException>(() => Valid(off with { Name = "enp1s0" }, off with { Name = "enp2s0" }, off with { Name = "enp3s0", Enabled = false }));
        Assert.Equal("network.config.unreachable", refused.Code);
        // A bond with an address keeps the box reachable even when its cards have none of their own.
        Valid(Bond(BondMode.ActiveBackup), off with { Name = "enp3s0" });
    }

    [Fact]
    public void AcceptsAndCanonicalizesWhatWorks()
    {
        var config = Valid(
            new GlobalNetworkConfig(" NAS-01 ", [" 192.168.1.1 ", "192.168.1.1", "2001:0db8::53"], ["Home.Arpa."]),
            Static() with { Ipv6Address = "2001:0db8:0000::0010/64", Ipv6Gateway = "2001:db8::1" },
            new InterfaceConfig("bond1", InterfaceKind.Bond, Ipv4: Ipv4Mode.Off, Bond: new BondSettings(["enp3s0", "enp2s0"], BondMode.Lacp, "enp2s0")));
        var card = config.Interfaces.Single(item => item.Name == "enp1s0");
        Assert.Equal("2001:db8::10/64", card.Ipv6Address);
        Assert.Equal(["192.168.1.10", "2001:db8::10"], NetworkValidator.StaticAddresses(card));
        var bond = config.Interfaces.Single(item => item.Name == "bond1").Bond!;
        Assert.Equal(["enp2s0", "enp3s0"], bond.Members);
        Assert.Null(bond.Primary);
        Assert.Equal("nas-01", config.Global.HostName);
        Assert.Equal(["192.168.1.1", "2001:db8::53"], config.Global.DnsServers);
        Assert.Equal(["home.arpa"], config.Global.SearchDomains);
    }

    [Fact]
    public void ADevelopmentMachineChangesOnlyItsTestCardsAndNeverTheHostName()
    {
        var policy = new NetworkPolicy(false, new HashSet<string>(["enp2s0", "enp3s0"]), false);
        var refused = Assert.Throws<PithyxException>(() => NetworkValidator.Validate(new NetworkConfig([Static()], Global()), _cards, _empty, policy));
        Assert.Equal(("network.interface.not_changeable", (object?)"enp1s0"), (refused.Code, refused.Parameters?["name"]));
        var bond = new InterfaceConfig("bond0", InterfaceKind.Bond, Bond: new BondSettings(["enp1s0", "enp2s0"], BondMode.ActiveBackup));
        Assert.Equal("network.interface.not_changeable", Assert.Throws<PithyxException>(() => NetworkValidator.Validate(new NetworkConfig([bond], Global()), _cards, _empty, policy)).Code);
        Assert.Equal("network.global.not_changeable", Assert.Throws<PithyxException>(() => NetworkValidator.Validate(new NetworkConfig([], Global() with { HostName = "other" }), _cards, _empty, policy)).Code);

        var allowed = NetworkValidator.Validate(new NetworkConfig([bond with { Bond = bond.Bond! with { Members = ["enp2s0", "enp3s0"] } }, new InterfaceConfig("v9", InterfaceKind.Vlan, Vlan: new VlanSettings("bond0", 9))], Global()), _cards, _empty, policy);
        Assert.True(policy.Changeable(allowed.Interfaces.Single(item => item.Name == "v9"), allowed));
    }

    [Fact]
    public void ACardThatIsGoneKeepsItsUnchangedSettings()
    {
        var stored = Valid(Static("enp3s0"));
        var cardsNow = _cards.Where(card => card.Name != "enp3s0").ToList();
        NetworkValidator.Validate(stored with { Global = Global() }, cardsNow, stored, NetworkPolicy.Everything);
        var changed = stored with { Interfaces = [stored.Interfaces[0] with { Mtu = 9000 }] };
        Assert.Equal("network.interface.unknown", Assert.Throws<PithyxException>(() => NetworkValidator.Validate(changed, cardsNow, stored, NetworkPolicy.Everything)).Code);
    }
}
