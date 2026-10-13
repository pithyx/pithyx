using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Network;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Network;

public sealed class FirewallTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public FirewallTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DropsByDefaultAndOpensOnlyTheWebPortsWithoutFileServices()
    {
        var rules = Firewall.Render(new FirewallPlan(Smb: false, Ssh: false, Ftp: false, PrivateNetworksOnly: false));

        Assert.Equal(
            """
            add table inet pithyx
            add chain inet pithyx input { type filter hook input priority filter; policy drop; }
            flush chain inet pithyx input
            add rule inet pithyx input iif "lo" accept
            add rule inet pithyx input ct state established,related accept
            add rule inet pithyx input icmp type { echo-request, destination-unreachable, time-exceeded, parameter-problem } accept
            add rule inet pithyx input icmpv6 type { destination-unreachable, packet-too-big, time-exceeded, parameter-problem, echo-request, nd-router-advert, nd-neighbor-solicit, nd-neighbor-advert, nd-redirect, mld-listener-query } accept
            add rule inet pithyx input udp sport 67 udp dport 68 accept
            add rule inet pithyx input ip6 saddr fe80::/10 udp sport 547 udp dport 546 accept
            add rule inet pithyx input tcp dport { 80, 443, 7484 } accept

            """,
            rules);
        Assert.DoesNotContain("apps-input", rules, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, false, "tcp dport { 445 } accept")]
    [InlineData(false, true, false, "tcp dport { 22 } accept")]
    [InlineData(false, false, true, "tcp dport { 21, 50000-50099 } accept")]
    [InlineData(true, true, true, "tcp dport { 21, 22, 445, 50000-50099 } accept")]
    public void OpensTheSwitchedOnFileServicesToEveryNetwork(bool smb, bool ssh, bool ftp, string expected)
    {
        var lines = Firewall.Render(new FirewallPlan(smb, ssh, ftp, PrivateNetworksOnly: false)).Split('\n');

        Assert.Equal($"add rule inet pithyx input {expected}", lines[^2]);
    }

    [Fact]
    public void LimitsTheFileServicesToPrivateNetworks()
    {
        var lines = Firewall.Render(new FirewallPlan(Smb: true, Ssh: true, Ftp: false, PrivateNetworksOnly: true)).Split('\n');

        Assert.Equal("add rule inet pithyx input ip saddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } tcp dport { 22, 445 } accept", lines[^3]);
        Assert.Equal("add rule inet pithyx input ip6 saddr { fc00::/7, fe80::/10 } tcp dport { 22, 445 } accept", lines[^2]);
        // The web interface stays reachable from everywhere.
        Assert.Contains("add rule inet pithyx input tcp dport { 80, 443, 7484 } accept", lines);
    }

    [Fact]
    public async Task SwitchingAServiceRewritesTheRules()
    {
        await _admin.SetupAdminAsync();
        var firewall = _factory.Services.GetRequiredService<Firewall>();

        var initial = await _admin.GetFromJsonAsync<JsonElement>("/api/file-services", Token);
        Assert.False(initial.GetProperty("privateNetworksOnly").GetBoolean());
        Assert.False(initial.GetProperty("firewallActive").GetBoolean());
        // SMB is on from the first boot.
        Assert.Equal(new FirewallPlan(Smb: true, Ssh: false, Ftp: false, PrivateNetworksOnly: false), await firewall.WantedAsync(Token));

        using (var changed = await _admin.PutAsJsonAsync("/api/file-services", new { smbEnabled = false, sftpEnabled = true, ftpEnabled = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        Assert.Contains("tcp dport { 21, 22, 50000-50099 } accept", firewall.Rules, StringComparison.Ordinal);
        Assert.DoesNotContain("445", firewall.Rules, StringComparison.Ordinal);

        // SSH logins alone keep port 22 open.
        using (await _admin.PutAsJsonAsync("/api/file-services", new { sftpEnabled = false, sshEnabled = true, ftpEnabled = false }, Token))
        {
        }
        Assert.Equal(new FirewallPlan(Smb: false, Ssh: true, Ftp: false, PrivateNetworksOnly: false), await firewall.WantedAsync(Token));

        using var limited = await _admin.PutAsJsonAsync("/api/file-services", new { privateNetworksOnly = true }, Token);
        Assert.True((await limited.JsonAsync()).GetProperty("privateNetworksOnly").GetBoolean());
        Assert.Contains("ip6 saddr { fc00::/7, fe80::/10 } tcp dport { 22 } accept", firewall.Rules, StringComparison.Ordinal);
    }
}
