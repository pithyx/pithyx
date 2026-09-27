using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vyxis.Daemon.Auth;
using Vyxis.Daemon.Realtime;
using Vyxis.Daemon.Tests.Auth;
using Vyxis.Daemon.Tests.Support;
using static Vyxis.Daemon.Tests.Auth.AuthApi;

namespace Vyxis.Daemon.Tests.Realtime;

public sealed class WebSocketTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _client;

    public WebSocketTests() => _client = _factory.CreateApiClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<string> SessionCookieAsync()
    {
        using var setup = await _client.PostAsync("/api/setup", new { username = "admin", password = AdminPassword });
        return setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("vyxis_session=", StringComparison.Ordinal)).Split(';')[0];
    }

    private async Task<WebSocket> ConnectAsync(string? cookie, string origin = "http://localhost")
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (cookie is not null)
            {
                request.Headers.Cookie = cookie;
            }
            request.Headers.Origin = origin;
        };
        return await client.ConnectAsync(new Uri("ws://localhost/ws"), Token);
    }

    private static Task SendAsync(WebSocket socket, object message) =>
        SendRawAsync(socket, JsonSerializer.Serialize(message));

    private static Task SendRawAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, Token);

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    private static async Task<WebSocketCloseStatus?> ReceiveCloseAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
        }
        while (result.MessageType != WebSocketMessageType.Close);
        return result.CloseStatus;
    }

    private static WebSocketCloseStatus Status(WebSocketCloseCode code) => (WebSocketCloseStatus)code;

    [Fact]
    public async Task ConnectionWithoutSessionIsClosedWithSessionEnded()
    {
        using var socket = await ConnectAsync(cookie: null);

        Assert.Equal(Status(WebSocketCloseCode.SessionEnded), await ReceiveCloseAsync(socket));
    }

    [Fact]
    public async Task ConnectionFromAnotherOriginIsClosedWithForbidden()
    {
        using var socket = await ConnectAsync(await SessionCookieAsync(), origin: "https://evil.example");

        Assert.Equal(Status(WebSocketCloseCode.Forbidden), await ReceiveCloseAsync(socket));
    }

    [Fact]
    public async Task ExpiredSessionClosesTheOpenConnection()
    {
        using var socket = await ConnectAsync(await SessionCookieAsync());
        await SendAsync(socket, new { type = "unsub", id = "c1", topic = "system/info" });
        await ReceiveAsync(socket);

        _factory.Time.Advance(SessionService.AbsoluteLifetime);

        Assert.Equal(Status(WebSocketCloseCode.SessionEnded), await ReceiveCloseAsync(socket));
    }

    [Fact]
    public async Task OpenConnectionKeepsTheSessionAliveUntilItsAbsoluteEnd()
    {
        var cookie = await SessionCookieAsync();
        using var socket = await ConnectAsync(cookie);
        await SendAsync(socket, new { type = "unsub", id = "c1", topic = "system/info" });
        await ReceiveAsync(socket);

        for (var hour = 0; hour < 24 * 10; hour++)
        {
            _factory.Time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(1, Token);
        }

        using var session = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
        session.Headers.Add("Cookie", cookie);
        using var anonymous = _factory.CreateClient(new() { HandleCookies = false });
        Assert.Equal(System.Net.HttpStatusCode.OK, (await anonymous.SendAsync(session, Token)).StatusCode);
    }

    [Fact]
    public async Task StoppingTheDaemonClosesWithDaemonRestart()
    {
        using var socket = await ConnectAsync(await SessionCookieAsync());
        await SendAsync(socket, new { type = "unsub", id = "c1", topic = "system/info" });
        await ReceiveAsync(socket);

        _factory.Services.GetRequiredService<TopicHub>().CloseAll();

        Assert.Equal(Status(WebSocketCloseCode.DaemonRestart), await ReceiveCloseAsync(socket));
    }

    [Fact]
    public async Task SubscribeSendsAckThenSnapshot()
    {
        using var socket = await ConnectAsync(await SessionCookieAsync());

        await SendAsync(socket, new { type = "sub", id = "c1", topic = "system/info" });
        var ack = await ReceiveAsync(socket);
        var snapshot = await ReceiveAsync(socket);

        Assert.Equal("ack", ack.GetProperty("type").GetString());
        Assert.Equal("c1", ack.GetProperty("id").GetString());
        Assert.Equal(32, ack.GetProperty("requestId").GetString()!.Length);
        Assert.Equal("snap", snapshot.GetProperty("type").GetString());
        Assert.Equal("c1", snapshot.GetProperty("id").GetString());
        Assert.Equal("system/info", snapshot.GetProperty("topic").GetString());
        Assert.False(string.IsNullOrEmpty(snapshot.GetProperty("data").GetProperty("hostname").GetString()));
        Assert.True(snapshot.GetProperty("data").GetProperty("memoryTotalBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task EveryMessageGetsItsOwnRequestId()
    {
        using var socket = await ConnectAsync(await SessionCookieAsync());

        await SendAsync(socket, new { type = "unsub", id = "c1", topic = "system/info" });
        await SendAsync(socket, new { type = "unsub", id = "c2", topic = "system/info" });

        Assert.NotEqual((await ReceiveAsync(socket)).GetProperty("requestId").GetString(), (await ReceiveAsync(socket)).GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task WildcardSubscriptionGetsEventsOfNewTopicsUntilUnsubscribed()
    {
        _factory.Services.GetRequiredService<TopicRegistry>().Add(
            new TopicDefinition("test/+", typeof(int), typeof(int), (_, _) => ValueTask.FromResult<object>(0), TopicDefinition.AnyUser, CurrentTopics: () => []));
        using var socket = await ConnectAsync(await SessionCookieAsync());
        var hub = _factory.Services.GetRequiredService<TopicHub>();

        await SendAsync(socket, new { type = "sub", id = "c1", topic = "test/#" });
        Assert.Equal("ack", (await ReceiveAsync(socket)).GetProperty("type").GetString());

        hub.Publish("test/new", 7);
        var message = await ReceiveAsync(socket);
        Assert.Equal("event", message.GetProperty("type").GetString());
        Assert.Equal("test/new", message.GetProperty("topic").GetString());
        Assert.Equal(7, message.GetProperty("data").GetInt32());

        await SendAsync(socket, new { type = "unsub", id = "c2", topic = "test/#" });
        Assert.Equal("c2", (await ReceiveAsync(socket)).GetProperty("id").GetString());
        hub.Publish("test/new", 8);
        await SendAsync(socket, new { type = "unsub", id = "c3", topic = "test/#" });
        Assert.Equal("c3", (await ReceiveAsync(socket)).GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("{\"type\":\"sub\",\"id\":\"c1\",\"topic\":\"storage/#/x\"}", "c1", "realtime.topic.invalid")]
    [InlineData("{\"type\":\"sub\",\"id\":\"c1\",\"topic\":\"nothing/here\"}", "c1", "realtime.topic.unknown")]
    [InlineData("{\"type\":\"publish\",\"id\":\"c1\",\"topic\":\"system/info\"}", "c1", "realtime.message.invalid")]
    [InlineData("{\"type\":\"sub\",\"id\":\"c1\"}", "c1", "realtime.message.invalid")]
    [InlineData("{\"type\":\"unsub\",\"id\":\"c1\",\"topic\":null}", "c1", "realtime.message.invalid")]
    [InlineData("not json", "", "realtime.message.invalid")]
    public async Task InvalidMessagesAreAnsweredWithAnError(string raw, string expectedId, string expectedCode)
    {
        using var socket = await ConnectAsync(await SessionCookieAsync());

        await SendRawAsync(socket, raw);
        var error = await ReceiveAsync(socket);

        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal(expectedId, error.GetProperty("id").GetString());
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task MessagesAboveTheLimitCloseTheConnection()
    {
        using var socket = await ConnectAsync(await SessionCookieAsync());

        await SendRawAsync(socket, new string(' ', WebSocketEndpoint.MaxMessageBytes + 1));

        Assert.Equal(Status(WebSocketCloseCode.MessageTooBig), await ReceiveCloseAsync(socket));
    }

    [Fact]
    public async Task MessagesJustBelowTheLimitAreHandled()
    {
        using var socket = await ConnectAsync(await SessionCookieAsync());
        var padding = new string(' ', WebSocketEndpoint.MaxMessageBytes - 1024);

        await SendRawAsync(socket, "{\"type\":\"unsub\",\"id\":\"c1\",\"topic\":\"system/info\"}" + padding);

        Assert.Equal("ack", (await ReceiveAsync(socket)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task LogoutClosesTheConnectionWithSessionEnded()
    {
        var cookie = await SessionCookieAsync();
        using var socket = await ConnectAsync(cookie);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Cookie", cookie);
        logout.Headers.Add("X-Vyxis-Request", "1");
        using var anonymous = _factory.CreateClient(new() { HandleCookies = false });
        await anonymous.SendAsync(logout, Token);

        Assert.Equal(Status(WebSocketCloseCode.SessionEnded), await ReceiveCloseAsync(socket));
    }

    [Fact]
    public async Task SystemInfoIsPublishedWhileSubscribed()
    {
        using var socket = await ConnectAsync(await SessionCookieAsync());
        await SendAsync(socket, new { type = "sub", id = "c1", topic = "system/info" });
        await ReceiveAsync(socket);
        await ReceiveAsync(socket);

        var receive = ReceiveAsync(socket);
        for (var i = 0; i < 50 && !receive.IsCompleted; i++)
        {
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(50, Token);
        }

        var message = await receive;
        Assert.Equal("event", message.GetProperty("type").GetString());
        Assert.Equal("system/info", message.GetProperty("topic").GetString());
    }
}
