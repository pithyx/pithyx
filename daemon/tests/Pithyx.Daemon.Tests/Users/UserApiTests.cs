using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Users;

public sealed class UserApiTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public UserApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<JsonElement> CreateAsync(string username, bool isAdmin = false)
    {
        using var response = await _admin.PostAsync("/api/users", new { username, displayName = (string?)null, password = "river-candle-orbit-7", isAdmin });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    [Fact]
    public async Task UsersGetUidsFromTheRangeThatAreNeverGivenOutAgain()
    {
        await _admin.SetupAdminAsync();

        var rita = await CreateAsync("rita");
        Assert.Equal(20001, rita.GetProperty("uid").GetInt64());
        using (var deleted = await _admin.DeleteAsync($"/api/users/{rita.GetProperty("id").GetInt64()}", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        var again = await CreateAsync("rita");

        Assert.Equal(20002, again.GetProperty("uid").GetInt64());
        Assert.Equal(20002, again.GetProperty("id").GetInt64());
        var list = await _admin.GetFromJsonAsync<JsonElement>("/api/users", Token);
        Assert.Equal([("admin", 20000L), ("rita", 20002L)], list.EnumerateArray().Select(user => (user.GetProperty("username").GetString()!, user.GetProperty("uid").GetInt64())));
    }

    [Fact]
    public async Task DeletingAUserCancelsItsJobsAndForgetsItsNotifications()
    {
        await _admin.SetupAdminAsync();
        var ritaId = (await CreateAsync("rita")).GetProperty("id").GetInt64();
        var notifications = _factory.Services.GetRequiredService<NotificationService>();
        var store = _factory.Services.GetRequiredService<NotificationStore>();
        await notifications.CreateAsync(ritaId, new NotificationDraft("test.hello", new Dictionary<string, object?>(), NotificationTone.Info), Token);
        var cancelled = new TaskCompletionSource();
        var job = await _factory.Services.GetRequiredService<JobService>().StartAsync(
            ritaId,
            "test.work",
            null,
            async context =>
            {
                using var registration = context.Cancellation.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, context.Cancellation);
            },
            Token);
        Assert.Single(store.Of(ritaId));

        using (var deleted = await _admin.DeleteAsync($"/api/users/{ritaId}", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Empty(store.Of(ritaId));
        using var lookup = await _admin.GetAsync($"/api/jobs/{job.Id}", Token);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Fact]
    public async Task TheLastAdministratorStaysAndNobodyDeletesThemselves()
    {
        await _admin.SetupAdminAsync();
        var adminId = (await _admin.GetFromJsonAsync<JsonElement>("/api/users", Token))[0].GetProperty("id").GetInt64();

        using var demote = await _admin.PatchAsJsonAsync($"/api/users/{adminId}", new { isAdmin = false }, Token);
        Assert.Equal("users.user.last_admin", await demote.ErrorCodeAsync());
        using var self = await _admin.DeleteAsync($"/api/users/{adminId}", Token);
        Assert.Equal("users.user.self", await self.ErrorCodeAsync());

        var second = await CreateAsync("bea", isAdmin: true);
        using var demoted = await _admin.PatchAsJsonAsync($"/api/users/{adminId}", new { isAdmin = false }, Token);
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        Assert.False((await demoted.JsonAsync()).GetProperty("isAdmin").GetBoolean());
        _ = second;
    }

    [Fact]
    public async Task ADeactivatedUserCannotSignInUntilActivatedAgain()
    {
        await _admin.SetupAdminAsync();
        var ritaId = (await CreateAsync("rita")).GetProperty("id").GetInt64();
        using var browser = _factory.CreateApiClient();
        using (var signedIn = await browser.LoginAsync("rita", "river-candle-orbit-7"))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        using (var off = await _admin.PatchAsJsonAsync($"/api/users/{ritaId}", new { disabled = true }, Token))
        {
            Assert.True((await off.JsonAsync()).GetProperty("disabled").GetBoolean());
        }

        // The session ended, and a wrong password still says only that.
        using (var session = await browser.GetAsync("/api/auth/session", Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        }
        using (var wrong = await browser.LoginAsync("rita", "wrong-candle-orbit-7"))
        {
            Assert.Equal("auth.credentials.invalid", await wrong.ErrorCodeAsync());
        }
        using (var refused = await browser.LoginAsync("rita", "river-candle-orbit-7"))
        {
            Assert.Equal("auth.account.disabled", await refused.ErrorCodeAsync());
        }

        using (var on = await _admin.PatchAsJsonAsync($"/api/users/{ritaId}", new { disabled = false }, Token))
        {
            Assert.False((await on.JsonAsync()).GetProperty("disabled").GetBoolean());
        }
        using var again = await browser.LoginAsync("rita", "river-candle-orbit-7");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task NobodyDeactivatesThemselvesOrTheLastActiveAdministrator()
    {
        await _admin.SetupAdminAsync();
        var adminId = (await _admin.GetFromJsonAsync<JsonElement>("/api/users", Token))[0].GetProperty("id").GetInt64();
        using (var self = await _admin.PatchAsJsonAsync($"/api/users/{adminId}", new { disabled = true }, Token))
        {
            Assert.Equal("users.user.self", await self.ErrorCodeAsync());
        }

        // A deactivated administrator does not count: the active one stays the last.
        var beaId = (await CreateAsync("bea", isAdmin: true)).GetProperty("id").GetInt64();
        using (var off = await _admin.PatchAsJsonAsync($"/api/users/{beaId}", new { disabled = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        }
        using var demote = await _admin.PatchAsJsonAsync($"/api/users/{adminId}", new { isAdmin = false }, Token);
        Assert.Equal("users.user.last_admin", await demote.ErrorCodeAsync());
    }

    [Fact]
    public void ShareAclsGiveAdministratorsAndGrantedUsersTheirAccessAndNobodyElse()
    {
        Assert.Equal("u::rwX,g::---,o::---,m::rwX", ShareAcls.Entries([]));
        Assert.Equal(
            "u::rwX,g::---,o::---,m::rwX,u:20000:rwX,u:20001:r-X,u:20002:rwX",
            ShareAcls.Entries([(20002, false), (20000, true), (20001, false), (20002, true)]));
        var commands = ShareAcls.Commands("/tank/media", "u::rwX");
        Assert.Equal(["-R", "-P", "--set", "u::rwX", "--", "/tank/media"], commands[0].Arguments);
        Assert.Equal(["-R", "-P", "-d", "--set", "u::rwX", "--", "/tank/media"], commands[1].Arguments);
    }

    [Fact]
    public async Task NamesOfSystemAccountsAndTakenNamesAreRefused()
    {
        await _admin.SetupAdminAsync();

        // The simulation's passwd has the machine owner's account outside the Pithyx range.
        using var foreign = await _admin.PostAsync("/api/users", new { username = "owner", password = "river-candle-orbit-7" });
        Assert.Equal("users.account.refused", await foreign.ErrorCodeAsync());
        using var reserved = await _admin.PostAsync("/api/users", new { username = "root", password = "river-candle-orbit-7" });
        Assert.Equal("auth.username.reserved", await reserved.ErrorCodeAsync());
        using var taken = await _admin.PostAsync("/api/users", new { username = "Admin", password = "river-candle-orbit-7" });
        Assert.Equal("users.user.exists", await taken.ErrorCodeAsync());
        using var weak = await _admin.PostAsync("/api/users", new { username = "tom", password = "password1" });
        Assert.Equal("auth.password.too_common", await weak.ErrorCodeAsync());
    }

    [Fact]
    public async Task APasswordResetEndsTheUsersSessionsAndOnlyAdministratorsManageUsers()
    {
        await _admin.SetupAdminAsync();
        var rita = await CreateAsync("rita");
        var ritaId = rita.GetProperty("id").GetInt64();
        using var ritaClient = _factory.ClientWithCookie(await _factory.SessionCookieAsync(ritaId));
        using (var forbidden = await ritaClient.GetAsync("/api/users", Token))
        {
            Assert.Equal("auth.permission.denied", await forbidden.ErrorCodeAsync());
        }

        using var reset = await _admin.PutAsJsonAsync($"/api/users/{ritaId}/password", new { password = "a new passphrase here" }, Token);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM sessions WHERE user_id = @ritaId", new { ritaId }));
        using var login = await _factory.CreateApiClient().LoginAsync("rita", "a new passphrase here");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
