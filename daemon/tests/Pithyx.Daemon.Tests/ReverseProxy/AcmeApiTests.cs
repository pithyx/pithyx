using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.ReverseProxy;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.ReverseProxy;

/// <summary>
/// Certificates from an ACME authority for proxy hosts (D378), on a real Kestrel with a plain proxy port. No test talks
/// to a real authority: the directory is an address where nothing answers, so every request ends unreachable.
/// </summary>
public sealed class AcmeApiTests : IAsyncLifetime
{
    private const string Nowhere = "https://127.0.0.1:1/directory";
    private static readonly string[] _nas = ["nas.example.com"];
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly int _web = FreePort();
    private readonly int _plain = FreePort();
    private AcmeDaemon _daemon = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _daemon = new AcmeDaemon(_web, _plain);
        _daemon.UseKestrel();
        _daemon.StartServer();
        _admin = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_web}") };
        _admin.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _daemon.DisposeAsync();
    }

    // An agreement needs the authority's terms from its directory; the tests write it as if one had been made.
    private async Task AgreeAsync()
    {
        await using var connection = await _daemon.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value)",
            new { key = AcmeService.SettingsKey, value = JsonSerializer.Serialize(new AcmeSettings(Nowhere, null, "https://127.0.0.1:1/terms.pdf"), _json) });
    }

    private static async Task<(HttpStatusCode Status, string Code)> RefusedAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? "" : await response.ErrorCodeAsync());
    }

    private Task<HttpResponseMessage> CreateAsync(string[] domains, string certificate = "acme") =>
        _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(domains, certificate), Token);

    private async Task<JsonElement> AcmeOfFirstHostAsync() =>
        (await _admin.GetFromJsonAsync<JsonElement>("/api/system/proxy", Token)).GetProperty("hosts")[0].GetProperty("acme");

    [Fact]
    public async Task AHostUsesTheAuthorityOnlyForPublicNamesAfterTheTermsWereAgreed()
    {
        Assert.Equal((HttpStatusCode.Conflict, "proxy.acme.terms_required"), await RefusedAsync(CreateAsync(_nas)));
        await AgreeAsync();
        Assert.Equal((HttpStatusCode.BadRequest, "proxy.acme.wildcard"), await RefusedAsync(CreateAsync(["*.example.com"])));
        Assert.Equal((HttpStatusCode.BadRequest, "proxy.acme.name_not_public"), await RefusedAsync(CreateAsync(["nas.home.arpa"])));
        Assert.Equal((HttpStatusCode.OK, ""), await RefusedAsync(CreateAsync(_nas)));
    }

    [Fact]
    public async Task AnUnreachableAuthorityFailsTheRequestAndTheBoxWaitsLongerEachTime()
    {
        await AgreeAsync();
        using (var created = await CreateAsync(_nas))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        JsonElement acme = default;
        await JobTestSupport.WaitUntilAsync(async () => (acme = await AcmeOfFirstHostAsync()).GetProperty("status").GetString() == "failed", "the first request to fail");
        var first = acme.GetProperty("attemptedAt").GetDateTimeOffset();
        Assert.Equal("proxy.acme.unreachable", acme.GetProperty("errorCode").GetString());
        Assert.Equal(first.AddHours(1), acme.GetProperty("retryAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, acme.GetProperty("notAfter").ValueKind);

        var id = (await _admin.GetFromJsonAsync<JsonElement>("/api/system/proxy", Token)).GetProperty("hosts")[0].GetProperty("id").GetInt64();
        using var renewed = await _admin.PostAsync(string.Create(CultureInfo.InvariantCulture, $"/api/system/proxy/hosts/{id}/certificate"), null, Token);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var job = await renewed.Content.ReadFromJsonAsync<JsonElement>(Token);
        await _daemon.Services.GetRequiredService<Pithyx.Daemon.Jobs.JobService>().WaitForStateAsync(job.GetProperty("id").GetInt64(), Pithyx.Daemon.Jobs.JobState.Failed);

        acme = await AcmeOfFirstHostAsync();
        Assert.Equal(acme.GetProperty("attemptedAt").GetDateTimeOffset().AddHours(2), acme.GetProperty("retryAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task OnlyAHostWithACertificateFromTheAuthorityIsRenewed()
    {
        using (var created = await CreateAsync(["media.home.arpa"], "box"))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }
        var id = (await _admin.GetFromJsonAsync<JsonElement>("/api/system/proxy", Token)).GetProperty("hosts")[0].GetProperty("id").GetInt64();

        Assert.Equal(
            (HttpStatusCode.Conflict, "proxy.acme.not_acme"),
            await RefusedAsync(_admin.PostAsync(string.Create(CultureInfo.InvariantCulture, $"/api/system/proxy/hosts/{id}/certificate"), null, Token)));
        Assert.Equal(JsonValueKind.Null, (await AcmeOfFirstHostAsync()).ValueKind);
    }

    [Fact]
    public async Task TheAccountRefusesAnInsecureDirectoryAndAnAgreementWithoutTerms()
    {
        Assert.Equal((HttpStatusCode.BadRequest, "proxy.acme.directory_invalid"), await RefusedAsync(_admin.PutAsJsonAsync("/api/system/proxy/acme", new { directory = "http://ca.example/directory" }, Token)));
        Assert.Equal((HttpStatusCode.BadRequest, "proxy.acme.email_invalid"), await RefusedAsync(_admin.PutAsJsonAsync("/api/system/proxy/acme", new { directory = Nowhere, email = "nobody" }, Token)));
        Assert.Equal((HttpStatusCode.BadGateway, "proxy.acme.unreachable"), await RefusedAsync(_admin.PutAsJsonAsync("/api/system/proxy/acme", new { directory = Nowhere, agreeToTerms = true }, Token)));

        using var saved = await _admin.PutAsJsonAsync("/api/system/proxy/acme", new { directory = Nowhere, email = "admin@example.com" }, Token);
        var account = await saved.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(Nowhere, account.GetProperty("directory").GetString());
        Assert.Equal("admin@example.com", account.GetProperty("email").GetString());
        Assert.False(account.GetProperty("termsAgreed").GetBoolean());
        Assert.False(account.GetProperty("reachable").GetBoolean());
    }

    [Fact]
    public async Task PortEightyAnswersTheAuthoritysCheckForAnyName()
    {
        _daemon.Services.GetRequiredService<AcmeRuntime>().Offer("token-1", "token-1.thumbprint");
        using var plain = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_plain}") };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/acme-challenge/token-1");
        request.Headers.Host = "nas.example.com";
        using var unknown = new HttpRequestMessage(HttpMethod.Get, "/.well-known/acme-challenge/token-2");
        unknown.Headers.Host = "nas.example.com";

        using var answer = await plain.SendAsync(request, Token);
        using var other = await plain.SendAsync(unknown, Token);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("token-1.thumbprint", await answer.Content.ReadAsStringAsync(Token));
        Assert.NotEqual(HttpStatusCode.OK, other.StatusCode);
    }

    private static Dictionary<string, object?> Host(string[] domains, string certificate) => new()
    {
        ["domains"] = domains,
        ["enabled"] = true,
        ["target"] = new { scheme = "http", host = "jellyfin.lan", port = 8096, tls = "system", onBox = false },
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
        ["signIn"] = new { required = false, users = Array.Empty<string>(), groups = Array.Empty<string>() },
        ["accessLog"] = true,
    };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The daemon on Kestrel with its web port and a plain proxy port, as port 80 of a box.</summary>
    private sealed class AcmeDaemon(int web, int plain) : DaemonFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PITHYX_DEV_PORT", web.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("PITHYX_DEV_PROXY_PORTS", plain.ToString(CultureInfo.InvariantCulture));
        }
    }
}
