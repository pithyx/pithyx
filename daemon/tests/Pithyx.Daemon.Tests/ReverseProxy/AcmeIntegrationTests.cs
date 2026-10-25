using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.ReverseProxy;

/// <summary>
/// A real ACME flow against Pebble, the test authority of Let's Encrypt (D378): the account, the order, the HTTP-01
/// check of the box's plain proxy port and the certificate. Only <c>tools/dev-vm/acme-pebble.sh</c> runs it, which
/// starts Pebble with a DNS that answers 127.0.0.1 for every name; GitHub CI never does.
/// </summary>
[Trait("Category", "AcmeIntegration")]
public sealed class AcmeIntegrationTests : IAsyncLifetime
{
    private static readonly string? _directory = Environment.GetEnvironmentVariable("PITHYX_PEBBLE_DIRECTORY");
    private static readonly int _httpPort = int.Parse(Environment.GetEnvironmentVariable("PITHYX_PEBBLE_HTTP_PORT") ?? "5002", CultureInfo.InvariantCulture);
    private static readonly string[] _domains = ["nas.example.com", "www.example.com"];
    private PebbleDaemon _daemon = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(_directory is null, "PITHYX_PEBBLE_DIRECTORY is not set; run tools/dev-vm/acme-pebble.sh.");
        var web = _httpPort + 1;
        _daemon = new PebbleDaemon(web, _httpPort);
        _daemon.UseKestrel();
        _daemon.StartServer();
        _admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{web}") };
        _admin.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin?.Dispose();
        if (_daemon is not null)
        {
            await _daemon.DisposeAsync();
        }
    }

    [Fact]
    public async Task AHostGetsACertificateForAllItsNamesFromTheAuthority()
    {
        using (var agreed = await _admin.PutAsJsonAsync("/api/system/proxy/acme", new { directory = _directory, email = "admin@example.com", agreeToTerms = true }, Token))
        {
            Assert.True(agreed.StatusCode == HttpStatusCode.OK, await agreed.Content.ReadAsStringAsync(Token));
            Assert.True((await agreed.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("termsAgreed").GetBoolean());
        }
        using (var created = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(), Token))
        {
            Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Token));
        }

        JsonElement host = default;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while ((host = (await _admin.GetFromJsonAsync<JsonElement>("/api/system/proxy", Token)).GetProperty("hosts")[0])
            .GetProperty("acme").GetProperty("status").GetString() == "pending" || host.GetProperty("acme").GetProperty("running").GetBoolean())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), "Pebble issued no certificate within two minutes.");
            // The daemon's clock is fake; its waits between the checks of an authorization move only with it.
            _daemon.Time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(500, Token);
        }

        var acme = host.GetProperty("acme");
        Assert.True(acme.GetProperty("status").GetString() == "valid", acme.ToString());
        var presented = host.GetProperty("presented");
        Assert.Contains("Pebble", presented.GetProperty("issuer").GetString(), StringComparison.Ordinal);
        Assert.Equal(_domains, presented.GetProperty("names").EnumerateArray().Select(name => name.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(presented.GetProperty("notAfter").GetDateTimeOffset(), acme.GetProperty("notAfter").GetDateTimeOffset());
    }

    private static Dictionary<string, object?> Host() => new()
    {
        ["domains"] = _domains,
        ["enabled"] = true,
        ["target"] = new { scheme = "http", host = "jellyfin.lan", port = 8096, tls = "system", onBox = false },
        ["webSockets"] = true,
        ["http2"] = true,
        ["certificate"] = "acme",
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

    /// <summary>The daemon with the plain proxy port Pebble checks.</summary>
    private sealed class PebbleDaemon(int web, int plain) : DaemonFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PITHYX_DEV_PORT", web.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("PITHYX_DEV_PROXY_PORTS", plain.ToString(CultureInfo.InvariantCulture));
        }
    }
}
