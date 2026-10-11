using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.SystemSettings;

/// <summary>The Info Center, network and regional settings in simulation mode, where a time change stays in memory.</summary>
public sealed class SystemSettingsApiTests : IAsyncLifetime
{
    private static readonly string[] _servers = [" 0.pool.ntp.org ", "", "0.POOL.ntp.org", "192.168.1.1"];
    private static readonly string[] _invalidServer = ["not a host!"];
    private readonly DaemonFactory _factory = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateApiClient();
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await _admin.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<(HttpStatusCode Status, string Code)> RefusedAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return (response.StatusCode, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task TheOverviewNamesTheVersionTheMachineAndItsMemory()
    {
        var overview = await GetAsync("/api/system/overview");

        Assert.StartsWith("1.0.0", overview.GetProperty("version").GetString(), StringComparison.Ordinal);
        Assert.True(overview.GetProperty("cpuCores").GetInt32() >= 1);
        Assert.True(overview.GetProperty("memoryTotalBytes").GetInt64() > 0);
        Assert.False(string.IsNullOrEmpty(overview.GetProperty("kernel").GetString()));
        Assert.Equal(JsonValueKind.Object, (await GetAsync("/api/system/network")).ValueKind);
    }

    [Fact]
    public async Task OnlyAdministratorsReadOrChangeSystemSettings()
    {
        var anna = await _factory.InsertUserAsync("anna");
        using var user = _factory.ClientWithCookie(await _factory.SessionCookieAsync(anna));

        foreach (var path in new[] { "/api/system/overview", "/api/system/network", "/api/system/time", "/api/system/region" })
        {
            Assert.Equal((HttpStatusCode.Forbidden, "auth.permission.denied"), await RefusedAsync(user.GetAsync(path, Token)));
        }
        Assert.Equal(
            (HttpStatusCode.Forbidden, "auth.permission.denied"),
            await RefusedAsync(user.PutAsJsonAsync("/api/system/region", new { language = "de", region = "DE" }, Token)));
        Assert.Equal(
            (HttpStatusCode.Forbidden, "auth.permission.denied"),
            await RefusedAsync(user.PutAsJsonAsync("/api/system/time", new { timeZone = "Europe/Berlin", ntpServers = Array.Empty<string>() }, Token)));
    }

    [Fact]
    public async Task TheTimeZoneAndServersAreCheckedAndSaved()
    {
        var time = await GetAsync("/api/system/time");
        var zones = time.GetProperty("timeZones").EnumerateArray().Select(zone => zone.GetString()).ToList();
        Assert.Contains("Europe/Vienna", zones);
        Assert.Contains("UTC", zones);

        using (var saved = await _admin.PutAsJsonAsync("/api/system/time", new { timeZone = "Europe/Vienna", ntpServers = _servers }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        time = await GetAsync("/api/system/time");
        Assert.Equal("Europe/Vienna", time.GetProperty("timeZone").GetString());
        Assert.Equal(["0.pool.ntp.org", "192.168.1.1"], time.GetProperty("ntpServers").EnumerateArray().Select(server => server.GetString()));

        Assert.Equal(
            (HttpStatusCode.BadRequest, "system.time.zone_invalid"),
            await RefusedAsync(_admin.PutAsJsonAsync("/api/system/time", new { timeZone = "Mars/Olympus", ntpServers = Array.Empty<string>() }, Token)));
        Assert.Equal(
            (HttpStatusCode.BadRequest, "system.time.server_invalid"),
            await RefusedAsync(_admin.PutAsJsonAsync("/api/system/time", new { timeZone = "UTC", ntpServers = _invalidServer }, Token)));
        Assert.Equal(
            (HttpStatusCode.BadRequest, "system.time.servers_too_many"),
            await RefusedAsync(_admin.PutAsJsonAsync("/api/system/time", new { timeZone = "UTC", ntpServers = Enumerable.Range(1, 9).Select(n => $"ntp{n}.example") }, Token)));
    }

    [Fact]
    public async Task TheSystemLanguageAndRegionReachTheSignInPage()
    {
        var setup = await GetAsync("/api/setup");
        Assert.Equal(("en", JsonValueKind.Null), (setup.GetProperty("language").GetString(), setup.GetProperty("region").ValueKind));

        using (var saved = await _admin.PutAsJsonAsync("/api/system/region", new { language = "de", region = "AT" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using var anonymous = _factory.CreateClient();
        var after = await (await anonymous.GetAsync("/api/setup", Token)).JsonAsync();
        Assert.Equal(("de", "AT"), (after.GetProperty("language").GetString(), after.GetProperty("region").GetString()));
        var region = await GetAsync("/api/system/region");
        Assert.Equal(("de", "AT"), (region.GetProperty("language").GetString(), region.GetProperty("region").GetString()));

        foreach (var invalid in new object[] { new { language = "fr", region = (string?)null }, new { language = "en", region = (string?)"Austria" }, new { language = "en", region = (string?)"at" } })
        {
            Assert.Equal((HttpStatusCode.BadRequest, "request.body.invalid"), await RefusedAsync(_admin.PutAsJsonAsync("/api/system/region", invalid, Token)));
        }
    }
}
