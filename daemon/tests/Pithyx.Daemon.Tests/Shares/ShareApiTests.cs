using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Shares;

/// <summary>
/// The shared folder endpoints in simulation mode, whose pools have no Pithyx layout and whose data directory holds
/// the seeded folder share "Shared".
/// </summary>
public sealed class ShareApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private HttpClient _admin = null!;
    private long _shared;

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateApiClient();
        await _admin.SetupAdminAsync();
        var shares = await (await _admin.GetAsync("/api/shares", Token)).JsonAsync();
        _shared = shares.EnumerateArray().Single(share => share.GetProperty("name").GetString() == "Shared").GetProperty("id").GetInt64();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpClient Client, long Id)> UserAsync(string username)
    {
        var id = await _factory.InsertUserAsync(username);
        return (_factory.ClientWithCookie(await _factory.SessionCookieAsync(id)), id);
    }

    private async Task<JsonElement> DetailsAsync(string name)
    {
        using var response = await _admin.GetAsync("/api/shares/details", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.JsonAsync()).EnumerateArray().Single(share => share.GetProperty("name").GetString() == name);
    }

    private static async Task<(HttpStatusCode Status, string Code)> RefusedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return (response.StatusCode, await response.ErrorCodeAsync());
        }
    }

    private static Task<HttpResponseMessage> PlanAsync(HttpClient client, object request) => client.PostAsJsonAsync("/api/shares/plans", request, Token);

    [Fact]
    public async Task TheListShowsTheSeededFolderShareWithItsRights()
    {
        var (_, anna) = await UserAsync("anna");
        var (_, ben) = await UserAsync("ben");
        (await _admin.PutAsJsonAsync($"/api/shares/{_shared}/access/{anna}", new { access = "read" }, Token)).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"/api/shares/{_shared}/access/{ben}", new { access = "readWrite" }, Token)).EnsureSuccessStatusCode();

        var shared = await DetailsAsync("Shared");

        Assert.Equal("", shared.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, shared.GetProperty("dataset").ValueKind);
        Assert.Equal(JsonValueKind.Null, shared.GetProperty("usedBytes").ValueKind);
        Assert.EndsWith("/shares/shared", shared.GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal((1, 1), (shared.GetProperty("readUsers").GetInt32(), shared.GetProperty("readWriteUsers").GetInt32()));

        var access = (await (await _admin.GetAsync($"/api/shares/{_shared}/access", Token)).JsonAsync()).EnumerateArray()
            .ToDictionary(user => user.GetProperty("username").GetString()!, user => (user.GetProperty("administrator").GetBoolean(), user.GetProperty("access").GetString()));
        Assert.Equal((true, "none"), access["admin"]);
        Assert.Equal((false, "read"), access["anna"]);
        Assert.Equal((false, "readWrite"), access["ben"]);
        Assert.Equal((HttpStatusCode.NotFound, "fs.share.not_found"), await RefusedAsync(await _admin.GetAsync("/api/shares/999/access", Token)));
    }

    [Fact]
    public async Task EditingChangesTheDescriptionAndTheNameButNeverToATakenOne()
    {
        using var response = await _admin.PatchAsJsonAsync($"/api/shares/{_shared}", new { description = " Team files " }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.JsonAsync();
        Assert.Equal(("Shared", "Team files"), (updated.GetProperty("name").GetString(), updated.GetProperty("description").GetString()));

        (await _admin.PatchAsJsonAsync($"/api/shares/{_shared}", new { name = "Team" }, Token)).EnsureSuccessStatusCode();
        Assert.Equal("Team files", (await DetailsAsync("Team")).GetProperty("description").GetString());

        var other = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "other")).FullName;
        (await _admin.PostAsync("/api/shares", new { name = "Other", path = other })).EnsureSuccessStatusCode();
        Assert.Equal((HttpStatusCode.Conflict, "fs.share.exists"), await RefusedAsync(await _admin.PatchAsJsonAsync($"/api/shares/{_shared}", new { name = "other" }, Token)));
        Assert.Equal((HttpStatusCode.BadRequest, "fs.share.description_invalid"), await RefusedAsync(await _admin.PatchAsJsonAsync($"/api/shares/{_shared}", new { description = new string('x', 201) }, Token)));
        Assert.Equal((HttpStatusCode.BadRequest, "fs.share.invalid"), await RefusedAsync(await _admin.PatchAsJsonAsync($"/api/shares/{_shared}", new { name = "a/b" }, Token)));
        Assert.Equal((HttpStatusCode.NotFound, "fs.share.not_found"), await RefusedAsync(await _admin.PatchAsJsonAsync("/api/shares/999", new { description = "x" }, Token)));
    }

    [Fact]
    public async Task OnlyAdministratorsManageSharedFolders()
    {
        var (user, _) = await UserAsync("anna");
        using (user)
        {
            var denied = (HttpStatusCode.Forbidden, "auth.permission.denied");
            Assert.Equal(denied, await RefusedAsync(await user.GetAsync("/api/shares/details", Token)));
            Assert.Equal(denied, await RefusedAsync(await user.GetAsync($"/api/shares/{_shared}/access", Token)));
            Assert.Equal(denied, await RefusedAsync(await user.PatchAsJsonAsync($"/api/shares/{_shared}", new { description = "mine" }, Token)));
            Assert.Equal(denied, await RefusedAsync(await PlanAsync(user, new { action = "share.create", name = "Mine", pool = "pithyxtestm" })));
            Assert.Equal(denied, await RefusedAsync(await PlanAsync(user, new { action = "share.delete", shareId = _shared, confirmName = "Shared", data = "keep" })));
            Assert.Equal(denied, await RefusedAsync(await user.PostAsync("/api/shares", new { name = "Mine", path = _factory.DataDirectory })));
        }
    }

    [Fact]
    public async Task CreatingNeedsAPoolWithThePithyxLayout()
    {
        Assert.Equal((HttpStatusCode.Conflict, "fs.share.pool_not_prepared"), await RefusedAsync(await PlanAsync(_admin, new { action = "share.create", name = "Media", pool = "pithyxtestm" })));
        Assert.Equal((HttpStatusCode.NotFound, "storage.pool.not_found"), await RefusedAsync(await PlanAsync(_admin, new { action = "share.create", name = "Media", pool = "nope" })));
        Assert.Equal((HttpStatusCode.BadRequest, "storage.plan.action_invalid"), await RefusedAsync(await PlanAsync(_admin, new { action = "share.rename" })));
        // The storage planner no longer creates share datasets on its own.
        Assert.Equal((HttpStatusCode.BadRequest, "storage.plan.action_invalid"), await RefusedAsync(await _admin.PostAsJsonAsync("/api/storage/plans", new { action = "dataset.create", pool = "pithyxtestm", name = "media" }, Token)));
    }

    [Fact]
    public async Task DeletingNeedsTheTypedNameAndAChoiceAndKeepRunsAsAPlan()
    {
        Assert.Equal((HttpStatusCode.BadRequest, "fs.share.name_mismatch"), await RefusedAsync(await PlanAsync(_admin, new { action = "share.delete", shareId = _shared, confirmName = "shared", data = "keep" })));
        Assert.Equal((HttpStatusCode.BadRequest, "fs.share.data_choice_required"), await RefusedAsync(await PlanAsync(_admin, new { action = "share.delete", shareId = _shared, confirmName = "Shared" })));
        Assert.Equal((HttpStatusCode.Conflict, "fs.share.no_dataset"), await RefusedAsync(await PlanAsync(_admin, new { action = "share.delete", shareId = _shared, confirmName = "Shared", data = "destroy" })));
        Assert.Equal((HttpStatusCode.BadRequest, "request.body.invalid"), await RefusedAsync(await PlanAsync(_admin, new { action = "share.delete", shareId = _shared, confirmName = "Shared", data = "maybe" })));

        using var planned = await PlanAsync(_admin, new { action = "share.delete", shareId = _shared, confirmName = "Shared", data = "keep" });
        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
        var preview = await planned.JsonAsync();
        Assert.Equal(0, preview.GetProperty("commands").GetArrayLength());
        Assert.Equal(0, preview.GetProperty("datasets").GetArrayLength());
        using var run = await _admin.PostAsync($"/api/storage/plans/{preview.GetProperty("planId").GetString()}/run", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var job = await run.JsonAsync();
        Assert.Equal("storage.share.delete", job.GetProperty("kind").GetString());
        await _factory.Services.GetRequiredService<JobService>().WaitForStateAsync(job.GetProperty("id").GetInt64(), JobState.Succeeded);

        Assert.Empty((await (await _admin.GetAsync("/api/shares", Token)).JsonAsync()).EnumerateArray());
        // Keep means keep: the folder and its files are still there.
        Assert.True(Directory.Exists(Path.Combine(_factory.DataDirectory, "shares", "shared")));
    }

    [Fact]
    public async Task RegisteringAFolderIsRefusedOnProd()
    {
        await using var prod = new ProdDaemonFactory();
        using var admin = prod.CreateApiClient();
        await admin.SetupAdminAsync();

        var folder = Directory.CreateDirectory(Path.Combine(prod.DataDirectory, "folder")).FullName;
        Assert.Equal((HttpStatusCode.Forbidden, "fs.share.folder_dev_only"), await RefusedAsync(await admin.PostAsync("/api/shares", new { name = "Folder", path = folder })));
    }

    [Fact]
    public async Task TheTopicTellsEveryUserThatTheListChangedAndNothingElse()
    {
        var (_, anna) = await UserAsync("anna");
        using var socket = await ConnectAsync(await _factory.SessionCookieAsync(anna));

        await SendAsync(socket, new { type = "sub", id = "c1", topic = "shares/list" });
        Assert.Equal("ack", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        var snapshot = await ReceiveAsync(socket);
        Assert.Equal("snap", snapshot.GetProperty("type").GetString());
        var revision = snapshot.GetProperty("data").GetProperty("revision").GetInt64();

        // A change anna has no part in: she learns that something changed, but not what.
        (await _admin.PatchAsJsonAsync($"/api/shares/{_shared}", new { description = "Secret project" }, Token)).EnsureSuccessStatusCode();
        var changed = await ReceiveAsync(socket);
        Assert.Equal(("event", "shares/list"), (changed.GetProperty("type").GetString(), changed.GetProperty("topic").GetString()));
        var data = changed.GetProperty("data");
        Assert.Equal(["revision"], data.EnumerateObject().Select(property => property.Name));
        Assert.NotEqual(revision, data.GetProperty("revision").GetInt64());
        Assert.DoesNotContain("Shared", changed.GetRawText(), StringComparison.Ordinal);

        (await _admin.PutAsJsonAsync($"/api/shares/{_shared}/access/{anna}", new { access = "read" }, Token)).EnsureSuccessStatusCode();
        Assert.Equal("event", (await ReceiveAsync(socket)).GetProperty("type").GetString());
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

    private static Task SendAsync(WebSocket socket, object message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, endOfMessage: true, Token);

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    /// <summary>A daemon whose <c>/etc/pithyx</c> says nothing, so it is prod.</summary>
    private sealed class ProdDaemonFactory : DaemonFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PITHYX_ETC_DIR", Directory.CreateDirectory(Path.Combine(DataDirectory, "etc")).FullName);
        }
    }
}
