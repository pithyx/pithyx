using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Vyxis.Daemon.Auth;
using Vyxis.Daemon.Database;
using Vyxis.Daemon.Layouts;
using Vyxis.Daemon.Tests.Auth;
using Vyxis.Daemon.Tests.Support;
using static Vyxis.Daemon.Tests.Auth.AuthApi;

namespace Vyxis.Daemon.Tests.Layouts;

public sealed class LayoutApiTests : IDisposable
{
    private const string Firefox = "Mozilla/5.0 (X11; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0";
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _client;

    public LayoutApiTests()
    {
        _client = _factory.CreateClient(new() { HandleCookies = false });
        _client.DefaultRequestHeaders.Add("X-Vyxis-Request", "1");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string Layout(string app = "disks") =>
        $$"""{"version":1,"taskbar":"left","windows":[{"id":"w1","app":"{{app}}","screen":1,"title":null,"rect":{"x":10,"y":20,"width":600,"height":400},"placement":{"kind":"free"},"minimized":false,"args":null}],"order":["w1"],"focused":"w1"}""";

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

    private async Task<WebSocket> ConnectAsync(string cookies)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookies;
            request.Headers.Origin = "http://localhost";
            request.Headers.UserAgent = Firefox;
        };
        return await client.ConnectAsync(new Uri("ws://localhost/ws"), Token);
    }

    private static async Task<JsonElement> SaveAsync(WebSocket socket, string data)
    {
        var message = $$"""{"type":"layout","id":"l1","data":{{data}}}""";
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, Token);
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

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

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces and ! marks in it")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task AMalformedDeviceCookieIsReplaced(string value)
    {
        var cookies = await SetupAsync();
        var session = cookies.Split("; ").Single(cookie => cookie.StartsWith(SessionService.CookieName, StringComparison.Ordinal));

        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", $"{session}; {DeviceCookie.CookieName}={value}");

        var issued = state.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(DeviceCookie.CookieName + "=", StringComparison.Ordinal));
        Assert.Matches("^vyxis_device=[A-Za-z0-9_-]{16,64};", issued);
        Assert.DoesNotContain(value, issued, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LayoutSavedOverTheWebSocketIsRestoredForTheSameDevice()
    {
        var cookies = await SetupAsync();
        using var socket = await ConnectAsync(cookies);

        var ack = await SaveAsync(socket, Layout());
        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", cookies);
        var body = await state.JsonAsync();

        Assert.Equal("ack", ack.GetProperty("type").GetString());
        Assert.Equal("l1", ack.GetProperty("id").GetString());
        Assert.Equal("left", body.GetProperty("layout").GetProperty("taskbar").GetString());
        Assert.Equal("disks", body.GetProperty("layout").GetProperty("windows")[0].GetProperty("app").GetString());
        Assert.Equal(0, body.GetProperty("preferences").GetArrayLength());
    }

    [Fact]
    public async Task ADeviceWithoutLayoutGetsNull()
    {
        var cookies = await SetupAsync();

        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", cookies);

        Assert.Equal(JsonValueKind.Null, (await state.JsonAsync()).GetProperty("layout").ValueKind);
    }

    [Theory]
    [InlineData("{\"version\":2,\"windows\":[]}", "desktop.layout.invalid")]
    [InlineData("null", "desktop.layout.invalid")]
    [InlineData("[1,2]", "desktop.layout.invalid")]
    public async Task InvalidLayoutsAreAnsweredWithAnError(string data, string code)
    {
        using var socket = await ConnectAsync(await SetupAsync());

        var error = await SaveAsync(socket, data);

        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("l1", error.GetProperty("id").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OversizedLayoutsAreAnsweredWithTooLarge()
    {
        using var socket = await ConnectAsync(await SetupAsync());
        var args = JsonSerializer.Serialize(new string('x', 17 * 1024));

        var error = await SaveAsync(socket, Layout().Replace("\"args\":null", $"\"args\":{args}", StringComparison.Ordinal));

        Assert.Equal("desktop.layout.too_large", error.GetProperty("code").GetString());
        Assert.Equal(50, error.GetProperty("params").GetProperty("maxWindows").GetInt32());
    }

    [Fact]
    public async Task LayoutWithoutDeviceCookieIsRejected()
    {
        var cookies = await SetupAsync();
        var sessionOnly = cookies.Split("; ").Single(cookie => cookie.StartsWith(SessionService.CookieName, StringComparison.Ordinal));
        using var socket = await ConnectAsync(sessionOnly);

        var error = await SaveAsync(socket, Layout());

        Assert.Equal("desktop.device.missing", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OtherDevicesAreListedAndTheirLayoutCanBeCopied()
    {
        var laptop = await SetupAsync();
        using (var socket = await ConnectAsync(laptop))
        {
            await SaveAsync(socket, Layout());
        }
        var phone = await SecondDeviceAsync();

        using var devices = await SendAsync(HttpMethod.Get, "/api/desktop/devices", phone);
        var list = await devices.JsonAsync();
        var device = Assert.Single(list.EnumerateArray());
        Assert.Equal(DeviceId(laptop), device.GetProperty("deviceId").GetString());
        Assert.Equal("Firefox", device.GetProperty("browser").GetString());
        Assert.Equal("Linux", device.GetProperty("os").GetString());
        Assert.Equal(1, device.GetProperty("windowCount").GetInt32());

        using var copy = await SendAsync(HttpMethod.Post, "/api/desktop/layout/copy", phone, new { deviceId = DeviceId(laptop) });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
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
        using (var socket = await ConnectAsync(admin))
        {
            await SaveAsync(socket, Layout());
        }
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
        using var put = await SendAsync(HttpMethod.Put, "/api/desktop/preferences/disks", laptop, new { width = 900, height = 600 });
        using var again = await SendAsync(HttpMethod.Put, "/api/desktop/preferences/disks", laptop, new { width = 950, height = 610 });
        var phone = await SecondDeviceAsync();

        using var state = await SendAsync(HttpMethod.Get, "/api/desktop", phone);
        var preference = Assert.Single((await state.JsonAsync()).GetProperty("preferences").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("disks", preference.GetProperty("appId").GetString());
        Assert.Equal(950, preference.GetProperty("width").GetInt32());
        Assert.Equal(610, preference.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task InvalidWindowPreferencesAreRejected()
    {
        var cookies = await SetupAsync();

        using var put = await SendAsync(HttpMethod.Put, "/api/desktop/preferences/disks", cookies, new { width = 0, height = 600 });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task TheHourlyCleanupDeletesLayoutsOnlyAfterTheyExpire()
    {
        var cookies = await SetupAsync();
        using (var socket = await ConnectAsync(cookies))
        {
            await SaveAsync(socket, Layout());
        }
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
        var now = SessionService.Format(_factory.Time.GetUtcNow());
        long id;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            id = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO users (username, password_hash, created_at, updated_at) VALUES ('bob', 'x', @now, @now) RETURNING id",
                new { now });
        }
        var session = await _factory.Services.GetRequiredService<SessionService>().CreateAsync(id, null, Token);
        return $"{SessionService.CookieName}={session.Token}";
    }
}
