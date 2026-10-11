using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Network;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Network;

/// <summary>The confirmation timer as a state machine, the API in simulation, and the parsers of the host's state.</summary>
public sealed class NetworkApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "network", name));

    [Fact]
    public async Task AChangeRollsBackAfterSixtySecondsUnlessKeptOrUndone()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var confirmation = new NetworkConfirmation(time);
        var reasons = new List<string>();
        Task RollBack(string reason)
        {
            reasons.Add(reason);
            return Task.CompletedTask;
        }

        var first = Guid.NewGuid();
        Assert.True(confirmation.TryStart(first, RollBack));
        Assert.False(confirmation.TryStart(Guid.NewGuid(), RollBack));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(reasons);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["timeout"], reasons);
        Assert.Null(confirmation.Pending);
        Assert.False(confirmation.Confirm(first));

        var kept = Guid.NewGuid();
        Assert.True(confirmation.TryStart(kept, RollBack));
        Assert.True(confirmation.Confirm(kept));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(reasons);

        var undone = Guid.NewGuid();
        Assert.True(confirmation.TryStart(undone, RollBack));
        Assert.True(await confirmation.RevertAsync(undone));
        Assert.Equal(["timeout", "undo"], reasons);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await confirmation.RevertAsync(undone));

        // A deadline that passed while the daemon was down rolls back at once.
        Assert.True(confirmation.TryStart(Guid.NewGuid(), RollBack, time.GetUtcNow() - TimeSpan.FromSeconds(5)));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["timeout", "undo", "timeout"], reasons);
    }

    [Fact]
    public async Task TheApiChecksAppliesWaitsAndRollsBackInSimulation()
    {
        using var factory = new DaemonFactory();
        using var admin = factory.CreateApiClient();
        await admin.SetupAdminAsync();
        var state = await admin.GetFromJsonAsync<JsonElement>("/api/system/network", Token);
        Assert.Equal(60, state.GetProperty("confirmSeconds").GetInt32());
        Assert.Equal(["enp1s0", "enp2s0", "enp3s0"], Names(state));
        Assert.True(state.GetProperty("globalChangeable").GetBoolean());
        // The test client comes in without an address, like loopback, which a simulated box counts as its first card.
        Assert.Equal("enp1s0", state.GetProperty("connectionInterface").GetString());
        var hostName = state.GetProperty("hostName").GetString();

        var config = new
        {
            interfaces = new object[]
            {
                new { name = "bond0", kind = "bond", bond = new { members = new[] { "enp2s0", "enp3s0" }, mode = "activeBackup", primary = "enp2s0" } },
                new { name = "vlan42", kind = "vlan", ipv4 = "static", ipv4Address = "10.42.0.10/24", ipv6 = "off", vlan = new { parent = "bond0", id = 42 } },
                new { name = "enp1s0", kind = "ethernet", ipv4 = "static", ipv4Address = "192.168.77.10/24", ipv4Gateway = "192.168.77.1", ipv6 = "auto", mtu = 9000 },
            },
            global = new { hostName = "nas", dnsServers = new[] { "192.168.77.1" }, searchDomains = new[] { "home.arpa" }, defaultInterface = "enp1s0" },
        };

        using (var check = await admin.PostAsync("/api/system/network/check", config))
        {
            var answer = await check.JsonAsync();
            Assert.True(answer.GetProperty("connectionAffected").GetBoolean());
            Assert.True(answer.GetProperty("hostNameChanged").GetBoolean());
            Assert.Equal(["192.168.77.10", "10.42.0.10"], answer.GetProperty("addresses").EnumerateArray().Select(address => address.GetString()));
        }
        using (var invalid = await admin.PutAsJsonAsync("/api/system/network", new { config.interfaces, global = new { hostName = "-", dnsServers = Array.Empty<string>(), searchDomains = Array.Empty<string>() } }, Token))
        {
            Assert.Equal("network.host_name.invalid", await invalid.ErrorCodeAsync());
        }

        using var changed = await admin.PutAsJsonAsync("/api/system/network", config, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var pending = await changed.JsonAsync();
        var id = pending.GetProperty("id").GetString();
        Assert.Equal("nas", pending.GetProperty("hostName").GetString());
        using (var second = await admin.PutAsJsonAsync("/api/system/network", config, Token))
        {
            Assert.Equal("network.change.pending", await second.ErrorCodeAsync());
        }
        var during = await admin.GetFromJsonAsync<JsonElement>("/api/system/network", Token);
        Assert.Equal(["bond0", "enp1s0", "enp2s0", "enp3s0", "vlan42"], Names(during));
        Assert.Equal("nas", during.GetProperty("hostName").GetString());
        Assert.Equal("bond0", Link(during, "enp2s0").GetProperty("master").GetString());
        Assert.Equal(42, Link(during, "vlan42").GetProperty("vlanId").GetInt32());
        Assert.True(Link(during, "enp1s0").GetProperty("defaultRoute").GetBoolean());
        Assert.Equal(id, during.GetProperty("pending").GetProperty("id").GetString());

        // Sixty seconds without a confirmation: everything before the change is back.
        factory.Time.Advance(TimeSpan.FromSeconds(61));
        var after = await WaitAsync(admin, state => state.GetProperty("pending").ValueKind == JsonValueKind.Null);
        Assert.Equal(["enp1s0", "enp2s0", "enp3s0"], Names(after));
        Assert.Equal(hostName, after.GetProperty("hostName").GetString());
        Assert.Empty(after.GetProperty("config").GetProperty("interfaces").EnumerateArray());
        using (var late = await admin.PostAsync($"/api/system/network/changes/{id}/confirm", new { }))
        {
            Assert.Equal("network.change.not_found", await late.ErrorCodeAsync());
        }

        // Undone at once.
        using (var again = await admin.PutAsJsonAsync("/api/system/network", config, Token))
        using (var undo = await admin.PostAsync($"/api/system/network/changes/{(await again.JsonAsync()).GetProperty("id").GetString()}/revert", new { }))
        {
            Assert.Equal(HttpStatusCode.NoContent, undo.StatusCode);
        }
        Assert.Equal(3, Names(await admin.GetFromJsonAsync<JsonElement>("/api/system/network", Token)).Count);

        // Kept in time, it stays.
        using var kept = await admin.PutAsJsonAsync("/api/system/network", config, Token);
        using (var confirm = await admin.PostAsync($"/api/system/network/changes/{(await kept.JsonAsync()).GetProperty("id").GetString()}/confirm", new { }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        }
        factory.Time.Advance(TimeSpan.FromSeconds(61));
        var stays = await admin.GetFromJsonAsync<JsonElement>("/api/system/network", Token);
        Assert.Equal(5, Names(stays).Count);
        Assert.Equal(JsonValueKind.Null, stays.GetProperty("pending").ValueKind);
        using var same = await admin.PutAsJsonAsync("/api/system/network", config, Token);
        Assert.Equal("network.change.empty", await same.ErrorCodeAsync());
    }

    [Fact]
    public async Task ADaemonThatRestartsWhileAChangeWaitsRollsItBack()
    {
        string directory;
        using (var first = new DaemonFactory { KeepDataDirectory = true })
        {
            directory = first.DataDirectory;
            using var admin = first.CreateApiClient();
            await admin.SetupAdminAsync();
            var hostName = (await admin.GetFromJsonAsync<JsonElement>("/api/system/network", Token)).GetProperty("hostName").GetString();
            using var changed = await admin.PutAsJsonAsync("/api/system/network", new
            {
                interfaces = new[] { new { name = "enp2s0", kind = "ethernet", ipv4 = "static", ipv4Address = "10.0.0.2/24" } },
                global = new { hostName, dnsServers = Array.Empty<string>(), searchDomains = Array.Empty<string>() },
            }, Token);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        using var second = new DaemonFactory { DataDirectory = directory };
        using var client = second.CreateApiClient();
        using (var login = await client.LoginAsync("admin", AuthApi.AdminPassword))
        {
            login.EnsureSuccessStatusCode();
        }
        var state = await client.GetFromJsonAsync<JsonElement>("/api/system/network", Token);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("pending").ValueKind);
        Assert.Empty(state.GetProperty("config").GetProperty("interfaces").EnumerateArray());
    }

    [Fact]
    public void ParsesTheKernelsAndNetworkdsState()
    {
        // Recorded on a dev VM: DHCP through ifupdown gives the default route metric 1002.
        var routes = NetworkParsers.Ipv4DefaultRoutes(Fixture("proc-net-route"));
        Assert.Equal([new DefaultRoute("ens18", IPAddress.Parse("192.168.1.1"), 1002)], routes);
        Assert.Empty(NetworkParsers.Ipv6DefaultRoutes(Fixture("proc-net-ipv6_route")));
        var ipv6 = NetworkParsers.Ipv6DefaultRoutes(Fixture("proc-net-ipv6_route-gateway"));
        Assert.Equal([new DefaultRoute("ptest0", IPAddress.Parse("fd77::1"), 1024)], ipv6);
        Assert.Equal((42, "ptbond0"), NetworkParsers.Vlans(Fixture("proc-net-vlan-config"))["ptvlan42"]);
        var link = NetworkParsers.NetworkdLink(Fixture("networkd-link"));
        Assert.Equal("/etc/systemd/network/10-pithyx-ptest0.network", link["NETWORK_FILE"]);
        Assert.Equal(["10.77.0.1"], NetworkParsers.NetworkdDns(link));
        Assert.Equal("bond", NetworkParsers.DevType("DEVTYPE=bond\nINTERFACE=bond0\nIFINDEX=7\n"));
        Assert.Null(NetworkParsers.DevType("INTERFACE=ens18\nIFINDEX=2\n"));
        Assert.Null(NetworkParsers.Number("-1"));
        Assert.Equal(1000, NetworkParsers.Number("1000\n"));
        Assert.Equal("127.0.0.1\tlocalhost\n127.0.1.1\tnas\n::1\t\tlocalhost\n", NetworkParsers.HostsWithName("127.0.0.1\tlocalhost\n127.0.1.1\tpithyx\n::1\t\tlocalhost\n", "nas"));
        Assert.Equal("127.0.0.1\tlocalhost\n127.0.1.1\tnas\n", NetworkParsers.HostsWithName("127.0.0.1\tlocalhost\n", "nas"));
    }

    private static List<string> Names(JsonElement state) =>
        [.. state.GetProperty("links").EnumerateArray().Select(link => link.GetProperty("name").GetString()!)];

    private static JsonElement Link(JsonElement state, string name) =>
        state.GetProperty("links").EnumerateArray().Single(link => link.GetProperty("name").GetString() == name);

    private static async Task<JsonElement> WaitAsync(HttpClient client, Func<JsonElement, bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = await client.GetFromJsonAsync<JsonElement>("/api/system/network", Token);
            if (condition(state))
            {
                return state;
            }
            await Task.Delay(50, Token);
        }
        throw new TimeoutException("The network state did not change.");
    }
}
