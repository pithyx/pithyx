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

/// <summary>
/// Homes in simulation (D348): every user's own home as <c>home</c>, the others' homes for administrators only, the
/// choice for a deleted user's home, the quotas and moving every home to another pool.
/// </summary>
public sealed class HomeApiTests : IDisposable
{
    private const string Password = "river-candle-orbit-7";

    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public HomeApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<JsonElement[]> SharesAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/shares", Token)).EnumerateArray()];

    private static JsonElement Kind(JsonElement[] shares, string kind, string? name = null) =>
        shares.Single(share => share.GetProperty("kind").GetString() == kind && (name is null || share.GetProperty("name").GetString() == name));

    private async Task<long> CreateUserAsync(string username)
    {
        using var created = await _admin.PostAsync("/api/users", new { username, password = Password });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.JsonAsync()).GetProperty("id").GetInt64();
    }

    private async Task<HttpClient> SignInAsync(string username)
    {
        var client = _factory.CreateApiClient();
        (await client.LoginAsync(username, Password)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>The administrator from the setup gets its home in the background once the pools are read.</summary>
    private async Task<JsonElement> SetupAsync()
    {
        await _admin.SetupAdminAsync();
        JsonElement[] shares = [];
        await JobTestSupport.WaitUntilAsync(async () => (shares = await SharesAsync(_admin)).Any(share => share.GetProperty("kind").GetString() == "home"), "the administrator's home");
        return Kind(shares, "home");
    }

    private async Task<JsonElement> WaitForJobAsync(long id)
    {
        JsonElement job = default;
        await JobTestSupport.WaitUntilAsync(async () =>
        {
            using var response = await _admin.GetAsync($"/api/jobs/{id}", Token);
            job = await response.JsonAsync();
            return job.GetProperty("state").GetString() is not ("queued" or "running");
        }, $"job {id}");
        return job;
    }

    [Fact]
    public async Task EveryUserSeesTheOwnHomeAndOnlyAdministratorsSeeTheOthers()
    {
        var adminHome = await SetupAsync();
        Assert.Equal("home", adminHome.GetProperty("name").GetString());
        await CreateUserAsync("rita");
        using var rita = await SignInAsync("rita");

        var ritaShares = await SharesAsync(rita);
        var ritaHome = Kind(ritaShares, "home");
        Assert.Equal("home", ritaHome.GetProperty("name").GetString());
        Assert.Equal("readWrite", ritaHome.GetProperty("access").GetString());
        Assert.DoesNotContain(ritaShares, share => share.GetProperty("kind").GetString() == "userHome");
        // The first entry is the own home, so Files opens it first.
        Assert.Equal("home", ritaShares[0].GetProperty("kind").GetString());

        var adminShares = await SharesAsync(_admin);
        var seen = Kind(adminShares, "userHome", "rita");
        Assert.Equal(ritaHome.GetProperty("id").GetInt64(), seen.GetProperty("id").GetInt64());

        // rita writes into her home; the administrator reads it there, nobody else reaches the administrator's.
        var ritaId = ritaHome.GetProperty("id").GetInt64();
        using (var upload = await rita.PutAsync($"/api/files/{ritaId}/content?path=notes.txt", new StringContent("mine"), Token))
        {
            Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync(Token));
        }
        using (var list = await _admin.GetAsync($"/api/files/{ritaId}/list", Token))
        {
            Assert.Contains((await list.JsonAsync()).GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("name").GetString() == "notes.txt");
        }
        using (var sneak = await rita.GetAsync($"/api/files/{adminHome.GetProperty("id").GetInt64()}/list", Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, sneak.StatusCode);
        }

        // A home is never a shared folder: no details, no grants, no rename.
        Assert.DoesNotContain((await _admin.GetFromJsonAsync<JsonElement>("/api/shares/details", Token)).EnumerateArray(), share => share.GetProperty("id").GetInt64() == ritaId);
        using (var grant = await _admin.PutAsJsonAsync($"/api/shares/{adminHome.GetProperty("id").GetInt64()}/grants", new { grants = Array.Empty<object>() }, Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, grant.StatusCode);
        }
        using var rename = await _admin.PatchAsJsonAsync($"/api/shares/{ritaId}", new { name = "taken" }, Token);
        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
    }

    [Fact]
    public async Task ADeletedUsersHomeStaysIsDeletedOrMovesIntoASharedFolder()
    {
        await SetupAsync();
        var shared = Kind(await SharesAsync(_admin), "share");
        var ritaId = await CreateUserAsync("rita");
        var ritaHome = Kind(await SharesAsync(_admin), "userHome", "rita");
        await File.WriteAllTextAsync(Path.Combine(ritaHome.GetProperty("path").GetString()!, "notes.txt"), "kept", Token);

        // Deleting needs a complete choice.
        using (var incomplete = await _admin.DeleteAsync($"/api/users/{ritaId}?home=delete", Token))
        {
            Assert.Equal("homes.choice.invalid", await incomplete.ErrorCodeAsync());
        }
        using (var wrong = await _admin.DeleteAsync($"/api/users/{ritaId}?home=delete&confirm=tom", Token))
        {
            Assert.Equal("homes.home.name_mismatch", await wrong.ErrorCodeAsync());
        }

        // Kept: renamed for administrators, and a new rita starts with an empty home.
        using (var deleted = await _admin.DeleteAsync($"/api/users/{ritaId}", Token))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }
        var kept = Kind(await SharesAsync(_admin), "userHome", "rita.deleted-20001");
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(kept.GetProperty("path").GetString()!, "notes.txt"), Token));
        var again = await CreateUserAsync("rita");
        var fresh = Kind(await SharesAsync(_admin), "userHome", "rita");
        Assert.Empty(Directory.EnumerateFileSystemEntries(fresh.GetProperty("path").GetString()!));
        var overview = await _admin.GetFromJsonAsync<JsonElement>("/api/homes", Token);
        Assert.Contains(overview.GetProperty("homes").EnumerateArray(), home => home.GetProperty("folder").GetString() == "rita.deleted-20001" && home.GetProperty("userId").ValueKind == JsonValueKind.Null);

        // A kept home goes only after its folder name was typed, and never while its user is there.
        using (var inUse = await _admin.DeleteAsync($"/api/homes/{fresh.GetProperty("id").GetInt64()}?confirm=rita", Token))
        {
            Assert.Equal("homes.home.in_use", await inUse.ErrorCodeAsync());
        }
        using (var removed = await _admin.DeleteAsync($"/api/homes/{kept.GetProperty("id").GetInt64()}?confirm=rita.deleted-20001", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }
        Assert.False(Directory.Exists(kept.GetProperty("path").GetString()));

        // Moved: the files land in a folder named after the user in the shared folder, and the home is gone.
        await File.WriteAllTextAsync(Path.Combine(fresh.GetProperty("path").GetString()!, "report.txt"), "moved", Token);
        using (var moved = await _admin.DeleteAsync($"/api/users/{again}?home=move&shareId={shared.GetProperty("id").GetInt64()}", Token))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            var job = await WaitForJobAsync((await moved.JsonAsync()).GetProperty("job").GetProperty("id").GetInt64());
            Assert.Equal("succeeded", job.GetProperty("state").GetString());
        }
        Assert.Equal("moved", await File.ReadAllTextAsync(Path.Combine(shared.GetProperty("path").GetString()!, "rita", "report.txt"), Token));
        Assert.DoesNotContain(await SharesAsync(_admin), share => share.GetProperty("kind").GetString() == "userHome");

        // Deleted with every file.
        var tomId = await CreateUserAsync("tom");
        var tomHome = Kind(await SharesAsync(_admin), "userHome", "tom").GetProperty("path").GetString()!;
        using (var gone = await _admin.DeleteAsync($"/api/users/{tomId}?home=delete&confirm=tom", Token))
        {
            Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
        }
        Assert.False(Directory.Exists(tomHome));
    }

    [Fact]
    public async Task QuotasAreCheckedAndTheHomesMoveToAnotherPool()
    {
        var adminHome = await SetupAsync();
        await CreateUserAsync("rita");
        var ritaHome = Kind(await SharesAsync(_admin), "userHome", "rita");
        await File.WriteAllTextAsync(Path.Combine(ritaHome.GetProperty("path").GetString()!, "notes.txt"), "travels", Token);

        using (var small = await _admin.PutAsJsonAsync("/api/homes/settings", new { defaultQuotaBytes = 1000 }, Token))
        {
            Assert.Equal("homes.quota.invalid", await small.ErrorCodeAsync());
        }
        using (var set = await _admin.PutAsJsonAsync("/api/homes/settings", new { defaultQuotaBytes = 10L << 30 }, Token))
        {
            Assert.Equal(10L << 30, (await set.JsonAsync()).GetProperty("defaultQuotaBytes").GetInt64());
        }
        var rita = (await _admin.GetFromJsonAsync<JsonElement>("/api/homes", Token)).GetProperty("homes").EnumerateArray().Single(home => home.GetProperty("folder").GetString() == "rita");
        using (var own = await _admin.PutAsJsonAsync($"/api/homes/users/{rita.GetProperty("userId").GetInt64()}/quota", new { quotaBytes = 0 }, Token))
        {
            var home = (await own.JsonAsync()).GetProperty("homes").EnumerateArray().Single(entry => entry.GetProperty("folder").GetString() == "rita");
            Assert.Equal(0, home.GetProperty("ownQuotaBytes").GetInt64());
        }

        var overview = await _admin.GetFromJsonAsync<JsonElement>("/api/homes", Token);
        var from = overview.GetProperty("pool").GetString()!;
        var to = overview.GetProperty("pools").EnumerateArray().Select(pool => pool.GetProperty("name").GetString()!).First(name => name != from);
        using (var same = await _admin.PostAsync("/api/homes/move", new { pool = from }))
        {
            Assert.Equal("homes.pool.unchanged", await same.ErrorCodeAsync());
        }
        using (var move = await _admin.PostAsync("/api/homes/move", new { pool = to }))
        {
            Assert.Equal(HttpStatusCode.Accepted, move.StatusCode);
            Assert.Equal("succeeded", (await WaitForJobAsync((await move.JsonAsync()).GetProperty("id").GetInt64())).GetProperty("state").GetString());
        }
        Assert.Equal(to, (await _admin.GetFromJsonAsync<JsonElement>("/api/homes", Token)).GetProperty("pool").GetString());
        var moved = Kind(await SharesAsync(_admin), "userHome", "rita");
        Assert.Equal(ritaHome.GetProperty("id").GetInt64(), moved.GetProperty("id").GetInt64());
        Assert.Contains(Path.Combine(to, "homes", "rita"), moved.GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal("travels", await File.ReadAllTextAsync(Path.Combine(moved.GetProperty("path").GetString()!, "notes.txt"), Token));
        Assert.False(Directory.Exists(ritaHome.GetProperty("path").GetString()));
        Assert.Contains(Path.Combine(to, "homes", "admin"), Kind(await SharesAsync(_admin), "home").GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal(adminHome.GetProperty("id").GetInt64(), Kind(await SharesAsync(_admin), "home").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task AHomeOnAPoolThatIsGoneStaysBehindAMoveAndCanBeDeletedForANewOne()
    {
        await SetupAsync();
        await CreateUserAsync("rita");
        var ritaHome = Kind(await SharesAsync(_admin), "userHome", "rita");
        var ritaShare = ritaHome.GetProperty("id").GetInt64();
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            // As if its pool had been destroyed: the row points at a dataset of a pool that is not there.
            await connection.ExecuteAsync("UPDATE shares SET dataset = 'ghost/pithyx/homes/rita' WHERE id = @ritaShare", new { ritaShare });
        }
        var overview = await _admin.GetFromJsonAsync<JsonElement>("/api/homes", Token);
        Assert.Equal("pool_missing", overview.GetProperty("homes").EnumerateArray().Single(home => home.GetProperty("folder").GetString() == "rita").GetProperty("unavailable").GetString());

        var to = overview.GetProperty("pools").EnumerateArray().Select(pool => pool.GetProperty("name").GetString()!).First(name => name != overview.GetProperty("pool").GetString());
        using (var move = await _admin.PostAsync("/api/homes/move", new { pool = to }))
        {
            Assert.Equal("succeeded", (await WaitForJobAsync((await move.JsonAsync()).GetProperty("id").GetInt64())).GetProperty("state").GetString());
        }
        Assert.Equal(to, (await _admin.GetFromJsonAsync<JsonElement>("/api/homes", Token)).GetProperty("pool").GetString());
        Assert.Equal(ritaHome.GetProperty("path").GetString(), Kind(await SharesAsync(_admin), "userHome", "rita").GetProperty("path").GetString());

        using (var removed = await _admin.DeleteAsync($"/api/homes/{ritaShare}?confirm=rita", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }
        var fresh = Kind(await SharesAsync(_admin), "userHome", "rita");
        Assert.NotEqual(ritaHome.GetProperty("path").GetString(), fresh.GetProperty("path").GetString());
        Assert.Contains(Path.Combine(to, "homes", "rita"), fresh.GetProperty("path").GetString(), StringComparison.Ordinal);
    }
}
