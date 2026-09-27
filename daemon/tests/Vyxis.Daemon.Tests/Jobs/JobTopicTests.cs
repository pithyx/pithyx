using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vyxis.Daemon.Jobs;
using Vyxis.Daemon.Tests.Support;
using static Vyxis.Daemon.Tests.Auth.AuthApi;
using static Vyxis.Daemon.Tests.Jobs.JobTestSupport;

namespace Vyxis.Daemon.Tests.Jobs;

public sealed class JobTopicTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private JobService _jobs = null!;
    private long _bobId;
    private string _bobCookie = "";
    private string _carolCookie = "";

    public async ValueTask InitializeAsync()
    {
        _jobs = _factory.Services.GetRequiredService<JobService>();
        _bobId = await _factory.InsertUserAsync("bob");
        _bobCookie = await _factory.SessionCookieAsync(_bobId);
        _carolCookie = await _factory.SessionCookieAsync(await _factory.InsertUserAsync("carol"));
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

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
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    [Fact]
    public async Task JobEventsReachTheOwnerButNotAnotherUser()
    {
        using var bob = await ConnectAsync(_bobCookie);
        using var carol = await ConnectAsync(_carolCookie);
        await SendAsync(bob, "sub", "b1", JobBoard.Pattern);
        await SendAsync(carol, "sub", "c1", JobBoard.Pattern);
        Assert.Equal("ack", (await ReceiveAsync(bob)).GetProperty("type").GetString());
        Assert.Equal("ack", (await ReceiveAsync(carol)).GetProperty("type").GetString());

        var job = await _jobs.StartAsync(_bobId, "test.work", null, Gated(new TaskCompletionSource()), Token);
        var bobEvent = await ReceiveAsync(bob);
        await SendAsync(carol, "unsub", "c2", JobBoard.Pattern);
        var carolNext = await ReceiveAsync(carol);

        Assert.Equal("event", bobEvent.GetProperty("type").GetString());
        Assert.Equal(JobBoard.TopicOf(job.Id), bobEvent.GetProperty("topic").GetString());
        Assert.Equal(job.Id, bobEvent.GetProperty("data").GetProperty("id").GetInt64());
        Assert.Equal("queued", bobEvent.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("ack", carolNext.GetProperty("type").GetString());
        Assert.Equal("c2", carolNext.GetProperty("id").GetString());
    }

    [Fact]
    public async Task SubscribingSendsSnapshotsOfOwnJobsOnly()
    {
        var job = await _jobs.StartAsync(_bobId, "test.work", null, Gated(new TaskCompletionSource()), Token);
        await _jobs.WaitForStateAsync(job.Id, JobState.Running);
        using var bob = await ConnectAsync(_bobCookie);
        using var carol = await ConnectAsync(_carolCookie);

        await SendAsync(bob, "sub", "b1", JobBoard.Pattern);
        var bobAck = await ReceiveAsync(bob);
        var bobSnapshot = await ReceiveAsync(bob);
        await SendAsync(carol, "sub", "c1", JobBoard.Pattern);
        var carolAck = await ReceiveAsync(carol);
        await SendAsync(carol, "sub", "c2", JobBoard.TopicOf(job.Id));
        var carolDenied = await ReceiveAsync(carol);

        Assert.Equal("ack", bobAck.GetProperty("type").GetString());
        Assert.Equal("snap", bobSnapshot.GetProperty("type").GetString());
        Assert.Equal("running", bobSnapshot.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("c1", carolAck.GetProperty("id").GetString());
        Assert.Equal("error", carolDenied.GetProperty("type").GetString());
        Assert.Equal("c2", carolDenied.GetProperty("id").GetString());
        Assert.Equal("auth.permission.denied", carolDenied.GetProperty("code").GetString());
    }
}
