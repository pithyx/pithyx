using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Files;

public sealed class RecentFilesApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly string _root;
    private long _share;

    public RecentFilesApiTests()
    {
        _admin = _factory.CreateApiClient();
        _root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "share")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "Docs"));
        File.WriteAllText(Path.Combine(_root, "Docs", "plan.md"), "plan");
        File.WriteAllText(Path.Combine(_root, "photo.jpg"), "photo");
    }

    public async ValueTask InitializeAsync()
    {
        await _admin.SetupAdminAsync();
        using var created = await _admin.PostAsync("/api/shares", new { name = "Media", path = _root });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _share = (await created.JsonAsync()).GetProperty("id").GetInt64();
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<HttpStatusCode> OpenAsync(HttpClient client, string path, long? share = null)
    {
        using var response = await client.PostAsync("/api/files/recent", new { share = share ?? _share, path });
        return response.StatusCode;
    }

    private static async Task<string[]> RecentAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/files/recent", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).EnumerateArray().Select(entry => entry.GetProperty("path").GetString()!)];
    }

    private async Task<(HttpClient Client, long Id)> UserAsync(string username, string access)
    {
        var hash = await _factory.Services.GetRequiredService<PasswordHasher>().HashAsync(AdminPassword, Token);
        long id;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            id = await TestUsers.InsertAsync(connection, username, hash: hash);
        }
        await _admin.SetShareAccessAsync(_share, id, access);
        var client = _factory.CreateApiClient();
        using var login = await client.LoginAsync(username, AdminPassword);
        login.EnsureSuccessStatusCode();
        return (client, id);
    }

    [Fact]
    public async Task OpeningAFileAgainMovesItToTheTopWithItsCurrentSizeAndDate()
    {
        Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(_admin, "Docs/plan.md"));
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(_admin, "photo.jpg"));
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(_admin, "/Docs//plan.md"));
        Assert.Equal(["Docs/plan.md", "photo.jpg"], await RecentAsync(_admin));

        File.WriteAllText(Path.Combine(_root, "Docs", "plan.md"), "a longer plan");
        using var response = await _admin.GetAsync("/api/files/recent", Token);
        var first = (await response.JsonAsync())[0];
        Assert.Equal(_share, first.GetProperty("share").GetInt64());
        Assert.Equal(13, first.GetProperty("size").GetInt64());
        Assert.Equal(_factory.Time.GetUtcNow(), first.GetProperty("openedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task OnlyFilesTheUserCanReadAreRecorded()
    {
        var (anna, _) = await UserAsync("anna", "none");
        using (anna)
        {
            Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(anna, "photo.jpg"));
            Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(_admin, "missing.txt"));
            Assert.Equal(HttpStatusCode.BadRequest, await OpenAsync(_admin, "Docs"));
            Assert.Equal(HttpStatusCode.BadRequest, await OpenAsync(_admin, ""));
            Assert.Empty(await RecentAsync(anna));
            Assert.Empty(await RecentAsync(_admin));
        }
    }

    [Fact]
    public async Task EntriesTheUserCanNoLongerReadAreLeftOutAndEveryUserHasTheirOwnList()
    {
        var (anna, annaId) = await UserAsync("anna", "read");
        using (anna)
        {
            Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(anna, "photo.jpg"));
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(anna, "Docs/plan.md"));
            Assert.Empty(await RecentAsync(_admin));

            File.Move(Path.Combine(_root, "Docs", "plan.md"), Path.Combine(_root, "Docs", "moved.md"));
            Assert.Equal(["photo.jpg"], await RecentAsync(anna));

            await _admin.SetShareAccessAsync(_share, annaId, "none");
            Assert.Empty(await RecentAsync(anna));
            await _admin.SetShareAccessAsync(_share, annaId, "read");
            Assert.Equal(["photo.jpg"], await RecentAsync(anna));
        }
    }

    [Fact]
    public async Task TheListKeepsTheNewestFiftyAndCanBeCleared()
    {
        for (var i = 0; i <= RecentFiles.Limit; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"file{i}.txt"), "x");
            Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(_admin, $"file{i}.txt"));
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
        }
        var recent = await RecentAsync(_admin);
        Assert.Equal(RecentFiles.Limit, recent.Length);
        Assert.Equal($"file{RecentFiles.Limit}.txt", recent[0]);
        Assert.DoesNotContain("file0.txt", recent);

        using var cleared = await _admin.DeleteAsync("/api/files/recent", Token);
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Empty(await RecentAsync(_admin));
    }

    [Fact]
    public async Task RemovingTheShareRemovesItsEntries()
    {
        Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(_admin, "photo.jpg"));
        await _factory.Services.GetRequiredService<ShareService>().UnregisterAsync(_share, Token);

        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM recent_files"));
    }

    [Fact]
    public async Task TheTopicTellsOnlyTheUserWhoseListChanged()
    {
        var (anna, annaId) = await UserAsync("anna", "read");
        var (ben, benId) = await UserAsync("ben", "read");
        using (anna)
        using (ben)
        {
            using var annaSocket = await SubscribeAsync(annaId);
            using var benSocket = await SubscribeAsync(benId);

            Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(anna, "photo.jpg"));
            var annaEvent = await ReceiveAsync(annaSocket);
            Assert.Equal(("event", "files/recent"), (annaEvent.GetProperty("type").GetString(), annaEvent.GetProperty("topic").GetString()));
            var annaRevision = annaEvent.GetProperty("data").GetProperty("revision").GetInt64();

            // Ben's first event is his own change, not Anna's.
            Assert.Equal(HttpStatusCode.NoContent, await OpenAsync(ben, "photo.jpg"));
            var benEvent = await ReceiveAsync(benSocket);
            Assert.Equal(annaRevision + 1, benEvent.GetProperty("data").GetProperty("revision").GetInt64());
        }
    }

    private async Task<WebSocket> SubscribeAsync(long userId)
    {
        var cookie = await _factory.SessionCookieAsync(userId);
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "http://localhost";
        };
        var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "sub", id = "r", topic = RecentFiles.Topic })), WebSocketMessageType.Text, endOfMessage: true, Token);
        Assert.Equal("ack", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        Assert.Equal("snap", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        return socket;
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }
}
