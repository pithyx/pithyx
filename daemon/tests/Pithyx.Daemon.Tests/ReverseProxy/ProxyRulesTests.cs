using System.Net;
using Microsoft.AspNetCore.Http;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.ReverseProxy;
using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.Tests.ReverseProxy;

/// <summary>The reverse proxy's validation, routing decisions and access rules, without a server.</summary>
public sealed class ProxyRulesTests
{
    private static readonly BoxIdentity _box = new("pithyx", [IPAddress.Parse("192.168.1.10"), IPAddress.Parse("fd00::10")], 7484, [80, 443]);

    [Fact]
    public void DomainsAreLowerCaseUniqueNamesWithOptionalWildcards()
    {
        Assert.Equal(["media.home.arpa", "*.apps.lan"], ProxyValidator.Domains(["Media.Home.Arpa.", "*.apps.lan", "media.home.arpa"]));

        Assert.Equal(ErrorCodes.ProxyDomainsMissing, Code(() => ProxyValidator.Domains([" "])));
        Assert.Equal(ErrorCodes.ProxyDomainInvalid, Code(() => ProxyValidator.Domains(["192.168.1.10"])));
        Assert.Equal(ErrorCodes.ProxyDomainInvalid, Code(() => ProxyValidator.Domains(["*.lan"])));
        Assert.Equal(ErrorCodes.ProxyDomainInvalid, Code(() => ProxyValidator.Domains(["bad_name.lan"])));
        Assert.Equal(ErrorCodes.ProxyDomainsTooMany, Code(() => ProxyValidator.Domains([.. Enumerable.Range(0, 51).Select(index => $"h{index}.lan")])));
    }

    [Theory]
    [InlineData("localhost", 80)]
    [InlineData("127.0.0.1", 443)]
    [InlineData("192.168.1.10", 443)]
    [InlineData("pithyx.local", 80)]
    [InlineData("[::1]", 443)]
    public void ATargetOnTheProxysOwnPortsIsALoopEvenWhenConfirmed(string host, int port) =>
        Assert.Equal(ErrorCodes.ProxyTargetLoop, Code(() => ProxyValidator.Target(new ProxyTarget("http", host, port, ProxyTargetTls.System, OnBox: true), _box)));

    [Fact]
    public void AnotherServiceOfTheBoxNeedsAConfirmation()
    {
        Assert.Equal(ErrorCodes.ProxyTargetOnBox, Code(() => ProxyValidator.Target(new ProxyTarget("https", "fd00::10", 7484, ProxyTargetTls.Box, OnBox: false), _box)));

        var confirmed = ProxyValidator.Target(new ProxyTarget("https", "[fd00::10]", 7484, ProxyTargetTls.Box, OnBox: true), _box);
        var elsewhere = ProxyValidator.Target(new ProxyTarget("http", "Jellyfin.LAN", 8096, ProxyTargetTls.None, OnBox: false), _box);

        Assert.Equal("fd00::10", confirmed.Host);
        Assert.Equal("jellyfin.lan", elsewhere.Host);
        // Plain HTTP has no certificate to check.
        Assert.Equal(ProxyTargetTls.System, elsewhere.Tls);
        Assert.Equal(ErrorCodes.ProxyTargetInvalid, Code(() => ProxyValidator.Target(new ProxyTarget("ftp", "nas.lan", 21, ProxyTargetTls.System, false), _box)));
        Assert.Equal(ErrorCodes.ProxyTargetInvalid, Code(() => ProxyValidator.Target(new ProxyTarget("http", "*.lan", 80, ProxyTargetTls.System, false), _box)));
        Assert.Equal(ErrorCodes.ProxyLimitOutOfRange, Code(() => ProxyValidator.Target(new ProxyTarget("http", "nas.lan", 70000, ProxyTargetTls.System, false), _box)));
    }

    [Fact]
    public void HeadersTheProxyControlsCannotBeSet()
    {
        Assert.Equal([new ProxyHeader("X-Frame-Options", "SAMEORIGIN")], ProxyValidator.Headers([new("X-Frame-Options", " SAMEORIGIN ")]));

        foreach (var name in new[] { "Host", "X-Pithyx-User", "X-Forwarded-For", "Connection", "Strict-Transport-Security", "Bad Name" })
        {
            Assert.Equal(ErrorCodes.ProxyHeaderInvalid, Code(() => ProxyValidator.Headers([new(name, "x")])));
        }
        Assert.Equal(ErrorCodes.ProxyHeaderInvalid, Code(() => ProxyValidator.Headers([new("X-A", "one\r\nX-B: two")])));
        Assert.Equal(ErrorCodes.ProxyHeaderInvalid, Code(() => ProxyValidator.Headers([new("X-A", "1"), new("x-a", "2")])));
    }

    [Fact]
    public void AddressesAreSingleAddressesOrRanges()
    {
        Assert.Equal(["192.168.1.0/24", "10.0.0.5/32", "fd00::/8"], ProxyValidator.Addresses(["192.168.1.0/24", " 10.0.0.5 ", "fd00::/8"]));
        Assert.Equal(ErrorCodes.ProxyAddressInvalid, Code(() => ProxyValidator.Addresses(["10.0.0.0/33"])));
        Assert.Equal(ErrorCodes.ProxyAddressInvalid, Code(() => ProxyValidator.Addresses(["nas.lan"])));
    }

    [Fact]
    public void ANewBasicAuthUserNeedsAPassword()
    {
        Assert.Single(ProxyValidator.BasicAuth([new("anna")], new HashSet<string>(["anna"], StringComparer.Ordinal)));

        Assert.Equal(ErrorCodes.ProxyBasicAuthPasswordMissing, Code(() => ProxyValidator.BasicAuth([new("ben")], new HashSet<string>(StringComparer.Ordinal))));
        Assert.Equal(ErrorCodes.ProxyBasicAuthInvalid, Code(() => ProxyValidator.BasicAuth([new("a:b", "x")], new HashSet<string>(StringComparer.Ordinal))));
        Assert.Equal(ErrorCodes.ProxyBasicAuthInvalid, Code(() => ProxyValidator.BasicAuth([new("anna", "x"), new("anna", "y")], new HashSet<string>(StringComparer.Ordinal))));
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(445)]
    [InlineData(7484)]
    [InlineData(50000)]
    [InlineData(50099)]
    public void StreamsStayOffTheBoxsOwnPorts(int port) =>
        Assert.Equal(ErrorCodes.ProxyStreamPortReserved, Code(() => ProxyValidator.Stream(Stream(port), _box)));

    [Fact]
    public void StreamsBelow1024NeedAConfirmationAndAProtocol()
    {
        Assert.Equal(ErrorCodes.ProxyStreamPortPrivileged, Code(() => ProxyValidator.Stream(Stream(25), _box)));
        Assert.Equal(25, ProxyValidator.Stream(Stream(25) with { PrivilegedPort = true }, _box).Port);
        Assert.Equal(ErrorCodes.ProxyStreamProtocolMissing, Code(() => ProxyValidator.Stream(Stream(25565) with { Tcp = false, Udp = false }, _box)));
        Assert.Equal(ErrorCodes.ProxyTargetLoop, Code(() => ProxyValidator.Stream(Stream(25565) with { TargetHost = "localhost", TargetPort = 25565 }, _box)));
        Assert.Equal("minecraft.lan", ProxyValidator.Stream(Stream(25565), _box).TargetHost);
    }

    [Fact]
    public void RedirectTargetsAndTheSignInAddressAreAbsolute()
    {
        Assert.Equal("https://new.example.org/base", ProxyValidator.RedirectTarget(" https://new.example.org/base "));
        Assert.Equal(ErrorCodes.ProxyRedirectTargetInvalid, Code(() => ProxyValidator.RedirectTarget("new.example.org")));
        Assert.Equal(ErrorCodes.ProxyRedirectTargetInvalid, Code(() => ProxyValidator.RedirectTarget("https://user:pw@new.example.org")));
        Assert.Equal(ErrorCodes.ProxyRedirectStatusInvalid, Code(() => ProxyValidator.RedirectStatus(200)));
        Assert.Equal("https://nas.home.arpa:7484", ProxyValidator.SignInAddress("https://nas.home.arpa:7484/"));
        Assert.Null(ProxyValidator.SignInAddress(" "));
        Assert.Equal(ErrorCodes.ProxySignInAddressInvalid, Code(() => ProxyValidator.SignInAddress("https://nas.home.arpa/login")));
    }

    [Fact]
    public void AnExactNameWinsOverAWildcardOfOneLabel()
    {
        var media = Host(1, "media.lan");
        var apps = Host(2, "*.lan");
        var disabled = Host(3, "old.lan") with { Enabled = false };
        var redirect = new ProxyRedirectConfig(4, ["www.lan"], true, "https://lan.example", 301, true, ProxyCertificateSource.Box, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var table = new ProxyTable([media, apps, disabled], [redirect]);

        Assert.Equal(media, Assert.IsType<HostRoute>(table.Resolve("MEDIA.lan.")).Host);
        Assert.Equal(apps, Assert.IsType<HostRoute>(table.Resolve("photos.lan")).Host);
        // A disabled host's name is unknown, even where a wildcard would cover it.
        Assert.Equal(apps, Assert.IsType<HostRoute>(table.Resolve("old.lan")).Host);
        Assert.IsType<RedirectRoute>(table.Resolve("www.lan"));
        Assert.Null(table.Resolve("a.b.lan"));
        Assert.Null(table.Resolve("lan"));
        Assert.Null(table.Resolve(null));
    }

    [Fact]
    public void TheDecisionFollowsTheHostThePortAndTheChoiceForUnknownNames()
    {
        var host = new HostRoute(Host(1, "media.lan"));
        var lax = new HostRoute(Host(2, "lax.lan") with { ForceHttps = false });
        var redirect = new RedirectRoute(new ProxyRedirectConfig(3, ["www.lan"], true, "https://x", 302, false, ProxyCertificateSource.Box, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        Assert.Equal(ProxyAction.RedirectToHttps, ProxyTable.Decide(host, tls: false, tlsAvailable: true, UnknownHostAction.Redirect, "/"));
        Assert.Equal(ProxyAction.Forward, ProxyTable.Decide(host, tls: false, tlsAvailable: false, UnknownHostAction.Redirect, "/"));
        Assert.Equal(ProxyAction.Forward, ProxyTable.Decide(host, tls: true, tlsAvailable: true, UnknownHostAction.Redirect, "/"));
        Assert.Equal(ProxyAction.Forward, ProxyTable.Decide(lax, tls: false, tlsAvailable: true, UnknownHostAction.Redirect, "/"));
        Assert.Equal(ProxyAction.Redirect, ProxyTable.Decide(redirect, tls: false, tlsAvailable: true, UnknownHostAction.NotFound, "/"));
        Assert.Equal(ProxyAction.Next, ProxyTable.Decide(null, tls: true, tlsAvailable: true, UnknownHostAction.Redirect, "/"));
        Assert.Equal(ProxyAction.NotFound, ProxyTable.Decide(null, tls: true, tlsAvailable: true, UnknownHostAction.NotFound, "/"));
        // The installer finds a rebooted box by /api/install-id on port 80, whatever is chosen for unknown names.
        Assert.Equal(ProxyAction.Next, ProxyTable.Decide(null, tls: false, tlsAvailable: true, UnknownHostAction.NotFound, "/api/install-id"));
        Assert.Equal(ProxyAction.NotFound, ProxyTable.Decide(null, tls: true, tlsAvailable: true, UnknownHostAction.NotFound, "/api/install-id"));
    }

    [Theory]
    [InlineData(true, "/a/b", "?c=1", "https://new.example.org/base/a/b?c=1")]
    [InlineData(true, "", "", "https://new.example.org/base/")]
    [InlineData(false, "/a/b", "?c=1", "https://new.example.org/base")]
    public void ARedirectKeepsThePathWhenAsked(bool preserve, string path, string query, string expected)
    {
        var redirect = new ProxyRedirectConfig(1, ["old.lan"], true, "https://new.example.org/base", 308, preserve, ProxyCertificateSource.Box, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, ProxyTable.RedirectLocation(redirect, new PathString(path), new QueryString(query)));
    }

    [Fact]
    public void DeniedAddressesNeverGetInAndAllowedOnesAreTheOnlyOnes()
    {
        IReadOnlyList<IPNetwork> allow = [IPNetwork.Parse("192.168.1.0/24")];
        IReadOnlyList<IPNetwork> deny = [IPNetwork.Parse("192.168.1.66/32")];

        Assert.True(HostProxy.AddressAllowed(IPAddress.Parse("192.168.1.5").MapToIPv6(), allow, deny));
        Assert.False(HostProxy.AddressAllowed(IPAddress.Parse("192.168.1.66"), allow, deny));
        Assert.False(HostProxy.AddressAllowed(IPAddress.Parse("10.0.0.1"), allow, deny));
        Assert.True(HostProxy.AddressAllowed(IPAddress.Parse("10.0.0.1"), [], deny));
    }

    [Fact]
    public void TheBoxsCookiesNeverReachATarget()
    {
        Assert.Equal("app=1; theme=dark", HostProxy.WithoutBoxCookies("pithyx_session=a; app=1; pithyx_proxy=b; theme=dark"));
        Assert.Null(HostProxy.WithoutBoxCookies("pithyx_proxy=b"));
    }

    [Fact]
    public void TheSignInLetsInAdministratorsListedUsersAndGroupMembers()
    {
        var family = new ProxySignInRule(true, ["anna"], ["family"]);
        var everybody = new ProxySignInRule(true, [], []);

        Assert.True(ProxySignIn.Allows(family, User("root", admin: true)));
        Assert.True(ProxySignIn.Allows(family, User("anna")));
        Assert.True(ProxySignIn.Allows(family, User("ben", "family")));
        Assert.False(ProxySignIn.Allows(family, User("carl", "friends")));
        Assert.True(ProxySignIn.Allows(everybody, User("carl")));
    }

    [Theory]
    [InlineData("/x?y=1", "/x?y=1")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("", "/")]
    public void TheSignInOnlyReturnsToAPathOnTheSameHost(string to, string expected) =>
        Assert.Equal(expected, ProxySignIn.SafePath(to));

    [Fact]
    public void AnUploadedCertificateMustCoverEveryDomain()
    {
        var now = DateTimeOffset.UtcNow;
        var root = CertificateAuthority.CreateRoot("pithyx", NameScope.Unrestricted, now);
        using var wildcard = CertificateAuthority.Issue(root, ["*.media.lan", "media.lan"], 30, CertificateKeyType.Ecdsa, now).Certificate;

        Assert.True(ProxyService.CoversName(wildcard, "media.lan"));
        Assert.True(ProxyService.CoversName(wildcard, "tv.media.lan"));
        Assert.True(ProxyService.CoversName(wildcard, "*.media.lan"));
        Assert.False(ProxyService.CoversName(wildcard, "a.tv.media.lan"));
        Assert.False(ProxyService.CoversName(wildcard, "other.lan"));
    }

    private static ProxyStreamRequest Stream(int port) => new("Game", true, port, Tcp: true, Udp: false, "Minecraft.lan", 25565);

    internal static ProxyHostConfig Host(long id, params string[] domains) => new(
        id, domains, true, new ProxyTarget("http", "backend.lan", 8080, ProxyTargetTls.System, false), true, true,
        ProxyCertificateSource.Box, ForceHttps: true, Hsts: false, ForwardedHeaders: true, PreserveHost: false, HostHeader: null, [], [],
        null, 10, 100, [], [], [], new ProxySignInRule(false, [], []), AccessLog: true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static UserInfo User(string name, params string[] groups) => User(name, admin: false, groups);

    private static UserInfo User(string name, bool admin, params string[] groups) => new(
        1, name, null, admin, false, new SessionPermissions(admin, [], [], []), [.. groups.Select((group, index) => new GroupRef(index, group))]);

    private static string Code(Action action) => Assert.Throws<PithyxException>(action).Code;
}
