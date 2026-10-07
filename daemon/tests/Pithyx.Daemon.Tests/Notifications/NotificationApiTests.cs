using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;
using static Pithyx.Daemon.Tests.Jobs.JobTestSupport;

namespace Pithyx.Daemon.Tests.Notifications;

public sealed class NotificationApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private NotificationService _notifications = null!;
    private long _bobId;
    private long _carolId;
    private string _bobCookie = "";
    private string _carolCookie = "";
    private HttpClient _bob = null!;
    private HttpClient _carol = null!;

    public async ValueTask InitializeAsync()
    {
        _notifications = _factory.Services.GetRequiredService<NotificationService>();
        _bobId = await _factory.InsertUserAsync("bob");
        _carolId = await _factory.InsertUserAsync("carol");
        _bobCookie = await _factory.SessionCookieAsync(_bobId);
        _carolCookie = await _factory.SessionCookieAsync(_carolId);
        _bob = _factory.ClientWithCookie(_bobCookie);
        _carol = _factory.ClientWithCookie(_carolCookie);
    }

    public ValueTask DisposeAsync()
    {
        _bob.Dispose();
        _carol.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<Notification> CreateAsync(long userId) =>
        _notifications.CreateAsync(userId, new NotificationDraft("test.message", new Dictionary<string, object?> { ["name"] = "tank" }, NotificationTone.Warning), Token);

    private static long[] Ids(JsonElement list) => [.. list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64())];

    private async Task<WebSocket> ConnectAsync(string cookie)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "http://localhost";
        };
        return await client.ConnectAsync(new Uri("ws://localhost/ws"), Token);
    }

    private static Task SendAsync(WebSocket socket, string type, string id, string topic) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, id, topic })), WebSocketMessageType.Text, endOfMessage: true, Token);

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[256 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    [Fact]
    public async Task EveryoneListsAndChangesOnlyTheirOwnNotifications()
    {
        var bobs = await CreateAsync(_bobId);
        var carols = await CreateAsync(_carolId);

        using var list = await _bob.GetAsync("/api/notifications", Token);
        using var deleteForeign = await _bob.DeleteAsync($"/api/notifications/{carols.Id}", Token);
        using var readForeign = await _bob.PostAsync("/api/notifications/read", new { ids = new[] { carols.Id } });
        using var clear = await _bob.DeleteAsync("/api/notifications", Token);
        using var carolList = await _carol.GetAsync("/api/notifications", Token);

        var bobList = await list.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal([bobs.Id], Ids(bobList));
        Assert.Equal("warning", bobList.GetProperty("items")[0].GetProperty("tone").GetString());
        Assert.Equal(HttpStatusCode.NotFound, deleteForeign.StatusCode);
        Assert.Equal(ErrorCodes.NotificationsNotificationNotFound, await deleteForeign.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, readForeign.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        var carolItem = Assert.Single((await carolList.JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(carols.Id, carolItem.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, carolItem.GetProperty("readAt").ValueKind);
        Assert.Empty((await _notifications.ListAsync(Principal(_bobId), Token)).Items);
    }

    [Fact]
    public async Task ReadAndDeleteOneWorkForTheOwner()
    {
        var first = await CreateAsync(_bobId);
        var second = await CreateAsync(_bobId);

        using var read = await _bob.PostAsync("/api/notifications/read", new { ids = new[] { first.Id } });
        using var delete = await _bob.DeleteAsync($"/api/notifications/{second.Id}", Token);
        using var list = await _bob.GetAsync("/api/notifications", Token);

        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var item = Assert.Single((await list.JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(first.Id, item.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.String, item.GetProperty("readAt").ValueKind);
    }

    [Fact]
    public async Task WithoutASessionEveryEndpointAnswers401()
    {
        using var anonymous = _factory.CreateApiClient();

        using var list = await anonymous.GetAsync("/api/notifications", Token);
        using var app = await anonymous.PostAsync("/api/notifications/app", new { app = "org.pithyx.files", title = "Hi" });
        using var clear = await anonymous.DeleteAsync("/api/notifications", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, app.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, clear.StatusCode);
    }

    [Fact]
    public async Task AnAppNotificationIsStoredAsPlainTextWithTheApp()
    {
        using var response = await _bob.PostAsync("/api/notifications/app", new { app = "org.pithyx.files", title = "  Backup done  ", body = "{count} files", tone = "success" });
        var created = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(NotificationService.AppMessageCode, created.GetProperty("code").GetString());
        Assert.Equal("org.pithyx.files", created.GetProperty("app").GetString());
        Assert.Equal("success", created.GetProperty("tone").GetString());
        Assert.Equal("Backup done", created.GetProperty("params").GetProperty("title").GetString());
        Assert.Equal("{count} files", created.GetProperty("params").GetProperty("body").GetString());
        Assert.Equal(created.GetProperty("id").GetInt64(), Assert.Single((await _notifications.ListAsync(Principal(_bobId), Token)).Items).Id);
    }

    [Theory]
    [InlineData("org.pithyx.Demo", "Hi", null, "app")]
    [InlineData("demo", "Hi", null, "app")]
    [InlineData("org.pithyx.files", " ", null, "title")]
    [InlineData("org.pithyx.files", "Hi", 1001, "body")]
    public async Task AnInvalidAppNotificationIsRefusedWithTheField(string app, string title, int? bodyLength, string field)
    {
        using var response = await _bob.PostAsync("/api/notifications/app", new { app, title, body = bodyLength is { } length ? new string('x', length) : null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.JsonAsync();
        Assert.Equal(ErrorCodes.NotificationsMessageInvalid, problem.GetProperty("code").GetString());
        Assert.Equal(field, problem.GetProperty("params").GetProperty("field").GetString());
        Assert.Empty((await _notifications.ListAsync(Principal(_bobId), Token)).Items);
    }

    [Fact]
    public async Task TheTopicSendsEachUserTheirOwnListAndOnlyTheirOwnChanges()
    {
        var existing = await CreateAsync(_bobId);
        using var bob = await ConnectAsync(_bobCookie);
        using var carol = await ConnectAsync(_carolCookie);
        await SendAsync(bob, "sub", "b1", NotificationStore.Topic);
        await SendAsync(carol, "sub", "c1", NotificationStore.Topic);
        Assert.Equal("ack", (await ReceiveAsync(bob)).GetProperty("type").GetString());
        Assert.Equal("ack", (await ReceiveAsync(carol)).GetProperty("type").GetString());
        var bobSnapshot = await ReceiveAsync(bob);
        var carolSnapshot = await ReceiveAsync(carol);

        var created = await CreateAsync(_bobId);
        var bobEvent = await ReceiveAsync(bob);
        await _notifications.MarkReadAsync(Principal(_bobId), null, Token);
        var bobRead = await ReceiveAsync(bob);
        await _notifications.DeleteAsync(Principal(_bobId), existing.Id, Token);
        var bobDeleted = await ReceiveAsync(bob);
        await SendAsync(carol, "unsub", "c2", NotificationStore.Topic);
        var carolNext = await ReceiveAsync(carol);

        Assert.Equal("snap", bobSnapshot.GetProperty("type").GetString());
        Assert.Equal([existing.Id], Ids(bobSnapshot.GetProperty("data")));
        Assert.Empty(Ids(carolSnapshot.GetProperty("data")));
        Assert.Equal("event", bobEvent.GetProperty("type").GetString());
        Assert.Equal("created", bobEvent.GetProperty("data").GetProperty("change").GetString());
        Assert.Equal(created.Id, bobEvent.GetProperty("data").GetProperty("notification").GetProperty("id").GetInt64());
        Assert.Equal("read", bobRead.GetProperty("data").GetProperty("change").GetString());
        Assert.Equal([created.Id, existing.Id], bobRead.GetProperty("data").GetProperty("ids").EnumerateArray().Select(id => id.GetInt64()));
        Assert.Equal("deleted", bobDeleted.GetProperty("data").GetProperty("change").GetString());
        Assert.Equal([existing.Id], bobDeleted.GetProperty("data").GetProperty("ids").EnumerateArray().Select(id => id.GetInt64()));
        // Carol's next message is the ack of her unsub: none of Bob's changes reached her.
        Assert.Equal("ack", carolNext.GetProperty("type").GetString());
        Assert.Equal("c2", carolNext.GetProperty("id").GetString());
    }

    [Fact]
    public async Task TheJobsTopicShowsEachUserTheirOwnRunningJobsAndTheirEnd()
    {
        var jobs = _factory.Services.GetRequiredService<JobService>();
        var gate = new TaskCompletionSource();
        var carolsJob = await jobs.StartAsync(_carolId, "files.copy", null, Gated(new TaskCompletionSource()), Token);
        await jobs.WaitForStateAsync(carolsJob.Id, JobState.Running);
        using var bob = await ConnectAsync(_bobCookie);
        await SendAsync(bob, "sub", "b1", JobBoard.ActiveTopic);
        Assert.Equal("ack", (await ReceiveAsync(bob)).GetProperty("type").GetString());
        var snapshot = await ReceiveAsync(bob);

        var job = await jobs.StartAsync(_bobId, "files.move", null, Gated(gate), Token);
        var queued = await ReceiveAsync(bob);
        var running = await ReceiveAsync(bob);
        gate.SetResult();
        var succeeded = await ReceiveAsync(bob);

        Assert.Equal("snap", snapshot.GetProperty("type").GetString());
        Assert.Empty(snapshot.GetProperty("data").GetProperty("jobs").EnumerateArray());
        Assert.Equal(("event", JobBoard.ActiveTopic, job.Id, "queued"), (queued.GetProperty("type").GetString(), queued.GetProperty("topic").GetString(), queued.GetProperty("data").GetProperty("id").GetInt64(), queued.GetProperty("data").GetProperty("state").GetString()));
        Assert.Equal("running", running.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("succeeded", succeeded.GetProperty("data").GetProperty("state").GetString());
    }
}
