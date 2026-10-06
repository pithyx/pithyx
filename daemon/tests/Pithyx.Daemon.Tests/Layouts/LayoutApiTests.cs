using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Layouts;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Layouts;

public sealed class LayoutApiTests : IDisposable
{
    private const string Firefox = "Mozilla/5.0 (X11; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0";
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _client;

    public LayoutApiTests()
    {
        _client = _factory.CreateClient(new() { HandleCookies = false });
        _client.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string Layout(string app = "disks") =>
        $$"""{"version":1,"viewport":{"width":1920,"height":1032},"taskbar":"left","windows":[{"id":"w1","app":"{{app}}","screen":1,"title":null,"rect":{"x":10,"y":20,"width":600,"height":400},"placement":{"kind":"free"},"minimized":false,"args":null}],"order":["w1"],"focused":"w1"}""";

    private static string Cookies(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string cookies, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookies);
        request.Headers.UserAgent.ParseAdd(Firefox);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request, Token);
    }

    /// <summary>Sets up the administrator and returns the session and device cookies of that browser.</summary>
    private async Task<string> SetupAsync()
    {
        using var setup = await _client.PostAsync("/api/setup", new { username = "admin", password = AdminPassword });
        setup.EnsureSuccessStatusCode();
        return Cookies(setup);
    }

    /// <summary>Signs in again from another browser, which gets its own device cookie.</summary>
    private async Task<string> SecondDeviceAsync()
    {
        using var login = await _client.PostAsync("/api/auth/login", new { username = "admin", password = AdminPassword });
        login.EnsureSuccessStatusCode();
        return Cookies(login);
    }

    private async Task<HttpResponseMessage> SaveAsync(string cookies, string layout, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/desktop/layout");
        request.Headers.Add("Cookie", cookies);
        request.Headers.UserAgent.ParseAdd(Firefox);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        request.Content = new StringContent(layout, Encoding.UTF8, "application/json");
        return await _client.SendAsync(request, Token);
    }

    private static string? SetDevice(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.SingleOrDefault(cookie => cookie.StartsWith(DeviceCookie.CookieName + "=", StringComparison.Ordinal))
            : null;

    private static string DeviceId(string cookies) =>
        cookies.Split("; ").Single(cookie => cookie.StartsWith(DeviceCookie.CookieName + "=", StringComparison.Ordinal)).Split('=')[1];

    [Fact]
    public async Task SessionStartSetsALongLivedHttpOnlyDeviceCookie()
    {
        using var setup = await _client.PostAsync("/api/setup", new { username = "admin", password = AdminPassword });

        var cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(DeviceCookie.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=34560000", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDeviceCookieIsSecureOnHttps()
    {
        using var client = _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");

        using var setup = await client.PostAsync("/api/setup", new { username = "admin", password = AdminPassword });
        using var plain = await _client.PostAsync("/api/auth/login", new { username = "admin", password = AdminPassword });

        Assert.Contains("secure", SetDevice(setup), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", SetDevice(plain), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces and ! marks in it")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task AMalformedDeviceCookieIsIgnoredAndTheSaveIssuesAnId(string value)
    {
        var cookies = await SetupAsync();
        var session = cookies.Split("; ").Single(cookie => cookie.StartsWith(SessionService.CookieName, StringComparison.Ordinal));
        var malformed = $"{session}; {DeviceCookie.CookieName}={value}";

        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", malformed);
        using var save = await SaveAsync(malformed, Layout());

        Assert.Equal(JsonValueKind.Null, (await state.JsonAsync()).GetProperty("layout").ValueKind);
        Assert.False(state.Headers.TryGetValues("Set-Cookie", out _));
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
        Assert.DoesNotContain(value, SetDevice(save), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASavedLayoutIsRestoredForTheSameDeviceWithItsVersion()
    {
        var cookies = await SetupAsync();

        using var save = await SaveAsync(cookies, Layout(), "\"0\"");
        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", cookies);
        var body = await state.JsonAsync();

        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
        Assert.Equal("\"1\"", save.Headers.ETag?.Tag);
        Assert.Equal(1, body.GetProperty("layoutVersion").GetInt64());
        Assert.Equal("left", body.GetProperty("layout").GetProperty("taskbar").GetString());
        Assert.Equal("disks", body.GetProperty("layout").GetProperty("windows")[0].GetProperty("app").GetString());
        Assert.Equal(0, body.GetProperty("preferences").GetArrayLength());
    }

    [Fact]
    public async Task ADeviceWithoutLayoutGetsNullAndVersionZero()
    {
        var cookies = await SetupAsync();

        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", cookies);
        var body = await state.JsonAsync();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("layout").ValueKind);
        Assert.Equal(0, body.GetProperty("layoutVersion").GetInt64());
    }

    [Fact]
    public async Task ASaveWithAnOlderVersionIsRefusedWithTheCurrentOne()
    {
        var cookies = await SetupAsync();
        using var first = await SaveAsync(cookies, Layout(), "\"0\"");

        using var stale = await SaveAsync(cookies, Layout("about"), "\"0\"");
        using var malformed = await SaveAsync(cookies, Layout("about"), "W/\"1\"");
        using var current = await SaveAsync(cookies, Layout("about"), "\"1\"");
        using var unconditional = await SaveAsync(cookies, Layout());

        var problem = await stale.JsonAsync();
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("desktop.layout.conflict", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("params").GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.PreconditionFailed, malformed.StatusCode);
        Assert.Equal("\"2\"", current.Headers.ETag?.Tag);
        Assert.Equal("\"3\"", unconditional.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task AnExpiredLayoutCountsAsNone()
    {
        var cookies = await SetupAsync();
        using var first = await SaveAsync(cookies, Layout(), "\"0\"");
        // Only the layout expires here; advancing the clock would end the session as well.
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            await connection.ExecuteAsync("UPDATE desktop_layouts SET expires_at = @now", new { now = SessionService.Format(_factory.Time.GetUtcNow()) });
        }

        using var again = await SaveAsync(cookies, Layout(), "\"0\"");

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal("\"1\"", again.Headers.ETag?.Tag);
    }

    [Theory]
    [InlineData("{\"version\":2,\"windows\":[]}", "desktop.layout.invalid")]
    [InlineData("null", "desktop.layout.invalid")]
    [InlineData("[1,2]", "desktop.layout.invalid")]
    public async Task InvalidLayoutsAreAnsweredWithAnError(string data, string code)
    {
        using var save = await SaveAsync(await SetupAsync(), data);

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Equal(code, await save.ErrorCodeAsync());
    }

    [Fact]
    public async Task OversizedLayoutsAreAnsweredWithTooLarge()
    {
        var args = JsonSerializer.Serialize(new string('x', 17 * 1024));

        using var save = await SaveAsync(await SetupAsync(), Layout().Replace("\"args\":null", $"\"args\":{args}", StringComparison.Ordinal));

        var problem = await save.JsonAsync();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, save.StatusCode);
        Assert.Equal("desktop.layout.too_large", problem.GetProperty("code").GetString());
        Assert.Equal(50, problem.GetProperty("params").GetProperty("maxWindows").GetInt32());
    }

    [Fact]
    public async Task ASaveWithoutDeviceCookieGetsOne()
    {
        var cookies = await SetupAsync();
        var sessionOnly = cookies.Split("; ").Single(cookie => cookie.StartsWith(SessionService.CookieName, StringComparison.Ordinal));

        using var save = await SaveAsync(sessionOnly, Layout());

        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
        Assert.NotNull(SetDevice(save));
    }

    [Fact]
    public async Task LayoutsKeepTheirTextAsSent()
    {
        var cookies = await SetupAsync();
        using var save = await SaveAsync(cookies, Layout().Replace("\"title\":null", "\"title\":\"Übersicht <1> & 2\"", StringComparison.Ordinal));

        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        var stored = await connection.ExecuteScalarAsync<string>("SELECT layout FROM desktop_layouts");

        Assert.Contains("\"title\":\"Übersicht <1> & 2\"", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherDevicesAreListedAndTheirLayoutCanBeCopied()
    {
        var laptop = await SetupAsync();
        using var saved = await SaveAsync(laptop, Layout());
        var phone = await SecondDeviceAsync();

        using var devices = await SendAsync(HttpMethod.Get, "/api/desktop/devices", phone);
        var list = await devices.JsonAsync();
        var device = Assert.Single(list.EnumerateArray());
        Assert.Equal(DeviceId(laptop), device.GetProperty("deviceId").GetString());
        Assert.Equal("Firefox", device.GetProperty("browser").GetString());
        Assert.Equal("Linux", device.GetProperty("os").GetString());
        Assert.Equal(1, device.GetProperty("windowCount").GetInt32());
        Assert.Equal(1920, device.GetProperty("screenWidth").GetInt32());
        Assert.Equal(1032, device.GetProperty("screenHeight").GetInt32());

        using var copy = await SendAsync(HttpMethod.Post, "/api/desktop/layout/copy", phone, new { deviceId = DeviceId(laptop) });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        var copied = await copy.JsonAsync();
        Assert.Equal("left", copied.GetProperty("layout").GetProperty("taskbar").GetString());
        Assert.Equal(1, copied.GetProperty("version").GetInt64());
        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", phone);
        Assert.Equal("left", (await state.JsonAsync()).GetProperty("layout").GetProperty("taskbar").GetString());

        using var own = await SendAsync(HttpMethod.Get, "/api/desktop/devices", laptop);
        Assert.Equal(DeviceId(phone), Assert.Single((await own.JsonAsync()).EnumerateArray()).GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task CopyingFromAnUnknownDeviceFails()
    {
        var cookies = await SetupAsync();

        using var copy = await SendAsync(HttpMethod.Post, "/api/desktop/layout/copy", cookies, new { deviceId = "no-such-device-000" });

        Assert.Equal(HttpStatusCode.NotFound, copy.StatusCode);
        Assert.Equal("desktop.device.not_found", await copy.ErrorCodeAsync());
    }

    [Fact]
    public async Task LayoutsOfOtherUsersStayInvisible()
    {
        var admin = await SetupAsync();
        using var saved = await SaveAsync(admin, Layout());
        var other = await OtherUserAsync();

        using var devices = await SendAsync(HttpMethod.Get, "/api/desktop/devices", other);
        using var copy = await SendAsync(HttpMethod.Post, "/api/desktop/layout/copy", other, new { deviceId = DeviceId(admin) });
        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", other + "; " + DeviceCookie.CookieName + "=" + DeviceId(admin));

        Assert.Equal(0, (await devices.JsonAsync()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, copy.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await state.JsonAsync()).GetProperty("layout").ValueKind);
    }

    [Fact]
    public async Task WindowPreferencesAreSharedAcrossDevices()
    {
        var laptop = await SetupAsync();
        using var put = await SendAsync(HttpMethod.Put, "/api/desktop/preferences/org.pithyx.files", laptop, new { width = 900, height = 600 });
        using var again = await SendAsync(HttpMethod.Put, "/api/desktop/preferences/org.pithyx.files", laptop, new { width = 950, height = 610 });
        var phone = await SecondDeviceAsync();

        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", phone);
        var preference = Assert.Single((await state.JsonAsync()).GetProperty("preferences").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("org.pithyx.files", preference.GetProperty("appId").GetString());
        Assert.Equal(950, preference.GetProperty("width").GetInt32());
        Assert.Equal(610, preference.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task InvalidWindowPreferencesAreRejected()
    {
        var cookies = await SetupAsync();

        using var put = await SendAsync(HttpMethod.Put, "/api/desktop/preferences/org.pithyx.files", cookies, new { width = 0, height = 600 });
        using var shortId = await SendAsync(HttpMethod.Put, "/api/desktop/preferences/files", cookies, new { width = 900, height = 600 });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, shortId.StatusCode);
    }

    [Fact]
    public async Task AccentAndWallpaperFollowTheUserToEveryDevice()
    {
        var laptop = await SetupAsync();
        using (var initial = await SendAsync(HttpMethod.Get, "/api/desktop", laptop))
        {
            var appearance = (await initial.JsonAsync()).GetProperty("appearance");
            Assert.Equal(("petrol", "boxes"), (appearance.GetProperty("accent").GetString(), appearance.GetProperty("wallpaper").GetString()));
        }

        using var put = await SendAsync(HttpMethod.Put, "/api/desktop/appearance", laptop, new { accent = "moss", wallpaper = "plain" });
        var phone = await SecondDeviceAsync();
        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", phone);
        var saved = (await state.JsonAsync()).GetProperty("appearance");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(("moss", "plain"), (saved.GetProperty("accent").GetString(), saved.GetProperty("wallpaper").GetString()));
    }

    [Theory]
    [InlineData("red", "boxes")]
    [InlineData("petrol", "photo")]
    public async Task UnknownAccentsAndWallpapersAreRejected(string accent, string wallpaper)
    {
        var cookies = await SetupAsync();

        using var put = await SendAsync(HttpMethod.Put, "/api/desktop/appearance", cookies, new { accent, wallpaper });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal("request.body.invalid", await put.ErrorCodeAsync());
    }

    [Fact]
    public async Task TheHourlyCleanupDeletesLayoutsOnlyAfterTheyExpire()
    {
        var cookies = await SetupAsync();
        using var saved = await SaveAsync(cookies, Layout());
        var layouts = _factory.Services.GetRequiredService<LayoutService>();
        var userId = await UserIdAsync("admin");

        _factory.Time.Advance(LayoutService.Lifetime - TimeSpan.FromHours(2));
        await Task.Delay(50, Token);
        Assert.Equal(1, await LayoutCountAsync());
        Assert.NotNull((await layouts.GetStateAsync(userId, DeviceId(cookies), Token)).Layout);

        // Advancing the clock fires the cleanup timer, which runs in the background.
        _factory.Time.Advance(TimeSpan.FromHours(3));
        Assert.Null((await layouts.GetStateAsync(userId, DeviceId(cookies), Token)).Layout);
        for (var attempt = 0; attempt < 100 && await LayoutCountAsync() > 0; attempt++)
        {
            await Task.Delay(50, Token);
        }
        Assert.Equal(0, await LayoutCountAsync());
    }

    private async Task<long> LayoutCountAsync()
    {
        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM desktop_layouts");
    }

    private async Task<long> UserIdAsync(string username)
    {
        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return await connection.ExecuteScalarAsync<long>("SELECT id FROM users WHERE username = @username", new { username });
    }

    /// <summary>A second user with a session; user management comes with M3, so the row is inserted directly.</summary>
    private async Task<string> OtherUserAsync()
    {
        long id;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            id = await TestUsers.InsertAsync(connection, "bob");
        }
        var session = await _factory.Services.GetRequiredService<SessionService>().CreateAsync(id, null, Token);
        return $"{SessionService.CookieName}={session.Token}";
    }
}
