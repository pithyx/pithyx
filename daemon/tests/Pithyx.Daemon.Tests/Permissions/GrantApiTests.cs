using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Permissions;

public sealed class GrantApiTests : IDisposable
{
    private const string Password = "river-candle-orbit-7";
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public GrantApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static object Grants(params string[] resources) => new { grants = resources.Select(resource => new { resource, level = "allow" }) };

    private async Task<long> UserAsync(string username, params string[] rights)
    {
        using var response = await _admin.PostAsJsonAsync("/api/users", new { username, password = Password, grants = rights.Select(resource => new { resource, level = "allow" }) }, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetInt64();
    }

    private async Task<long> GroupAsync(string name, params long[] memberIds)
    {
        using var response = await _admin.PostAsJsonAsync("/api/groups", new { name, memberIds }, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetInt64();
    }

    private async Task<long> SharedAsync() =>
        (await _admin.GetFromJsonAsync<JsonElement>("/api/shares", Token)).EnumerateArray().Single(share => share.GetProperty("name").GetString() == "Shared").GetProperty("id").GetInt64();

    private async Task<HttpClient> SignInAsync(string username)
    {
        var client = _factory.CreateApiClient();
        using var login = await client.LoginAsync(username, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static async Task<string?> AccessAsync(HttpClient client, long shareId) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/shares", Token)).EnumerateArray()
            .Where(share => share.GetProperty("id").GetInt64() == shareId)
            .Select(share => share.GetProperty("access").GetString())
            .SingleOrDefault();

    [Fact]
    public async Task AGroupsGrantReachesItsMembersAndLeavesWithTheMembershipAtTheNextRequest()
    {
        await _admin.SetupAdminAsync();
        var shared = await SharedAsync();
        var rita = await UserAsync("rita", "right:desktopLogin");
        var media = await GroupAsync("media", rita);
        using var ritaClient = await SignInAsync("rita");
        Assert.Null(await AccessAsync(ritaClient, shared));

        await _admin.SetShareAccessAsync(shared, rita, "read");
        using (var groupGrant = await _admin.PutAsJsonAsync($"/api/shares/{shared}/grants", new { grants = new object[] { new { subjectKind = "user", subjectId = rita, level = "read" }, new { subjectKind = "group", subjectId = media, level = "readWrite" } } }, Token))
        {
            var users = (await groupGrant.JsonAsync()).GetProperty("users").EnumerateArray().ToDictionary(user => user.GetProperty("username").GetString()!, user => user.GetProperty("access").GetString());
            Assert.Equal("readWrite", users["rita"]);
            Assert.Equal("readWrite", users["admin"]);
        }
        Assert.Equal("readWrite", await AccessAsync(ritaClient, shared));

        // Out of the group: only her own read is left, at once.
        using (var leave = await _admin.PatchAsJsonAsync($"/api/groups/{media}", new { memberIds = Array.Empty<long>() }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, leave.StatusCode);
        }
        Assert.Equal("read", await AccessAsync(ritaClient, shared));
        using var write = await ritaClient.PutAsync($"/api/files/{shared}/content?path=x.txt", new StringContent("x"), Token);
        Assert.Equal("fs.share.read_only", await write.ErrorCodeAsync());

        // The built-in group gives it to everybody.
        using (await _admin.PutAsJsonAsync("/api/groups/100/grants", Grants("protocol:ftp"), Token))
        {
        }
        var grants = await _admin.GetFromJsonAsync<JsonElement>($"/api/users/{rita}/grants", Token);
        Assert.Contains("protocol:ftp", grants.GetProperty("effective").GetProperty("protocols").EnumerateArray().Select(protocol => protocol.GetString()));
        Assert.Contains(grants.GetProperty("inherited").EnumerateArray(), grant => grant.GetProperty("groupName").GetString() == "users");
    }

    [Fact]
    public async Task WithoutTheDesktopLoginRightTheSessionEndsAndSigningInIsRefused()
    {
        await _admin.SetupAdminAsync();
        var rita = await UserAsync("rita", "right:desktopLogin", "app:org.pithyx.files");
        using var ritaClient = await SignInAsync("rita");
        Assert.Equal(["org.pithyx.files"], (await ritaClient.GetFromJsonAsync<JsonElement>("/api/auth/session", Token)).GetProperty("permissions").GetProperty("apps").EnumerateArray().Select(app => app.GetString()));

        using (await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", Grants("app:org.pithyx.files"), Token))
        {
        }

        using var session = await ritaClient.GetAsync("/api/auth/session", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        using var again = _factory.CreateApiClient();
        using (var wrong = await again.LoginAsync("rita", "wrong-candle-orbit-7"))
        {
            Assert.Equal("auth.credentials.invalid", await wrong.ErrorCodeAsync());
        }
        using var refused = await again.LoginAsync("rita", Password);
        Assert.Equal("auth.desktop.not_allowed", await refused.ErrorCodeAsync());

        // Through a group it works again.
        var desk = await GroupAsync("desk", rita);
        using (await _admin.PutAsJsonAsync($"/api/groups/{desk}/grants", Grants("right:desktopLogin"), Token))
        {
        }
        using var back = await again.LoginAsync("rita", Password);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
    }

    [Fact]
    public async Task AnAdministratorThroughAGroupCountsAndTheLastOneStays()
    {
        await _admin.SetupAdminAsync();
        var adminId = (await _admin.GetFromJsonAsync<JsonElement>("/api/users", Token))[0].GetProperty("id").GetInt64();
        var bea = await UserAsync("bea", "right:desktopLogin");
        var admins = await GroupAsync("admins", bea);
        using (var granted = await _admin.PutAsJsonAsync($"/api/groups/{admins}/grants", Grants("right:admin"), Token))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }
        using (var beaClient = await SignInAsync("bea"))
        using (var users = await beaClient.GetAsync("/api/users", Token))
        {
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        }

        // The setup administrator may give up the right while bea has it through the group.
        using (var demoted = await _admin.PutAsJsonAsync($"/api/users/{adminId}/grants", Grants("right:desktopLogin"), Token))
        {
            Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        }
        using var beaAdmin = await SignInAsync("bea");
        // Now the group holds the last administrator: neither its right, its member nor the group itself may go.
        using (var revoke = await beaAdmin.PutAsJsonAsync($"/api/groups/{admins}/grants", Grants(), Token))
        {
            Assert.Equal("users.user.last_admin", await revoke.ErrorCodeAsync());
        }
        using (var leave = await beaAdmin.PatchAsJsonAsync($"/api/groups/{admins}", new { memberIds = Array.Empty<long>() }, Token))
        {
            Assert.Equal("users.user.last_admin", await leave.ErrorCodeAsync());
        }
        using (var delete = await beaAdmin.DeleteAsync($"/api/groups/{admins}", Token))
        {
            Assert.Equal("users.user.last_admin", await delete.ErrorCodeAsync());
        }
        // The former administrator lost every administrator endpoint with the next request.
        using var forbidden = await _admin.GetAsync("/api/users", Token);
        Assert.Equal("auth.permission.denied", await forbidden.ErrorCodeAsync());
    }

    [Fact]
    public async Task UnknownResourcesAndWrongLevelsAreRefused()
    {
        await _admin.SetupAdminAsync();
        var rita = await UserAsync("rita", "right:desktopLogin");
        var shared = await SharedAsync();
        foreach (var grant in new object[]
        {
            new { resource = "right:root", level = "allow" },
            new { resource = "app:org.pithyx.storage", level = "allow" },
            new { resource = "app:files", level = "allow" },
            new { resource = $"share:{shared}", level = "allow" },
            new { resource = "protocol:smb", level = "read" },
        })
        {
            using var response = await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", new { grants = new[] { grant } }, Token);
            Assert.Equal("permissions.grant.invalid", await response.ErrorCodeAsync());
        }
        using var twice = await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", Grants("protocol:smb", "protocol:smb"), Token);
        Assert.Equal("permissions.grant.invalid", await twice.ErrorCodeAsync());
        using var noShare = await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", new { grants = new[] { new { resource = "share:4711", level = "read" } } }, Token);
        Assert.Equal("fs.share.not_found", await noShare.ErrorCodeAsync());
        using var noUser = await _admin.PutAsJsonAsync("/api/users/4711/grants", Grants(), Token);
        Assert.Equal("users.user.not_found", await noUser.ErrorCodeAsync());
        using var noGroup = await _admin.PutAsJsonAsync("/api/groups/4711/grants", Grants(), Token);
        Assert.Equal("users.group.not_found", await noGroup.ErrorCodeAsync());
    }

    [Fact]
    public async Task ARevokedRightIsPushedOverTheWebSocket()
    {
        await _admin.SetupAdminAsync();
        var rita = await UserAsync("rita", "right:desktopLogin", "app:org.pithyx.files", "app:org.pithyx.viewer");
        var cookie = await Jobs.JobTestSupport.SessionCookieAsync(_factory, rita);
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "http://localhost";
        };
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"sub","id":"p","topic":"session/permissions"}"""), WebSocketMessageType.Text, endOfMessage: true, Token);
        Assert.Equal("ack", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        var snapshot = await ReceiveAsync(socket);
        Assert.Equal(["org.pithyx.files", "org.pithyx.viewer"], snapshot.GetProperty("data").GetProperty("apps").EnumerateArray().Select(app => app.GetString()));

        using (await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", Grants("right:desktopLogin", "app:org.pithyx.files"), Token))
        {
        }

        var changed = await ReceiveAsync(socket);
        Assert.Equal(("event", "session/permissions"), (changed.GetProperty("type").GetString(), changed.GetProperty("topic").GetString()));
        Assert.Equal(["org.pithyx.files"], changed.GetProperty("data").GetProperty("apps").EnumerateArray().Select(app => app.GetString()));

        // Without the desktop login the connection ends.
        using (await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", Grants(), Token))
        {
        }
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
        }
        while (result.MessageType != WebSocketMessageType.Close);
        Assert.Equal(4401, (int?)socket.CloseStatus);
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
