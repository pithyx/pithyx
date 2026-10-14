using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Installer;

/// <summary>The live system's API in simulation: the claim, the owner checks and an installation that only walks through its steps.</summary>
public sealed class InstallerApiTests : IAsyncDisposable
{
    private sealed class InstallerFactory : DaemonFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PITHYX_INSTALLER", "1");
            builder.UseSetting("PITHYX_SIMULATE_SCENARIO", "installer-handwritten");
        }
    }

    private readonly InstallerFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object Request(string diskId, string hostname = "nas") => new
    {
        hostname,
        language = "en",
        region = (string?)null,
        timeZone = "UTC",
        ntpServers = Array.Empty<string>(),
        keyboardLayout = "us",
        diskIds = new[] { diskId },
        username = "admin",
        password = "correct horse battery staple",
        displayName = (string?)null,
        eraseConfirmed = true,
    };

    [Fact]
    public async Task TheLiveSystemServesOnlyTheInstaller()
    {
        using var client = _factory.CreateApiClient();

        var setup = await (await client.GetAsync("/api/setup", Token)).JsonAsync();
        Assert.True(setup.GetProperty("installer").GetBoolean());
        Assert.False(setup.GetProperty("required").GetBoolean());
        using var session = await client.GetAsync("/api/auth/session", Token);
        Assert.Equal(HttpStatusCode.NotFound, session.StatusCode);
        using var createAdmin = await client.PostAsJsonAsync("/api/setup", new { username = "admin", password = "correct horse battery staple" }, Token);
        Assert.NotEqual(HttpStatusCode.OK, createAdmin.StatusCode);
    }

    [Fact]
    public async Task OnlyTheFirstBrowserInstalls()
    {
        using var owner = _factory.CreateApiClient();
        using var other = _factory.CreateApiClient();

        using (var early = await owner.GetAsync("/api/installer/options", Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
            Assert.Equal("installer.claim.required", await early.ErrorCodeAsync());
        }
        using (var claim = await owner.PostAsync("/api/installer/claim", null, Token))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            Assert.True((await claim.JsonAsync()).GetProperty("owner").GetBoolean());
        }
        using (var second = await other.PostAsync("/api/installer/claim", null, Token))
        {
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("installer.claim.taken", await second.ErrorCodeAsync());
        }
        var status = await (await other.GetAsync("/api/installer", Token)).JsonAsync();
        Assert.True(status.GetProperty("claimed").GetBoolean());
        Assert.False(status.GetProperty("owner").GetBoolean());

        var options = await (await owner.GetAsync("/api/installer/options", Token)).JsonAsync();
        var disk = options.GetProperty("disks").EnumerateArray().First(candidate => candidate.GetProperty("suitable").GetBoolean());
        var diskId = disk.GetProperty("disk").GetProperty("id").GetString()!;

        using (var forbidden = await other.PostAsJsonAsync("/api/installer/install", Request(diskId), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        using (var invalid = await owner.PostAsJsonAsync("/api/installer/install", Request(diskId, hostname: "My NAS"), Token))
        {
            Assert.Equal("installer.hostname.invalid", await invalid.ErrorCodeAsync());
        }
        using (var started = await owner.PostAsJsonAsync("/api/installer/install", Request(diskId), Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        }

        var phase = "";
        for (var attempt = 0; attempt < 200 && phase != "installed"; attempt++)
        {
            await Task.Delay(50, Token);
            status = await (await owner.GetAsync("/api/installer", Token)).JsonAsync();
            phase = status.GetProperty("phase").GetString()!;
        }
        Assert.Equal("installed", phase);
        Assert.StartsWith("PTHX", status.GetProperty("recoveryKey").GetString(), StringComparison.Ordinal);
        var seenByOther = await (await other.GetAsync("/api/installer", Token)).JsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, seenByOther.GetProperty("recoveryKey").ValueKind);

        using var reboot = await owner.PostAsync("/api/installer/reboot", null, Token);
        Assert.Equal(HttpStatusCode.NoContent, reboot.StatusCode);
    }

    [Fact]
    public async Task TheConnectionTestChecksTheCardsAsTheyAreWhenNothingChanged()
    {
        using var owner = _factory.CreateApiClient();
        using (var claim = await owner.PostAsync("/api/installer/claim", null, Token))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }
        var options = await (await owner.GetAsync("/api/installer/options", Token)).JsonAsync();
        var config = options.GetProperty("networkConfig");
        Assert.Empty(config.GetProperty("interfaces").EnumerateArray());

        using (var started = await owner.PostAsJsonAsync("/api/installer/network/test", config, Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        }
        var test = await (await owner.GetAsync("/api/installer/network/test", Token)).JsonAsync();
        for (var attempt = 0; attempt < 200 && test.GetProperty("running").GetBoolean(); attempt++)
        {
            await Task.Delay(50, Token);
            test = await (await owner.GetAsync("/api/installer/network/test", Token)).JsonAsync();
        }

        Assert.Equal(System.Text.Json.JsonValueKind.Null, test.GetProperty("errorCode").ValueKind);
        var cards = options.GetProperty("network").EnumerateArray()
            .Where(link => link.GetProperty("kind").GetString() == "ethernet" && link.GetProperty("carrier").GetBoolean())
            .Select(link => link.GetProperty("name").GetString())
            .ToList();
        Assert.NotEmpty(cards);
        Assert.Equal(cards, test.GetProperty("interfaces").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    }
}

/// <summary>The installed system answers the id the installer gave it, readable from the installer page on another port.</summary>
public sealed class InstallIdTests : IAsyncDisposable
{
    private readonly DaemonFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task TheInstallIdIsPublicAndCrossOrigin()
    {
        using var client = _factory.CreateApiClient();
        using (var missing = await client.GetAsync("/api/install-id", Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        await File.WriteAllTextAsync(Path.Combine(_factory.DataDirectory, "install-id"), "abc123\n", Token);
        using var found = await client.GetAsync("/api/install-id", Token);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal("*", found.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("abc123", (await found.JsonAsync()).GetProperty("installId").GetString());
    }
}
