using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.ReverseProxy;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.Tests.ReverseProxy;

/// <summary>Settings > Reverse proxy through the API: saving, refusing, certificates per host, and loading after a restart.</summary>
public sealed class ProxyApiTests : IDisposable
{
    private static readonly string[] _media = ["media.home.arpa", "*.media.home.arpa"];
    private static readonly string[] _public = ["media.example.org"];
    private static readonly string[] _mixedCase = ["Media.Home.Arpa"];
    private static readonly string[] _nobody = ["nobody"];
    private static readonly string[] _adminOnly = ["admin"];
    private readonly DaemonFactory _factory = new() { KeepDataDirectory = true };
    private readonly HttpClient _admin;

    public ProxyApiTests() => _admin = _factory.CreateApiClient();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _admin.Dispose();
        var directory = _factory.DataDirectory;
        _factory.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task TheConfigurationIsLoadedAgainAfterARestart()
    {
        await _admin.SetupAdminAsync();
        using (var created = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_media), Token))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var host = (await created.JsonAsync()).GetProperty("hosts")[0];
            Assert.Equal("proxy/media.home.arpa", host.GetProperty("logSource").GetString());
            // The box's authority made the host's certificate for both names.
            Assert.Equal(_media, host.GetProperty("presented").GetProperty("names").EnumerateArray().Select(name => name.GetString()));
        }
        using (var options = await _admin.PutAsJsonAsync("/api/system/proxy/options", new { unknownHosts = "notFound", signInAddress = "https://nas.home.arpa:7484/" }, Token))
        {
            Assert.Equal("https://nas.home.arpa:7484", (await options.JsonAsync()).GetProperty("signInAddress").GetString());
        }

        using var restarted = new DaemonFactory { DataDirectory = _factory.DataDirectory, KeepDataDirectory = true };
        using var client = restarted.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("admin", AuthApi.AdminPassword)).StatusCode);
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/system/proxy", Token);
        var state = restarted.Services.GetRequiredService<ProxyService>().State;

        Assert.Equal("notFound", settings.GetProperty("unknownHosts").GetString());
        Assert.Equal(_media, settings.GetProperty("hosts")[0].GetProperty("domains").EnumerateArray().Select(name => name.GetString()));
        Assert.IsType<HostRoute>(state.Table.Resolve("tv.media.home.arpa"));
        Assert.Equal(UnknownHostAction.NotFound, state.UnknownHosts);
        Assert.NotNull(state.CertificateOf(state.Table.Resolve("media.home.arpa")!));
    }

    [Fact]
    public async Task ANameBelongsToOneHostAndOutsideTheScopeNeedsAnOwnCertificate()
    {
        await _admin.SetupAdminAsync();
        using (await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_media), Token))
        {
        }

        using var taken = await _admin.PostAsJsonAsync("/api/system/proxy/redirects", new { domains = _mixedCase, enabled = true, target = "https://example.org", status = 301, preservePath = false, certificate = "box" }, Token);
        using var outside = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_public), Token);
        using var missing = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_public, certificate: "custom"), Token);

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(ErrorCodes.ProxyDomainTaken, await taken.ErrorCodeAsync());
        Assert.Equal(ErrorCodes.TlsNameNotPermitted, await outside.ErrorCodeAsync());
        Assert.Equal(ErrorCodes.ProxyCertificateMissing, await missing.ErrorCodeAsync());
    }

    [Fact]
    public async Task AnUploadedCertificateIsCheckedKeptAndPresentedForItsNames()
    {
        await _admin.SetupAdminAsync();
        var now = DateTimeOffset.UtcNow;
        var authority = CertificateAuthority.CreateRoot("elsewhere", NameScope.Unrestricted, now);
        var wrong = CertificateAuthority.Issue(authority, ["other.example.org"], 30, CertificateKeyType.Ecdsa, now);
        var right = CertificateAuthority.Issue(authority, ["media.example.org"], 30, CertificateKeyType.Rsa, now);

        using (var mismatch = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_public, "custom", wrong.CertificatePem, wrong.PrivateKeyPem), Token))
        {
            Assert.Equal(ErrorCodes.ProxyCertificateNameMismatch, await mismatch.ErrorCodeAsync());
        }
        long id;
        using (var created = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_public, "custom", right.CertificatePem, right.PrivateKeyPem), Token))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var host = (await created.JsonAsync()).GetProperty("hosts")[0];
            id = host.GetProperty("id").GetInt64();
            Assert.Equal(CertificateAuthority.Fingerprint(right.Certificate), host.GetProperty("presented").GetProperty("fingerprint").GetString());
        }
        // Saving again without the files keeps them.
        using (var saved = await _admin.PutAsJsonAsync($"/api/system/proxy/hosts/{id}", Host(_public, "custom"), Token))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var tls = Path.Combine(_factory.DataDirectory, "tls");
        Assert.True(File.Exists(Path.Combine(tls, $"proxy-host-{id}.pem")));

        var proxy = _factory.Services.GetRequiredService<ProxyService>();
        Assert.Equal(CertificateAuthority.Fingerprint(right.Certificate), CertificateAuthority.Fingerprint(proxy.ServerOptions("MEDIA.example.org").ServerCertificateContext!.TargetCertificate));
        // Any other name gets the box's own certificate.
        Assert.Same(_factory.Services.GetRequiredService<BoxCertificates>().ServerOptions, proxy.ServerOptions("nas.home.arpa"));

        using (await _admin.DeleteAsync($"/api/system/proxy/hosts/{id}", Token))
        {
        }
        Assert.False(File.Exists(Path.Combine(tls, $"proxy-host-{id}.pem")));
    }

    [Fact]
    public async Task ATargetOnTheBoxNeedsAConfirmationAndItsProxyPortsNever()
    {
        await _admin.SetupAdminAsync();

        using var unconfirmed = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_media, target: new { scheme = "https", host = "localhost", port = 7484, tls = "box", onBox = false }), Token);
        using var confirmed = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_media, target: new { scheme = "https", host = "localhost", port = 7484, tls = "box", onBox = true }), Token);
        using var loop = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_public, target: new { scheme = "http", host = "127.0.0.1", port = 80, tls = "system", onBox = true }), Token);

        Assert.Equal(ErrorCodes.ProxyTargetOnBox, await unconfirmed.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(ErrorCodes.ProxyTargetLoop, await loop.ErrorCodeAsync());
    }

    [Fact]
    public async Task SignInNamesMustExist()
    {
        await _admin.SetupAdminAsync();

        using var user = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_media, signIn: new { required = true, users = _nobody, groups = Array.Empty<string>() }), Token);
        using var group = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(_media, signIn: new { required = true, users = _adminOnly, groups = _nobody }), Token);

        Assert.Equal(ErrorCodes.ProxySignInUserNotFound, await user.ErrorCodeAsync());
        Assert.Equal(ErrorCodes.ProxySignInGroupNotFound, await group.ErrorCodeAsync());
    }

    private static Dictionary<string, object?> Host(string[] domains, string certificate = "box", string? certificatePem = null, string? privateKeyPem = null, object? target = null, object? signIn = null) => new()
    {
        ["domains"] = domains,
        ["enabled"] = true,
        ["target"] = target ?? new { scheme = "http", host = "jellyfin.lan", port = 8096, tls = "system", onBox = false },
        ["webSockets"] = true,
        ["http2"] = true,
        ["certificate"] = certificate,
        ["forceHttps"] = true,
        ["hsts"] = false,
        ["forwardedHeaders"] = true,
        ["preserveHost"] = false,
        ["hostHeader"] = null,
        ["requestHeaders"] = Array.Empty<object>(),
        ["responseHeaders"] = Array.Empty<object>(),
        ["maxBodyMegabytes"] = 512,
        ["connectTimeoutSeconds"] = 10,
        ["responseTimeoutSeconds"] = 100,
        ["allowAddresses"] = Array.Empty<string>(),
        ["denyAddresses"] = Array.Empty<string>(),
        ["basicAuth"] = Array.Empty<object>(),
        ["signIn"] = signIn ?? new { required = false, users = Array.Empty<string>(), groups = Array.Empty<string>() },
        ["accessLog"] = true,
        ["certificatePem"] = certificatePem,
        ["privateKeyPem"] = privateKeyPem,
    };
}
