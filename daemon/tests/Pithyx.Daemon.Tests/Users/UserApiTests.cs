using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

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
        var list = await _admin.GetFromJsonAsync<JsonElement>("/api/users", Token);
        Assert.Equal([("admin", 20000L), ("rita", 20002L)], list.EnumerateArray().Select(user => (user.GetProperty("username").GetString()!, user.GetProperty("uid").GetInt64())));
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
