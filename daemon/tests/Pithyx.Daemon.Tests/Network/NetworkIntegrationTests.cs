using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Network;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Tests.Network;

/// <summary>
/// The network with a real systemd-networkd on the dev VM (D337), only through <c>tools/dev-vm/integration.sh
/// --network</c> (<c>PITHYX_NETWORK_INTEGRATION=1</c>, as root). The script builds the network namespace
/// <c>pithyx-nettest</c> with a bridge, a VLAN 42 on it and dnsmasq (DHCPv4 without a router, router advertisements
/// with lifetime 0 and DHCPv6), connects the veth ends <c>ptest0</c> to <c>ptest2</c> listed in
/// <c>/etc/pithyx/test-nics</c>, starts networkd so that it leaves foreign routes and rules alone, and removes all of it
/// afterwards. The management card is never touched: the daemon refuses it, which a test checks too.
/// </summary>
[Trait("Category", Category)]
public sealed class NetworkIntegrationTests : IAsyncLifetime
{
    public const string Category = "NetworkIntegration";
    private const string Bond = "ptbond0";
    private const string Vlan = "ptvlan42";

    private static readonly TimeSpan _settle = TimeSpan.FromSeconds(45);
    private static readonly string[] _testCards = ["ptest0", "ptest1", "ptest2"];

    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-network-").FullName;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly NetworkConfirmation _confirmation;
    private readonly ICommandRunner _commands = new ProcessCommandRunner(NullLogger<ProcessCommandRunner>.Instance);
    private readonly SqliteConnectionFactory _connections;
    private readonly NetworkHost _host;
    private readonly Support.ListLogger<NetworkHost> _hostLog = new();
    private NetworkService _network;

    public NetworkIntegrationTests()
    {
        _confirmation = new NetworkConfirmation(_time);
        _connections = new SqliteConnectionFactory(Path.Combine(_directory, "pithyx.db"));
        _host = new NetworkHost(_commands, DaemonMode.Real, DeploymentEnvironment.Load(DeploymentEnvironment.DefaultDirectory), _hostLog);
        _network = NewService();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private NetworkService NewService() => new(
        _connections,
        _host,
        _confirmation,
        new TopicHub(new TopicRegistry([NetworkService.Definition(() => _network)]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance),
        new FileServiceChanges(),
        _time,
        NullLogger<NetworkService>.Instance);

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("PITHYX_NETWORK_INTEGRATION") != "1")
        {
            return;
        }
        await new MigrationRunner(_connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("PITHYX_NETWORK_INTEGRATION") == "1")
        {
            // Whatever a test left: back to no files of Pithyx's and no bond or VLAN.
            await _network.ResetAsync(CancellationToken.None);
        }
        _network.Dispose();
        _confirmation.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private void RequireEnabled()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("PITHYX_NETWORK_INTEGRATION") == "1",
            "Real network tests run only on the dev VM through tools/dev-vm/integration.sh --network (PITHYX_NETWORK_INTEGRATION=1, as root).");
        Assert.True(_testCards.All(_host.TestNics.Contains), "/etc/pithyx/test-nics must list ptest0 to ptest2.");
    }

    private async Task<NetworkConfig> CurrentAsync() => await _network.ConfigAsync(Token);

    private async Task<PendingNetworkChange> ApplyAsync(params InterfaceConfig[] items)
    {
        var current = await CurrentAsync();
        try
        {
            return await _network.ChangeAsync(new NetworkConfig(items, current.Global), NetworkChangeOrigin.Web, Token);
        }
        catch (PithyxException exception) when (exception.Code == ErrorCodes.NetworkApplyFailed)
        {
            Assert.Fail($"Applying failed: {string.Join(" | ", _hostLog.Messages)}");
            throw;
        }
    }

    private async Task KeepAsync(params InterfaceConfig[] items) => await _network.ConfirmAsync((await ApplyAsync(items)).Id, Token);

    private async Task<NetworkLink> WaitForAsync(string name, Func<NetworkLink, bool> condition)
    {
        var until = DateTime.UtcNow + _settle;
        NetworkLink? link = null;
        while (DateTime.UtcNow < until)
        {
            link = _host.Links().FirstOrDefault(candidate => candidate.Name == name);
            if (link is not null && condition(link))
            {
                return link;
            }
            await Task.Delay(500, Token);
        }
        Assert.Fail($"{name} did not reach the expected state within {_settle.TotalSeconds} s; last seen: {link}");
        return link!;
    }

    private static bool Has(NetworkLink link, string prefix) => link.Addresses.Any(address => address.StartsWith(prefix, StringComparison.Ordinal));

    private static string Sys(string path) => File.ReadAllText(Path.Combine("/sys/class/net", path)).Trim();

    private static async Task<bool> PingAsync(string address)
    {
        using var ping = new Ping();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if ((await ping.SendPingAsync(IPAddress.Parse(address), TimeSpan.FromSeconds(1), null, null, CancellationToken.None)).Status == IPStatus.Success)
            {
                return true;
            }
            await Task.Delay(500, CancellationToken.None);
        }
        return false;
    }

    /// <summary>Copies what the kernel and networkd show now into the fixture folder, when the script asks for it.</summary>
    private static void Record(string name, string source)
    {
        if (Environment.GetEnvironmentVariable("PITHYX_FIXTURE_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.Copy(source, Path.Combine(folder, name), overwrite: true);
        }
    }

    [Fact]
    public async Task TheManagementCardIsNeverChanged()
    {
        RequireEnabled();
        var management = _host.Links().First(link => link.Kind == InterfaceKind.Ethernet && !_host.TestNics.Contains(link.Name));
        var refused = await Assert.ThrowsAsync<PithyxException>(() => ApplyAsync(new InterfaceConfig(management.Name, InterfaceKind.Ethernet, Ipv4: Ipv4Mode.Static, Ipv4Address: "10.99.0.1/24")));
        Assert.Equal(ErrorCodes.NetworkInterfaceNotChangeable, refused.Code);
        var global = await Assert.ThrowsAsync<PithyxException>(async () => await _network.ChangeAsync(new NetworkConfig([], (await CurrentAsync()).Global with { HostName = "other" }), NetworkChangeOrigin.Web, Token));
        Assert.Equal(ErrorCodes.NetworkGlobalNotChangeable, global.Code);
        Assert.Empty(_host.OwnedFiles());
    }

    [Fact]
    public async Task DhcpIpv6StaticAndMtuApplyOnACard()
    {
        RequireEnabled();
        await KeepAsync(new InterfaceConfig("ptest0", InterfaceKind.Ethernet));
        var dhcp = await WaitForAsync("ptest0", link => Has(link, "10.77.0.") && link.Addresses.Count(address => address.StartsWith("fd77:", StringComparison.Ordinal)) >= 2);
        Assert.Equal("dhcp", (await _network.StateAsync(null, Token)).Links.Single(link => link.Name == "ptest0").Method);
        Assert.Contains("10.77.0.1", dhcp.DnsServers);
        Record("networkd-link", $"/run/systemd/netif/links/{Sys("ptest0/ifindex")}");

        await KeepAsync(new InterfaceConfig("ptest0", InterfaceKind.Ethernet,
            Ipv4: Ipv4Mode.Static, Ipv4Address: "10.77.0.50/24", Ipv4Gateway: "10.77.0.1",
            Ipv6: Ipv6Mode.Static, Ipv6Address: "fd77::50/64", Ipv6Gateway: "fd77::1",
            Mtu: 1400));
        // The DHCP lease (10.77.0.100 to .199) is gone once the static address applies; the IPv6 route follows duplicate address detection.
        var static_ = await WaitForAsync("ptest0", link => link.Addresses.Contains("10.77.0.50/24") && link.Addresses.Contains("fd77::50/64") && !Has(link, "10.77.0.1")
            && link.Gateways.Contains("10.77.0.1") && link.Gateways.Contains("fd77::1"));
        Assert.Equal("1400", Sys("ptest0/mtu"));
        Assert.True(static_.DefaultRoute || static_.Gateways.Count == 2);
        Assert.True(await PingAsync("10.77.0.1"));
        Assert.True(await PingAsync("fd77::1"));
        Record("proc-net-ipv6_route-gateway", "/proc/net/ipv6_route");

        // IPv6 off: no IPv6 address at all, not even link-local.
        await KeepAsync(new InterfaceConfig("ptest0", InterfaceKind.Ethernet, Ipv6: Ipv6Mode.Off));
        await WaitForAsync("ptest0", link => Has(link, "10.77.0.") && !link.Addresses.Any(address => address.Contains(':', StringComparison.Ordinal)));
    }

    [Fact]
    public async Task BondsInEveryModeAndAVlanOnTheBondGetTheirAddresses()
    {
        RequireEnabled();
        var bond = new InterfaceConfig(Bond, InterfaceKind.Bond, Ipv6: Ipv6Mode.Off, Bond: new BondSettings(["ptest1", "ptest2"], BondMode.ActiveBackup, "ptest1"));
        await KeepAsync(bond);
        await WaitForAsync(Bond, link => Has(link, "10.77.0."));
        Assert.StartsWith("active-backup", Sys($"{Bond}/bonding/mode"), StringComparison.Ordinal);
        Assert.Equal("ptest1", Sys($"{Bond}/bonding/active_slave"));
        Assert.Equal(Bond, (await _network.StateAsync(null, Token)).Links.Single(link => link.Name == "ptest2").Master);

        foreach (var (mode, kernel) in new[] { (BondMode.Lacp, "802.3ad"), (BondMode.BalanceAlb, "balance-alb") })
        {
            await KeepAsync(bond with { Bond = bond.Bond! with { Mode = mode, Primary = null } });
            await WaitForAsync(Bond, link => Has(link, "10.77.0.") && Sys($"{Bond}/bonding/mode").StartsWith(kernel, StringComparison.Ordinal));
            Assert.Contains("ptest1", Sys($"{Bond}/bonding/slaves"), StringComparison.Ordinal);
        }

        // VLAN 42 on the bond: tagged frames reach the namespace's VLAN, whose dnsmasq hands out 10.77.42.0/24.
        await KeepAsync(
            bond with { Ipv4 = Ipv4Mode.Off, Bond = bond.Bond! with { Mode = BondMode.ActiveBackup } },
            new InterfaceConfig(Vlan, InterfaceKind.Vlan, Ipv6: Ipv6Mode.Off, Vlan: new VlanSettings(Bond, 42)));
        var vlan = await WaitForAsync(Vlan, link => Has(link, "10.77.42."));
        Assert.Equal((Bond, 42), (vlan.VlanParent, vlan.VlanId));
        Assert.True(await PingAsync("10.77.42.1"));
        Record("proc-net-vlan-config", "/proc/net/vlan/config");

        // Without the bond its cards are free again; on the dev VM no other file of networkd configures them.
        await KeepAsync();
        await WaitForAsync("ptest1", link => link.Master is null);
        Assert.False(Directory.Exists($"/sys/class/net/{Bond}"));
        Assert.False(Directory.Exists($"/sys/class/net/{Vlan}"));
    }

    [Fact]
    public async Task AChangeWithoutConfirmationRollsBackAndSoDoesARestart()
    {
        RequireEnabled();
        await KeepAsync(new InterfaceConfig("ptest0", InterfaceKind.Ethernet, Ipv6: Ipv6Mode.Off));
        await WaitForAsync("ptest0", link => Has(link, "10.77.0."));
        var before = _host.OwnedFiles();

        await ApplyAsync(new InterfaceConfig("ptest0", InterfaceKind.Ethernet, Ipv4: Ipv4Mode.Static, Ipv4Address: "10.77.0.99/24", Ipv6: Ipv6Mode.Off));
        await WaitForAsync("ptest0", link => link.Addresses.Contains("10.77.0.99/24"));
        _time.Advance(TimeSpan.FromSeconds(61));
        await WaitForAsync("ptest0", link => Has(link, "10.77.0.") && !link.Addresses.Contains("10.77.0.99/24"));
        Assert.Equal(before, _host.OwnedFiles());
        Assert.Null((await _network.StateAsync(null, Token)).Pending);

        // Undo at once.
        var undone = await ApplyAsync(new InterfaceConfig("ptest0", InterfaceKind.Ethernet, Ipv4: Ipv4Mode.Static, Ipv4Address: "10.77.0.98/24", Ipv6: Ipv6Mode.Off));
        await _network.RevertAsync(undone.Id, Token);
        Assert.Equal(before, _host.OwnedFiles());

        // A daemon that stops while a change waits rolls it back when it starts again.
        await ApplyAsync(new InterfaceConfig("ptest0", InterfaceKind.Ethernet, Ipv4: Ipv4Mode.Static, Ipv4Address: "10.77.0.97/24", Ipv6: Ipv6Mode.Off));
        await WaitForAsync("ptest0", link => link.Addresses.Contains("10.77.0.97/24"));
        _confirmation.Dispose();
        _network.Dispose();
        _network = NewService();
        await _network.RecoverAsync(Token);
        Assert.Equal(before, _host.OwnedFiles());
        await WaitForAsync("ptest0", link => Has(link, "10.77.0.") && !link.Addresses.Contains("10.77.0.97/24"));
    }
}
