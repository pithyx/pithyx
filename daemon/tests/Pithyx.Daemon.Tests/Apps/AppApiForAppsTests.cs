using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>
/// App tokens and the daemon's API for apps (A11, D303) in simulation: who gets a token, what a token is good for, that
/// the app and the user come from the token alone, and that every call checks again the session, the user, the grant,
/// the app and its permissions.
/// </summary>
public sealed class AppApiForAppsTests : IAsyncLifetime
{
    private const string AppA = "com.example.alpha";
    private const string AppB = "com.example.beta";
    private const string Pool = "pithyxtestm";
    private const string Backend = """{"compose":"backend/compose.yaml","endpoints":{"api":{"service":"web","port":8080},"metrics":{"service":"web","port":9090}},"lifecycle":"onDemand"}""";

    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly List<HttpClient> _clients = [];
    private long _adminId;

    public AppApiForAppsTests() => _admin = _factory.CreateApiClient();

    public async ValueTask InitializeAsync()
    {
        await _admin.SetupAdminAsync();
        _adminId = await _factory.ScalarAsync<long>("SELECT id FROM users WHERE username = 'admin'");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
        _admin.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private AppTokens Tokens => _factory.Services.GetRequiredService<AppTokens>();

    private AppRegistry Registry => _factory.Services.GetRequiredService<AppRegistry>();

    private async Task InstallAsync(string id, string permissions, string access = "admins", string extra = "")
    {
        var package = TarBuilder.MinimalPackage(id, "1.0.0", TarBuilder.Manifest(id, "1.0.0", permissions, extra)).End().Gzip();
        using var upload = await _admin.PutAsync("/api/apps/packages", new ByteArrayContent(package), Token);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var packageId = (await upload.JsonAsync()).GetProperty("packageId").GetString();
        using var install = await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", new { pool = Pool, access }, Token);
        Assert.Equal(HttpStatusCode.Accepted, install.StatusCode);
        await FinishedAsync(await install.JsonAsync());
    }

    private async Task FinishedAsync(JsonElement started)
    {
        var id = started.GetProperty("id").GetInt64();
        Job? job = null;
        var jobs = _factory.Services.GetRequiredService<JobService>();
        await JobTestSupport.WaitUntilAsync(async () => (job = await jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State is not (JobState.Queued or JobState.Running), $"job {id}");
        Assert.Equal(JobState.Succeeded, job!.State);
    }

    /// <summary>Gives an installed app a backend in its manifest; A10 installs frontends only until the Docker runtime.</summary>
    private async Task AddBackendAsync(string appId)
    {
        await _factory.ScalarAsync<long>("UPDATE apps SET manifest = json_set(manifest, '$.backend', json(@Backend)) WHERE id = @appId RETURNING 1", new { Backend, appId });
        await Registry.ReloadAsync(Token);
    }

    private async Task<(HttpClient Client, long Id, string Cookie)> UserAsync(string username, params string[] apps)
    {
        long id;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            id = await TestUsers.InsertAsync(connection, username);
            foreach (var app in apps)
            {
                await TestUsers.GrantAsync(connection, id, "app:" + app);
            }
        }
        _factory.Services.GetRequiredService<Pithyx.Daemon.Permissions.PermissionService>().Invalidate();
        var cookie = await _factory.SessionCookieAsync(id);
        var client = _factory.ClientWithCookie(cookie);
        _clients.Add(client);
        return (client, id, cookie);
    }

    private static async Task<HttpResponseMessage> RequestTokenAsync(HttpClient session, string appId, string kind = "host", string instance = "w1") =>
        await session.PostAsJsonAsync($"/api/apps/{appId}/tokens", new { kind, instance }, Token);

    private static async Task<string> TokenAsync(HttpClient session, string appId, string kind = "host", string instance = "w1")
    {
        using var response = await RequestTokenAsync(session, appId, kind, instance);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("token").GetString()!;
    }

    /// <summary>A client that sends only the token (and the CSRF header), no cookie.</summary>
    private HttpClient App(string? token)
    {
        var client = _factory.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        _clients.Add(client);
        return client;
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode ? null : await response.ErrorCodeAsync();

    private static async Task AssertRefusedAsync(HttpClient app, string code = "app.token.invalid")
    {
        using var response = await app.GetAsync("/api/app/storage", Token);
        Assert.Equal(code, await ErrorAsync(response));
    }

    private static async Task AssertWorksAsync(HttpClient app)
    {
        using var response = await app.GetAsync("/api/app/storage", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ATokenNeedsTheCsrfHeaderAGrantAPlainInstanceAndABackendForABackendToken()
    {
        await InstallAsync(AppA, "\"notifications\"");
        var (rita, _, ritaCookie) = await UserAsync("rita");

        Assert.Equal("app.app.not_found", await ErrorAsync(await RequestTokenAsync(rita, AppA)));
        Assert.Equal("app.app.not_found", await ErrorAsync(await RequestTokenAsync(_admin, "com.example.missing")));
        Assert.Equal("request.body.invalid", await ErrorAsync(await RequestTokenAsync(_admin, AppA, instance: "../w1")));
        Assert.Equal("request.body.invalid", await ErrorAsync(await RequestTokenAsync(_admin, AppA, kind: "ticket")));
        Assert.Equal("app.backend.missing", await ErrorAsync(await RequestTokenAsync(_admin, AppA, kind: "backend")));

        using (var withoutCsrf = _factory.CreateClient(new() { HandleCookies = false }))
        {
            withoutCsrf.DefaultRequestHeaders.Add("Cookie", ritaCookie);
            using var response = await withoutCsrf.PostAsJsonAsync($"/api/apps/{AppA}/tokens", new { kind = "host", instance = "w1" }, Token);
            Assert.Equal("auth.csrf.header_missing", await ErrorAsync(response));
        }
        // A host token is no session: it cannot get another token.
        using (var minted = await App(await TokenAsync(_admin, AppA)).PostAsJsonAsync($"/api/apps/{AppA}/tokens", new { kind = "host", instance = "w2" }, Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, minted.StatusCode);
        }

        using var issued = await RequestTokenAsync(_admin, AppA);
        var body = await issued.JsonAsync();
        Assert.Equal(43, body.GetProperty("token").GetString()!.Length);
        Assert.Equal(_factory.Time.GetUtcNow() + AppTokens.TokenLifetime, body.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task TheApiForAppsTakesOnlyAValidHostToken()
    {
        await InstallAsync(AppA, "\"notifications\"");
        await AddBackendAsync(AppA);

        await AssertRefusedAsync(App(null));
        await AssertRefusedAsync(App("not-a-token"));
        await AssertRefusedAsync(App(await TokenAsync(_admin, AppA, kind: "backend")));
        // The session cookie alone is not enough, not even an administrator's.
        await AssertRefusedAsync(_admin);

        var host = App(await TokenAsync(_admin, AppA));
        await AssertWorksAsync(host);
        using (var ticketResponse = await host.PostAsJsonAsync("/api/app/tickets", new { endpoint = "api" }, Token))
        {
            await AssertRefusedAsync(App((await ticketResponse.JsonAsync()).GetProperty("ticket").GetString()));
        }

        _factory.Time.Advance(AppTokens.TokenLifetime - TimeSpan.FromSeconds(1));
        await AssertWorksAsync(host);
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        await AssertRefusedAsync(host);
    }

    [Fact]
    public async Task TheAppAndTheUserComeFromTheTokenAlone()
    {
        await InstallAsync(AppA, "\"notifications\"", access: "everybody");
        await InstallAsync(AppB, "\"files.read\"", access: "everybody");
        var (rita, ritaId, _) = await UserAsync("rita");
        var alpha = App(await TokenAsync(_admin, AppA));
        var beta = App(await TokenAsync(_admin, AppB));
        var ritaAlpha = App(await TokenAsync(rita, AppA));

        using (var set = await alpha.PutAsJsonAsync($"/api/app/storage/value?key=k&app={AppB}", new { value = new { theme = "dark" } }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }
        Assert.Equal("dark", (await alpha.GetFromJsonAsync<JsonElement>("/api/app/storage/value?key=k", Token)).GetProperty("value").GetProperty("theme").GetString());
        Assert.Equal(JsonValueKind.Null, (await beta.GetFromJsonAsync<JsonElement>($"/api/app/storage/value?key=k&app={AppA}", Token)).GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ritaAlpha.GetFromJsonAsync<JsonElement>("/api/app/storage/value?key=k", Token)).GetProperty("value").ValueKind);
        Assert.Equal(0, (await beta.GetFromJsonAsync<JsonElement>("/api/app/storage", Token)).GetProperty("keys").GetArrayLength());

        // Rita's token with the administrator's cookie on the same request still acts for Rita.
        using (var mixed = new HttpRequestMessage(HttpMethod.Get, "/api/app/storage/value?key=k"))
        {
            mixed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ritaAlpha.DefaultRequestHeaders.Authorization!.Parameter);
            using var response = await _admin.SendAsync(mixed, Token);
            Assert.Equal(JsonValueKind.Null, (await response.JsonAsync()).GetProperty("value").ValueKind);
        }

        // B holds no notifications permission; that A holds it does not help B.
        using (var notify = await beta.PostAsJsonAsync("/api/app/notifications", new { title = "Hi" }, Token))
        {
            var problem = await notify.JsonAsync();
            Assert.Equal("app.permission.denied", problem.GetProperty("code").GetString());
            Assert.Equal("notifications", problem.GetProperty("params").GetProperty("permission").GetString());
        }
        using (var notify = await ritaAlpha.PostAsJsonAsync("/api/app/notifications", new { title = "Hi" }, Token))
        {
            Assert.Equal(HttpStatusCode.Created, notify.StatusCode);
            Assert.Equal(AppA, (await notify.JsonAsync()).GetProperty("app").GetString());
        }
        Assert.Equal(1, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE user_id = @ritaId AND app = @AppA", new { ritaId, AppA }));
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE user_id = @_adminId AND app IS NOT NULL", new { _adminId }));
    }

    [Fact]
    public async Task ATokenEndsWithItsSessionAndItsUser()
    {
        await InstallAsync(AppA, "\"notifications\"", access: "everybody");
        var (rita, ritaId, _) = await UserAsync("rita");

        var app = App(await TokenAsync(rita, AppA));
        await AssertWorksAsync(app);
        using (var logout = await rita.PostAsJsonAsync("/api/auth/logout", new { }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        await AssertRefusedAsync(app);

        // A session that ends elsewhere (revoked, expired) takes its tokens along on their next use.
        var session = _factory.ClientWithCookie(await _factory.SessionCookieAsync(ritaId));
        _clients.Add(session);
        app = App(await TokenAsync(session, AppA));
        await AssertWorksAsync(app);
        await _factory.ScalarAsync<long>("DELETE FROM sessions WHERE user_id = @ritaId RETURNING 1", new { ritaId });
        await AssertRefusedAsync(app);

        session = _factory.ClientWithCookie(await _factory.SessionCookieAsync(ritaId));
        _clients.Add(session);
        app = App(await TokenAsync(session, AppA));
        using (var deactivate = await _admin.PatchAsJsonAsync($"/api/users/{ritaId}", new { disabled = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        }
        await AssertRefusedAsync(app);
    }

    [Fact]
    public async Task ATokenEndsWithTheGrantTheAppAndItsRemoval()
    {
        await InstallAsync(AppA, "\"notifications\"");
        var (rita, ritaId, _) = await UserAsync("rita", AppA);

        var app = App(await TokenAsync(rita, AppA));
        await AssertWorksAsync(app);
        using (var revoke = await _admin.PutAsJsonAsync($"/api/users/{ritaId}/grants", new { grants = new[] { new { resource = "right:desktopLogin", level = "allow" } } }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }
        await AssertRefusedAsync(app);
        Assert.Equal("app.app.not_found", await ErrorAsync(await RequestTokenAsync(rita, AppA)));

        app = App(await TokenAsync(_admin, AppA));
        using (var disable = await _admin.PatchAsJsonAsync($"/api/apps/{AppA}", new { enabled = false }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        }
        await AssertRefusedAsync(app);
        using (var enable = await _admin.PatchAsJsonAsync($"/api/apps/{AppA}", new { enabled = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        }

        app = App(await TokenAsync(_admin, AppA));
        using (var set = await app.PutAsJsonAsync("/api/app/storage/value?key=k", new { value = 1 }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }
        using (var remove = await _admin.DeleteAsync($"/api/apps/{AppA}?data=keep", Token))
        {
            await FinishedAsync(await remove.JsonAsync());
        }
        await AssertRefusedAsync(app);
        // Keeping the data keeps what the app stored; deleting it does not.
        Assert.Equal(1, await _factory.ScalarAsync<long>("SELECT count(*) FROM app_storage WHERE app_id = @AppA", new { AppA }));
        await InstallAsync(AppA, "\"notifications\"");
        using (var remove = await _admin.DeleteAsync($"/api/apps/{AppA}?data=delete", Token))
        {
            await FinishedAsync(await remove.JsonAsync());
        }
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM app_storage WHERE app_id = @AppA", new { AppA }));
    }

    [Fact]
    public async Task ARevokedPermissionFailsTheNextCallAndAnUnknownOneIsRefused()
    {
        await InstallAsync(AppA, "\"notifications\", \"directory.read\"");
        var app = App(await TokenAsync(_admin, AppA));
        using (var notify = await app.PostAsJsonAsync("/api/app/notifications", new { title = "Hi" }, Token))
        {
            Assert.Equal(HttpStatusCode.Created, notify.StatusCode);
        }

        using (var revoke = await _admin.PutAsJsonAsync($"/api/apps/{AppA}/permissions", new { permissions = new[] { new { permission = "notifications", granted = false } } }, Token))
        {
            var details = await revoke.JsonAsync();
            Assert.Contains(details.GetProperty("permissions").EnumerateArray(), state => state.GetProperty("permission").GetString() == "notifications" && !state.GetProperty("granted").GetBoolean());
        }
        using (var notify = await app.PostAsJsonAsync("/api/app/notifications", new { title = "Hi" }, Token))
        {
            Assert.Equal("app.permission.denied", await ErrorAsync(notify));
        }
        using (var users = await app.GetAsync("/api/app/directory/users", Token))
        {
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        }
        using var unknown = await _admin.PutAsJsonAsync($"/api/apps/{AppA}/permissions", new { permissions = new[] { new { permission = "files.write", granted = true } } }, Token);
        Assert.Equal("app.permission.not_requested", await ErrorAsync(unknown));
    }

    [Fact]
    public async Task TicketsAndBackendTokensNeedABackendAndATicketWorksOnceForThirtySeconds()
    {
        await InstallAsync(AppA, "\"notifications\"");
        var app = App(await TokenAsync(_admin, AppA, instance: "w7"));
        using (var none = await app.PostAsJsonAsync("/api/app/tickets", new { endpoint = "api" }, Token))
        {
            Assert.Equal("app.backend.endpoint_unknown", await ErrorAsync(none));
        }

        await AddBackendAsync(AppA);
        using (var unknown = await app.PostAsJsonAsync("/api/app/tickets", new { endpoint = "admin" }, Token))
        {
            Assert.Equal("app.backend.endpoint_unknown", await ErrorAsync(unknown));
        }
        string ticket;
        using (var issued = await app.PostAsJsonAsync("/api/app/tickets", new { endpoint = "metrics" }, Token))
        {
            var body = await issued.JsonAsync();
            ticket = body.GetProperty("ticket").GetString()!;
            Assert.Equal(_factory.Time.GetUtcNow() + AppTokens.TicketLifetime, body.GetProperty("expiresAt").GetDateTimeOffset());
        }
        var grant = Tokens.RedeemTicket(ticket);
        Assert.NotNull(grant);
        Assert.Equal((AppTokenKind.Ticket, _adminId, AppA, "w7", "metrics"), (grant.Kind, grant.UserId, grant.AppId, grant.Instance, grant.Endpoint));
        Assert.Null(Tokens.RedeemTicket(ticket));

        using (var late = await app.PostAsJsonAsync("/api/app/tickets", new { endpoint = "api" }, Token))
        {
            ticket = (await late.JsonAsync()).GetProperty("ticket").GetString()!;
        }
        _factory.Time.Advance(AppTokens.TicketLifetime + TimeSpan.FromSeconds(1));
        Assert.Null(Tokens.RedeemTicket(ticket));

        var backend = await TokenAsync(_admin, AppA, kind: "backend", instance: "w7");
        Assert.Equal(AppA, Tokens.Validate(backend, AppTokenKind.Backend)?.AppId);
        Assert.Null(Tokens.Validate(backend, AppTokenKind.Host));
    }

    [Fact]
    public async Task TheDirectoryNeedsDirectoryReadAndLeavesOutDeactivatedUsersAndTheBuiltInGroup()
    {
        await InstallAsync(AppA, "\"notifications\"");
        await InstallAsync(AppB, "\"directory.read\"");
        var (_, ritaId, _) = await UserAsync("rita");
        var (_, goneId, _) = await UserAsync("gone");
        using (var group = await _admin.PostAsJsonAsync("/api/groups", new { name = "family", description = "Us", memberIds = new[] { ritaId } }, Token))
        {
            Assert.True(group.IsSuccessStatusCode, await group.Content.ReadAsStringAsync(Token));
        }
        using (var deactivate = await _admin.PatchAsJsonAsync($"/api/users/{goneId}", new { disabled = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        }

        var without = App(await TokenAsync(_admin, AppA));
        using (var users = await without.GetAsync("/api/app/directory/users", Token))
        {
            Assert.Equal("app.permission.denied", await ErrorAsync(users));
        }
        using (var groups = await without.GetAsync("/api/app/directory/groups", Token))
        {
            Assert.Equal("app.permission.denied", await ErrorAsync(groups));
        }

        var with = App(await TokenAsync(_admin, AppB));
        var listed = await with.GetFromJsonAsync<JsonElement>("/api/app/directory/users", Token);
        Assert.Equal(["admin", "rita"], listed.EnumerateArray().Select(user => user.GetProperty("name").GetString()));
        var family = Assert.Single((await with.GetFromJsonAsync<JsonElement>("/api/app/directory/groups", Token)).EnumerateArray());
        Assert.Equal("family", family.GetProperty("name").GetString());
        Assert.Equal([family.GetProperty("id").GetInt64()], listed[1].GetProperty("groups").EnumerateArray().Select(id => id.GetInt64()));
    }

    [Fact]
    public async Task DebugLogsReachTheDaemonOnlyWhileSwitchedOnWithTheCheckedUser()
    {
        await InstallAsync(AppA, "\"notifications\"");
        var app = App(await TokenAsync(_admin, AppA));
        async Task WriteAsync(string suffix)
        {
            using var response = await app.PostAsJsonAsync("/api/app/logs", new { entries = new[] { new { level = "debug", message = "debug " + suffix }, new { level = "info", message = "info " + suffix } } }, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await WriteAsync("off");
        var entries = await EntriesAsync($"source=app/{AppA}", 1);
        Assert.Equal(["info off"], entries.Select(entry => entry.GetProperty("message").GetString()));
        Assert.Equal("admin", entries[0].GetProperty("user").GetString());

        using (var on = await _admin.PutAsJsonAsync($"/api/apps/{AppA}/log-level", new { level = "debug" }, Token))
        {
            Assert.Equal("debug", (await on.JsonAsync()).GetProperty("logLevel").GetString());
        }
        await WriteAsync("on");
        Assert.Contains("debug on", (await EntriesAsync($"source=app/{AppA}", 3)).Select(entry => entry.GetProperty("message").GetString()));

        // Third-party apps no longer write through the desktop's route (D261).
        using var old = await _admin.PostAsJsonAsync($"/api/apps/{AppA}/logs", new { entries = Array.Empty<object>() }, Token);
        Assert.Equal("auth.permission.denied", await ErrorAsync(old));
    }

    private async Task<List<JsonElement>> EntriesAsync(string query, int atLeast)
    {
        for (var attempt = 0; ; attempt++)
        {
            var entries = (await _admin.GetFromJsonAsync<JsonElement>($"/api/logs?{query}", Token)).GetProperty("entries");
            if (entries.GetArrayLength() >= atLeast || attempt > 50)
            {
                return [.. entries.EnumerateArray()];
            }
            await Task.Delay(100, Token);
        }
    }

    [Fact]
    public async Task StoredValuesKeepTheirLimits()
    {
        await InstallAsync(AppA, "\"notifications\"");
        var app = App(await TokenAsync(_admin, AppA));
        Task<HttpResponseMessage> SetAsync(string key, object value) => app.PutAsJsonAsync($"/api/app/storage/value?key={Uri.EscapeDataString(key)}", new { value }, Token);

        Assert.Equal("app.storage.key_invalid", await ErrorAsync(await SetAsync(new string('k', AppValueStore.MaxKeyLength + 1), 1)));
        Assert.Equal("app.storage.key_invalid", await ErrorAsync(await app.GetAsync("/api/app/storage/value", Token)));
        Assert.Equal("app.storage.too_large", await ErrorAsync(await SetAsync("big", new string('x', AppValueStore.MaxValueBytes))));
        for (var i = 0; i < 16; i++)
        {
            Assert.Null(await ErrorAsync(await SetAsync($"part/{i}", new string('x', 63 * 1024))));
        }
        Assert.Equal("app.storage.too_large", await ErrorAsync(await SetAsync("one more", new string('x', 63 * 1024))));
        // Replacing a value counts only the new one.
        Assert.Null(await ErrorAsync(await SetAsync("part/0", new string('y', 63 * 1024))));
        using (var remove = await app.DeleteAsync("/api/app/storage/value?key=part%2F0", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        }
        var keys = (await app.GetFromJsonAsync<JsonElement>("/api/app/storage", Token)).GetProperty("keys");
        Assert.Equal(15, keys.GetArrayLength());
        Assert.DoesNotContain("part/0", keys.EnumerateArray().Select(key => key.GetString()));
    }

    [Fact]
    public async Task NotificationsKeepTheirButtonsAndDataAndATagReplacesTheEarlierOne()
    {
        await InstallAsync(AppA, "\"notifications\"");
        var app = App(await TokenAsync(_admin, AppA));
        async Task<JsonElement> NotifyAsync(object request)
        {
            using var response = await app.PostAsJsonAsync("/api/app/notifications", request, Token);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await response.JsonAsync();
        }

        var first = await NotifyAsync(new { title = "New message", tag = "chat-1", actions = new[] { new { id = "reply", label = "Reply" } }, data = new { chat = 1 }, level = "error" });
        Assert.Equal("critical", first.GetProperty("tone").GetString());
        Assert.Equal("reply", first.GetProperty("params").GetProperty("actions")[0].GetProperty("id").GetString());
        Assert.Equal(1, first.GetProperty("params").GetProperty("data").GetProperty("chat").GetInt32());
        await NotifyAsync(new { title = "Other chat", tag = "chat-2" });
        var replaced = await NotifyAsync(new { title = "Two new messages", tag = "chat-1" });

        // Jobs (the install) notify too; only the app's own count here.
        var ids = (await _admin.GetFromJsonAsync<JsonElement>("/api/notifications", Token)).GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("app").ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("id").GetInt64())
            .ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(replaced.GetProperty("id").GetInt64(), ids);
        Assert.DoesNotContain(first.GetProperty("id").GetInt64(), ids);

        foreach (var invalid in new object[]
        {
            new { title = "x", actions = new[] { new { id = "a", label = "A" }, new { id = "b", label = "B" }, new { id = "c", label = "C" } } },
            new { title = "x", actions = new[] { new { id = "a", label = "A" }, new { id = "a", label = "B" } } },
            new { title = "x", actions = new[] { new { id = "a", label = new string('l', 41) } } },
            new { title = "x", data = new string('d', 5000) },
            new { title = "x", tag = new string('t', 65) },
            new { title = " " },
        })
        {
            using var response = await app.PostAsJsonAsync("/api/app/notifications", invalid, Token);
            Assert.Equal("notifications.message.invalid", await ErrorAsync(response));
        }
    }

    [Fact]
    public async Task AMutedCategoryArrivesAsReadAndAnUndeclaredOneIsRefused()
    {
        await InstallAsync(AppA, "\"notifications\"", extra: ",\n  \"notifications\": { \"categories\": [{ \"id\": \"messages\", \"name\": { \"en\": \"Messages\" } }, { \"id\": \"mentions\", \"name\": { \"en\": \"Mentions\" } }] }");
        var app = App(await TokenAsync(_admin, AppA));
        async Task<JsonElement> NotifyAsync(object request)
        {
            using var response = await app.PostAsJsonAsync("/api/app/notifications", request, Token);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await response.JsonAsync();
        }

        var categories = (await _admin.GetFromJsonAsync<JsonElement>("/api/notifications/categories", Token)).GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(["messages", "mentions"], categories.Select(category => category.GetProperty("id").GetString()));
        Assert.All(categories, category => Assert.False(category.GetProperty("muted").GetBoolean()));
        using (var mute = await _admin.PutAsJsonAsync($"/api/notifications/categories/{AppA}/mentions", new { muted = true }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, mute.StatusCode);
        }
        using (var unknown = await _admin.PutAsJsonAsync($"/api/notifications/categories/{AppA}/reminders", new { muted = true }, Token))
        {
            Assert.Equal("notifications.category.not_found", await ErrorAsync(unknown));
        }

        Assert.Equal(JsonValueKind.Null, (await NotifyAsync(new { title = "New message", category = "messages" })).GetProperty("readAt").ValueKind);
        Assert.Equal(JsonValueKind.String, (await NotifyAsync(new { title = "You were mentioned", category = "mentions" })).GetProperty("readAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await NotifyAsync(new { title = "No category" })).GetProperty("readAt").ValueKind);
        using (var undeclared = await app.PostAsJsonAsync("/api/app/notifications", new { title = "x", category = "reminders" }, Token))
        {
            Assert.Equal("notifications.message.invalid", await ErrorAsync(undeclared));
        }
        Assert.True((await _admin.GetFromJsonAsync<JsonElement>("/api/notifications/categories", Token)).GetProperty("categories")[1].GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task TheContextTopicIsOnlyForUsersOfTheAppAndFollowsItsChanges()
    {
        await InstallAsync(AppA, "\"notifications\", \"files.read\"", extra: ",\n  \"storage\": [{ \"id\": \"inbox\", \"name\": { \"en\": \"Inbox\" }, \"access\": \"readWrite\", \"required\": false }]");
        await InstallAsync(AppB, "\"notifications\"");
        var (_, ritaId, ritaCookie) = await UserAsync("rita", AppA);
        var leases = _factory.Services.GetRequiredService<AppLeases>();
        var topic = AppContexts.TopicOf(AppA);

        var socket = await ConnectAsync(ritaCookie);
        // Neither an app the user may not use nor a wildcard over every app's context answers.
        await SendAsync(socket, new { type = "sub", id = "b", topic = AppContexts.TopicOf(AppB) });
        var refused = await ReceiveAsync(socket);
        Assert.Equal(("error", "auth.permission.denied"), (refused.GetProperty("type").GetString(), refused.GetProperty("code").GetString()));
        await SendAsync(socket, new { type = "sub", id = "w", topic = AppContexts.Pattern });
        Assert.Equal("ack", (await ReceiveAsync(socket)).GetProperty("type").GetString());

        // The wildcard may deliver an event of the context (the install of AppB, the new lease) at any point from here on.
        await SendAsync(socket, new { type = "sub", id = "1", topic });
        Assert.Equal("ack", (await ReceiveReplyAsync(socket)).GetProperty("type").GetString());
        var snapshot = await ReceiveReplyAsync(socket);
        Assert.Equal(("snap", "1"), (snapshot.GetProperty("type").GetString(), snapshot.GetProperty("id").GetString()));
        var data = snapshot.GetProperty("data");
        Assert.Equal(["files.read", "notifications"], data.GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("info", data.GetProperty("logLevel").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("backend").ValueKind);
        var storage = Assert.Single(data.GetProperty("storage").EnumerateArray());
        Assert.Equal(("inbox", "unmapped"), (storage.GetProperty("id").GetString(), storage.GetProperty("state").GetString()));
        Assert.Equal(new Dictionary<long, int> { [ritaId] = 1 }, leases.Holders(AppA));

        using (var revoke = await _admin.PutAsJsonAsync($"/api/apps/{AppA}/permissions", new { permissions = new[] { new { permission = "notifications", granted = false } } }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }
        // An event of the lease or of AppB's install may still carry the old permissions, so wait for the revoke's.
        var changed = await NextContextAsync(socket, topic, data => data.GetProperty("permissions").GetArrayLength() == 1);
        Assert.Equal(["files.read"], changed.GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));
        using (var debug = await _admin.PutAsJsonAsync($"/api/apps/{AppA}/log-level", new { level = "debug" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, debug.StatusCode);
        }
        Assert.Equal("debug", (await NextContextAsync(socket, topic, data => data.GetProperty("logLevel").GetString() == "debug")).GetProperty("logLevel").GetString());

        // Losing the app ends the lease at once; the app leaves the user's topic apps, which closes its windows.
        using (var grants = await _admin.PutAsJsonAsync($"/api/users/{ritaId}/grants", new { grants = new[] { new { resource = "right:desktopLogin", level = "allow" } } }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, grants.StatusCode);
        }
        Assert.Empty(leases.Holders(AppA));
        socket.Dispose();
    }

    [Fact]
    public async Task ABackendWaitingForARequiredFolderIsUnavailableWithItsReason()
    {
        await InstallAsync(AppA, "");
        await _factory.ScalarAsync<long>("UPDATE apps SET manifest = json_set(manifest, '$.backend', json(@Backend), '$.storage', json(@Clips)) WHERE id = @AppA RETURNING 1",
            new { Backend, AppA, Clips = """[{ "id": "clips", "name": { "en": "Clips" }, "access": "readWrite", "required": true, "services": ["web"] }]""" });
        await Registry.ReloadAsync(Token);
        var contexts = _factory.Services.GetRequiredService<AppContexts>();
        var admin = JobTestSupport.Principal(_adminId, admin: true);

        var waiting = contexts.For(admin, contexts.SourceOf(AppA))!.Backend!;
        Assert.Equal((Pithyx.Daemon.Apps.Proxy.AppBackends.Unavailable, Pithyx.Daemon.Apps.Proxy.AppBackends.ReasonStorage), (waiting.Status, waiting.Reason));

        var root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "clips")).FullName;
        long share;
        using (var created = await _admin.PostAsJsonAsync("/api/shares", new { name = "Clips", path = root }, Token))
        {
            share = (await created.JsonAsync()).GetProperty("id").GetInt64();
        }
        await _factory.ScalarAsync<long>("INSERT INTO storage_mappings (app_id, storage_id, share_id, path, access) VALUES (@AppA, 'clips', @share, '', 'readWrite') RETURNING 1", new { AppA, share });
        await Registry.ReloadAsync(Token);
        var ready = contexts.For(admin, contexts.SourceOf(AppA))!.Backend!;
        Assert.Equal((Pithyx.Daemon.Apps.Proxy.AppBackends.Stopped, (string?)null), (ready.Status, ready.Reason));
    }

    private async Task<System.Net.WebSockets.WebSocket> ConnectAsync(string cookie)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "http://localhost";
        };
        return await client.ConnectAsync(new Uri("ws://localhost/ws"), Token);
    }

    private static Task SendAsync(System.Net.WebSockets.WebSocket socket, object message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, Token);

    private static async Task<JsonElement> ReceiveAsync(System.Net.WebSockets.WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    /// <summary>The next message that is not an event, so a reply to a subscription.</summary>
    private static async Task<JsonElement> ReceiveReplyAsync(System.Net.WebSockets.WebSocket socket)
    {
        while (true)
        {
            var message = await ReceiveAsync(socket);
            if (message.GetProperty("type").GetString() != "event")
            {
                return message;
            }
        }
    }

    /// <summary>The next event of the topic that matches, skipping events of the app's other changes.</summary>
    private static async Task<JsonElement> NextContextAsync(System.Net.WebSockets.WebSocket socket, string topic, Func<JsonElement, bool>? match = null)
    {
        while (true)
        {
            var message = await ReceiveAsync(socket);
            if (message.GetProperty("type").GetString() == "event" && message.GetProperty("topic").GetString() == topic && (match?.Invoke(message.GetProperty("data")) ?? true))
            {
                return message.GetProperty("data");
            }
        }
    }

    [Fact]
    public async Task FilesFollowTheUsersRightsTheAppsPermissionsAndTheMappedFolder()
    {
        var root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "media")).FullName;
        var secret = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "secret")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        Directory.CreateDirectory(Path.Combine(root, "private"));
        await File.WriteAllTextAsync(Path.Combine(root, "readme.txt"), "hello", Token);
        await File.WriteAllTextAsync(Path.Combine(root, "private", "diary.txt"), "dear diary", Token);
        File.CreateSymbolicLink(Path.Combine(root, "inbox", "escape"), "../private");
        long media, hidden;
        using (var created = await _admin.PostAsJsonAsync("/api/shares", new { name = "Media", path = root }, Token))
        {
            media = (await created.JsonAsync()).GetProperty("id").GetInt64();
        }
        using (var created = await _admin.PostAsJsonAsync("/api/shares", new { name = "Secret", path = secret }, Token))
        {
            hidden = (await created.JsonAsync()).GetProperty("id").GetInt64();
        }
        const string Inbox = ",\n  \"storage\": [{ \"id\": \"inbox\", \"name\": { \"en\": \"Inbox\" }, \"access\": \"readWrite\", \"required\": false }]";
        await InstallAsync(AppA, "\"files.shares\", \"files.read\"", extra: Inbox);
        await InstallAsync(AppB, "\"files.shares\", \"files.read\", \"files.write\"");
        var (rita, ritaId, _) = await UserAsync("rita", AppA, AppB);
        await _admin.SetShareAccessAsync(media, ritaId, "read");
        var reader = App(await TokenAsync(rita, AppA));
        var writer = App(await TokenAsync(rita, AppB));
        var adminWriter = App(await TokenAsync(_admin, AppB));
        var share = $"share={media}";

        var shares = await reader.GetFromJsonAsync<JsonElement>("/api/app/files/shares", Token);
        var listed = Assert.Single(shares.EnumerateArray());
        Assert.Equal((media.ToString(System.Globalization.CultureInfo.InvariantCulture), "read"), (listed.GetProperty("id").GetString(), listed.GetProperty("access").GetString()));
        Assert.False(listed.TryGetProperty("path", out _));
        Assert.Equal("hello", await reader.GetStringAsync($"/api/app/files/content?{share}&path=readme.txt", Token));
        Assert.False((await reader.GetFromJsonAsync<JsonElement>($"/api/app/files/entry?{share}&path=readme.txt", Token)).GetProperty("writable").GetBoolean());

        async Task<string?> WriteAsync(HttpClient app, string query) => await ErrorAsync(await app.PutAsync($"/api/app/files/content?{query}", new StringContent("new"), Token));
        Assert.Equal("app.permission.denied", await WriteAsync(reader, $"{share}&path=new.txt"));
        Assert.Equal("fs.share.read_only", await WriteAsync(writer, $"{share}&path=new.txt"));
        Assert.Null(await WriteAsync(adminWriter, $"{share}&path=new.txt"));
        Assert.Equal("fs.share.not_found", await ErrorAsync(await reader.GetAsync($"/api/app/files/list?share={hidden}", Token)));
        Assert.Equal("fs.path.invalid", await ErrorAsync(await reader.GetAsync($"/api/app/files/content?{share}&path=../secret/x", Token)));
        Assert.Equal("fs.operation.invalid", await ErrorAsync(await reader.GetAsync($"/api/app/files/list?{share}&storage=inbox", Token)));

        // Without files.shares a shared folder is closed; the mapped storage needs no files.* permission.
        await _admin.PutAsJsonAsync($"/api/apps/{AppA}/permissions", new { permissions = new[] { new { permission = "files.shares", granted = false } } }, Token);
        Assert.Equal("app.permission.denied", await ErrorAsync(await reader.GetAsync("/api/app/files/shares", Token)));
        Assert.Equal("app.storage.not_found", await ErrorAsync(await reader.GetAsync("/api/app/files/list?storage=inbox", Token)));
        await _factory.ScalarAsync<long>(
            "INSERT INTO storage_mappings (app_id, storage_id, share_id, path, access) VALUES (@AppA, 'inbox', @media, 'inbox', 'readWrite') RETURNING 1",
            new { AppA, media });
        await Registry.ReloadAsync(Token);
        Assert.Equal(["escape"], (await reader.GetFromJsonAsync<JsonElement>("/api/app/files/list?storage=inbox", Token)).GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()));
        // Neither a symlink nor the user's read access lead out of the mapped folder or past the user's rights.
        Assert.NotNull(await ErrorAsync(await reader.GetAsync("/api/app/files/content?storage=inbox&path=escape/diary.txt", Token)));
        Assert.Equal("fs.share.read_only", await WriteAsync(reader, "storage=inbox&path=note.txt"));
        await _admin.SetShareAccessAsync(media, ritaId, "readWrite");
        Assert.Null(await WriteAsync(reader, "storage=inbox&path=note.txt"));
        Assert.True(File.Exists(Path.Combine(root, "inbox", "note.txt")));
    }

    [Fact]
    public async Task AFileTheUserHandedToAWindowNeedsNoSharesPermissionAndNothingElseOpens()
    {
        var root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "handed")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "readme.txt"), "hello", Token);
        await File.WriteAllTextAsync(Path.Combine(root, "other.txt"), "other", Token);
        long media;
        using (var created = await _admin.PostAsJsonAsync("/api/shares", new { name = "Handed", path = root }, Token))
        {
            media = (await created.JsonAsync()).GetProperty("id").GetInt64();
        }
        await InstallAsync(AppA, "\"files.read\"");
        await InstallAsync(AppB, "\"files.read\", \"files.write\"");
        var (rita, ritaId, _) = await UserAsync("rita", AppA, AppB);
        await _admin.SetShareAccessAsync(media, ritaId, "readWrite");
        var reader = App(await TokenAsync(rita, AppA));
        var writer = App(await TokenAsync(rita, AppB));
        var otherWindow = App(await TokenAsync(rita, AppB, instance: "w2"));
        var share = $"share={media}";

        async Task<string?> HandAsync(HttpClient app, string path, bool write) =>
            await ErrorAsync(await app.PostAsJsonAsync("/api/app/files/handles", new { share = media.ToString(System.Globalization.CultureInfo.InvariantCulture), path, write }, Token));

        Assert.Equal("app.permission.denied", await ErrorAsync(await reader.GetAsync($"/api/app/files/content?{share}&path=readme.txt", Token)));
        Assert.Null(await HandAsync(reader, "readme.txt", write: false));
        Assert.Equal("hello", await reader.GetStringAsync($"/api/app/files/content?{share}&path=readme.txt", Token));
        // Exactly the handed path, not its neighbours, not a folder listing, and not for writing.
        Assert.Equal("app.permission.denied", await ErrorAsync(await reader.GetAsync($"/api/app/files/content?{share}&path=other.txt", Token)));
        Assert.Equal("app.permission.denied", await ErrorAsync(await reader.GetAsync($"/api/app/files/list?{share}", Token)));
        Assert.Equal("app.permission.denied", await HandAsync(reader, "readme.txt", write: true));
        Assert.Equal("app.permission.denied", await ErrorAsync(await reader.PutAsync($"/api/app/files/content?{share}&path=readme.txt&overwrite=true", new StringContent("x"), Token)));

        Assert.Null(await HandAsync(writer, "new.txt", write: true));
        Assert.Null(await ErrorAsync(await writer.PutAsync($"/api/app/files/content?{share}&path=new.txt", new StringContent("saved"), Token)));
        Assert.Equal("saved", await File.ReadAllTextAsync(Path.Combine(root, "new.txt"), Token));
        Assert.Equal("app.permission.denied", await ErrorAsync(await writer.PutAsync($"/api/app/files/content?{share}&path=other.txt&overwrite=true", new StringContent("x"), Token)));
        // A handle belongs to the window that got it.
        Assert.Equal("app.permission.denied", await ErrorAsync(await otherWindow.PutAsync($"/api/app/files/content?{share}&path=new.txt&overwrite=true", new StringContent("x"), Token)));
        // The user's own rights still decide.
        await _admin.SetShareAccessAsync(media, ritaId, "read");
        Assert.Equal("fs.share.read_only", await ErrorAsync(await writer.PutAsync($"/api/app/files/content?{share}&path=new.txt&overwrite=true", new StringContent("x"), Token)));

        // A file URL reads that one file without any credentials, and ends with the app's permission.
        using var created2 = await reader.PostAsJsonAsync("/api/app/files/url", new { share = media.ToString(System.Globalization.CultureInfo.InvariantCulture), path = "readme.txt" }, Token);
        var url = (await created2.JsonAsync()).GetProperty("url").GetString()!;
        Assert.StartsWith("/api/app-files/content?pithyx-token=", url, StringComparison.Ordinal);
        var anonymous = _factory.CreateClient(new() { HandleCookies = false });
        _clients.Add(anonymous);
        using (var response = await anonymous.GetAsync(url, Token))
        {
            Assert.Equal("hello", await response.Content.ReadAsStringAsync(Token));
            Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
        using (var download = await anonymous.GetAsync(url + "&download=true", Token))
        {
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        }
        using (var range = new HttpRequestMessage(HttpMethod.Get, url))
        {
            range.Headers.Range = new RangeHeaderValue(1, 2);
            using var partial = await anonymous.SendAsync(range, Token);
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal("el", await partial.Content.ReadAsStringAsync(Token));
        }
        using (var other = await reader.PostAsJsonAsync("/api/app/files/url", new { share = media.ToString(System.Globalization.CultureInfo.InvariantCulture), path = "other.txt" }, Token))
        {
            Assert.Equal("app.permission.denied", await ErrorAsync(other));
        }
        Assert.Equal("app.token.invalid", await ErrorAsync(await anonymous.GetAsync("/api/app-files/content?pithyx-token=nope", Token)));
        Assert.Equal("app.token.invalid", await ErrorAsync(await anonymous.GetAsync($"/api/app-files/content?pithyx-token={await TokenAsync(rita, AppA)}", Token)));
        await _admin.PutAsJsonAsync($"/api/apps/{AppA}/permissions", new { permissions = new[] { new { permission = "files.read", granted = false } } }, Token);
        Assert.Equal("app.permission.denied", await ErrorAsync(await anonymous.GetAsync(url, Token)));
    }

    [Fact]
    public async Task TheFileManagerCallsNeedTheSharesAndTheReadOrWritePermissionAndJobsBelongToTheirApp()
    {
        var root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "manage")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "a", Token);
        await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "b", Token);
        long media;
        using (var created = await _admin.PostAsJsonAsync("/api/shares", new { name = "Manage", path = root }, Token))
        {
            media = (await created.JsonAsync()).GetProperty("id").GetInt64();
        }
        await InstallAsync(AppA, "\"files.shares\"");
        await InstallAsync(AppB, "\"files.shares\", \"files.read\", \"files.write\"");
        var (rita, ritaId, _) = await UserAsync("rita", AppA, AppB);
        await _admin.SetShareAccessAsync(media, ritaId, "readWrite");
        var weak = App(await TokenAsync(rita, AppA));
        var strong = App(await TokenAsync(rita, AppB));
        var id = media.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var share = $"share={media}";

        Task<HttpResponseMessage> Rename(HttpClient app) => app.PostAsJsonAsync("/api/app/files/rename", new { share = id, path = "a.txt", name = "c.txt" }, Token);
        Task<HttpResponseMessage> Operation(HttpClient app, string path) => app.PostAsJsonAsync("/api/app/files/operations", new { kind = "delete", items = new[] { new { share = id, path } } }, Token);

        Assert.Equal("app.permission.denied", await ErrorAsync(await Rename(weak)));
        Assert.Equal("app.permission.denied", await ErrorAsync(await Operation(weak, "b.txt")));
        Assert.Equal("app.permission.denied", await ErrorAsync(await weak.GetAsync($"/api/app/files/trash?{share}", Token)));
        Assert.Equal("app.permission.denied", await ErrorAsync(await weak.GetAsync($"/api/app/files/versions?{share}&path=a.txt", Token)));
        Assert.Equal("app.permission.denied", await ErrorAsync(await weak.PostAsJsonAsync("/api/app/files/trash/empty", new { share = id }, Token)));

        using (var renamed = await Rename(strong))
        {
            Assert.Equal("c.txt", (await renamed.JsonAsync()).GetProperty("name").GetString());
        }
        using var started = await Operation(strong, "b.txt");
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var startedJob = await started.JsonAsync();
        var job = startedJob.GetProperty("id").GetInt64();
        await FinishedAsync(startedJob);
        Assert.Equal(["b.txt"], (await strong.GetFromJsonAsync<JsonElement>($"/api/app/files/trash?{share}", Token)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Array, (await strong.GetFromJsonAsync<JsonElement>($"/api/app/files/versions?{share}&path=c.txt", Token)).GetProperty("versions").ValueKind);

        // A job belongs to the app that started it.
        Assert.Equal("jobs.job.not_found", await ErrorAsync(await weak.PostAsync($"/api/app/jobs/{job}/cancel", null, Token)));
        Assert.Equal("jobs.job.not_found", await ErrorAsync(await strong.PostAsync("/api/app/jobs/9999/cancel", null, Token)));
        using var cancelled = await strong.PostAsync($"/api/app/jobs/{job}/cancel", null, Token);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
    }

    [Fact]
    public async Task FileOperationsTakeAStorageRequestWithinItsMappedFolder()
    {
        var root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "clips")).FullName;
        var inbox = Directory.CreateDirectory(Path.Combine(root, "inbox")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "private"));
        await File.WriteAllTextAsync(Path.Combine(inbox, "clip.md"), "clip", Token);
        await File.WriteAllTextAsync(Path.Combine(inbox, "old.md"), "old", Token);
        await File.WriteAllTextAsync(Path.Combine(root, "private", "diary.txt"), "dear diary", Token);
        File.CreateSymbolicLink(Path.Combine(inbox, "escape"), "../private");
        long share;
        using (var created = await _admin.PostAsJsonAsync("/api/shares", new { name = "Clips", path = root }, Token))
        {
            share = (await created.JsonAsync()).GetProperty("id").GetInt64();
        }
        const string Inbox = ",\n  \"storage\": [{ \"id\": \"inbox\", \"name\": { \"en\": \"Inbox\" }, \"access\": \"readWrite\", \"required\": false }]";
        await InstallAsync(AppA, "", extra: Inbox);
        await InstallAsync(AppB, "\"files.shares\", \"files.write\"", extra: Inbox);
        foreach (var app in new[] { AppA, AppB })
        {
            using var mapped = await _admin.PutAsJsonAsync($"/api/apps/{app}/storage/inbox", new { shareId = share, path = "inbox", access = "readWrite" }, Token);
            Assert.Equal(HttpStatusCode.OK, mapped.StatusCode);
        }
        var (rita, ritaId, _) = await UserAsync("rita", AppA, AppB);
        await _admin.SetShareAccessAsync(share, ritaId, "readWrite");
        var storageOnly = App(await TokenAsync(rita, AppA));
        var withShares = App(await TokenAsync(rita, AppB));
        var id = share.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Task<HttpResponseMessage> Run(HttpClient app, object body) => app.PostAsJsonAsync("/api/app/files/operations", body, Token);

        // Out of the storage request into the shared folder: the shared folder needs files.shares and files.write.
        object copyOut = new { kind = "copy", items = new[] { new { storage = "inbox", path = "clip.md" } }, target = new { share = id, path = "" } };
        Assert.Equal("app.permission.denied", await ErrorAsync(await Run(storageOnly, copyOut)));
        using (var copied = await Run(withShares, copyOut))
        {
            Assert.Equal(HttpStatusCode.Accepted, copied.StatusCode);
            await FinishedAsync(await copied.JsonAsync());
        }
        Assert.Equal("clip", await File.ReadAllTextAsync(Path.Combine(root, "clip.md"), Token));

        // Within the storage request no files.* permission is needed; a delete goes to the shared folder's bin.
        using (var deleted = await Run(storageOnly, new { kind = "delete", items = new[] { new { storage = "inbox", path = "old.md" } } }))
        {
            Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
            await FinishedAsync(await deleted.JsonAsync());
        }
        Assert.False(File.Exists(Path.Combine(inbox, "old.md")));

        // Neither .. nor a symbolic link leaves the mapped folder, and the folder itself is the request's root.
        Assert.Equal("fs.path.invalid", await ErrorAsync(await Run(withShares, new { kind = "copy", items = new[] { new { storage = "inbox", path = "../private/diary.txt" } }, target = new { share = id, path = "" } })));
        using (var escaped = await Run(withShares, new { kind = "copy", items = new[] { new { storage = "inbox", path = "escape/diary.txt" } }, target = new { share = id, path = "" } }))
        {
            Assert.NotEqual(HttpStatusCode.Accepted, escaped.StatusCode);
        }
        Assert.False(File.Exists(Path.Combine(root, "diary.txt")));
        Assert.Equal("app.storage.not_found", await ErrorAsync(await Run(withShares, new { kind = "delete", items = new[] { new { storage = "other", path = "x" } } })));

        // A read-only mapping refuses every change in it.
        using (var readOnly = await _admin.PutAsJsonAsync($"/api/apps/{AppA}/storage/inbox", new { shareId = share, path = "inbox", access = "read" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, readOnly.StatusCode);
        }
        Assert.Equal("fs.share.read_only", await ErrorAsync(await Run(storageOnly, new { kind = "delete", items = new[] { new { storage = "inbox", path = "clip.md" } } })));
    }

    [Fact]
    public async Task ConcurrentWritesKeepOneNotificationPerTagAndTheStorageLimit()
    {
        await InstallAsync(AppA, "\"notifications\"");
        var app = App(await TokenAsync(_admin, AppA));

        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            using var response = await app.PostAsJsonAsync("/api/app/notifications", new { title = $"Message {index}", tag = "chat" }, Token);
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");
        }));
        Assert.Equal(1, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE app = @AppA", new { AppA }));
        var items = (await _admin.GetFromJsonAsync<JsonElement>("/api/notifications", Token)).GetProperty("items").EnumerateArray();
        Assert.Single(items, item => item.GetProperty("app").ValueKind == JsonValueKind.String);

        var value = new string('x', 63 * 1024);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
        {
            using var response = await app.PutAsJsonAsync($"/api/app/storage/value?key=k{index}", new { value }, Token);
            Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.RequestEntityTooLarge, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");
        }));
        Assert.InRange(
            await _factory.ScalarAsync<long>("SELECT sum(length(CAST(key AS BLOB)) + length(CAST(value AS BLOB))) FROM app_storage WHERE app_id = @AppA", new { AppA }),
            15 * 63 * 1024,
            AppValueStore.MaxTotalBytes);
    }

    [Fact]
    public async Task TokensNeverReachTheLogs()
    {
        await InstallAsync(AppA, "\"notifications\"");
        await AddBackendAsync(AppA);
        var token = await TokenAsync(_admin, AppA);
        var app = App(token);
        using (var ticketResponse = await app.PostAsJsonAsync("/api/app/tickets", new { endpoint = "api" }, Token))
        {
            var ticket = (await ticketResponse.JsonAsync()).GetProperty("ticket").GetString()!;
            using var withQuery = await app.GetAsync($"/api/app/storage?pithyx-ticket={ticket}&pithyx-token={token}", Token);
            // As the proxy's request log will carry it (A13).
            _factory.Services.GetRequiredService<Serilog.ILogger>().Information("Proxied {Url} for {Header}", $"/apps/{AppA}/api/live?pithyx-ticket={ticket}", $"Bearer {token}");
            Assert.DoesNotContain(ticket, await DaemonLogAsync(), StringComparison.Ordinal);
        }
        _factory.Time.Advance(AppTokens.TokenLifetime);
        await AssertRefusedAsync(app);
        Assert.DoesNotContain(token, await DaemonLogAsync(), StringComparison.Ordinal);
    }

    private async Task<string> DaemonLogAsync()
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_factory.DataDirectory, "logs"), "daemon*.json"))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            builder.Append(await reader.ReadToEndAsync(Token));
        }
        return builder.ToString();
    }
}
