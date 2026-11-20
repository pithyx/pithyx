using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Files.Search;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Files.Search;

/// <summary>
/// The name index in simulation (D413): no fanotify here, so a test brings the index up to date with a walk, the way
/// the box does without fanotify every 15 minutes.
/// </summary>
public sealed class FileSearchApiTests : IAsyncLifetime
{
    private const string Password = "river-candle-orbit-7";

    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly string _root;
    private long _share;

    public FileSearchApiTests()
    {
        _admin = _factory.CreateApiClient();
        _root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "media")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "Docs", "Old"));
        Directory.CreateDirectory(Path.Combine(_root, "Plans 2026"));
        File.WriteAllText(Path.Combine(_root, "plan.md"), "plan");
        File.WriteAllText(Path.Combine(_root, "explanation.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "Docs", "planning.txt"), "planning");
        File.WriteAllText(Path.Combine(_root, "Docs", "Old", "my plan notes.txt"), "notes");
        File.WriteAllText(Path.Combine(_root, FsPath.UploadPrefix + "plan"), "half an upload");
        Directory.CreateDirectory(Path.Combine(_root, FsPath.TrashFolder, "1"));
        File.WriteAllText(Path.Combine(_root, FsPath.TrashFolder, "1", "plan-deleted.md"), "gone");
    }

    private SearchIndex Index => _factory.Services.GetRequiredService<SearchIndex>();

    public async ValueTask InitializeAsync()
    {
        await _admin.SetupAdminAsync();
        using var created = await _admin.PostAsync("/api/shares", new { name = "Media", path = _root });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _share = (await created.JsonAsync()).GetProperty("id").GetInt64();
        await Index.IdleAsync();
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task CatchUpAsync()
    {
        await Index.RefreshAsync();
        await Index.IdleAsync();
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string query, int? limit = null)
    {
        using var response = await client.GetAsync($"/api/files/search?q={Uri.EscapeDataString(query)}{(limit is null ? "" : $"&limit={limit}")}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<string[]> PathsAsync(HttpClient client, string query) =>
        [.. (await SearchAsync(client, query)).GetProperty("hits").EnumerateArray().Select(hit => hit.GetProperty("path").GetString()!)];

    private async Task<HttpClient> UserAsync(string username)
    {
        using (var created = await _admin.PostAsync("/api/users", new { username, password = Password }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var client = _factory.CreateApiClient();
        (await client.LoginAsync(username, Password)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonElement[]> SharesAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/shares", Token)).EnumerateArray()];

    [Fact]
    public async Task FindsNamesAnywhereInASharedFolderWithTheBestMatchesFirst()
    {
        Assert.Equal(["plan.md", "Plans 2026", "Docs/planning.txt", "Docs/Old/my plan notes.txt", "explanation.pdf"], await PathsAsync(_admin, "plan"));

        var result = await SearchAsync(_admin, "PLAN");
        Assert.True(result.GetProperty("complete").GetBoolean());
        Assert.False(result.GetProperty("more").GetBoolean());
        var first = result.GetProperty("hits")[0];
        Assert.Equal((_share, "plan.md", "file", 4L), (first.GetProperty("share").GetInt64(), first.GetProperty("name").GetString(), first.GetProperty("kind").GetString(), first.GetProperty("size").GetInt64()));
        Assert.Equal("folder", result.GetProperty("hits")[1].GetProperty("kind").GetString());

        // Every word has to match, short ones too; the order of the words does not matter.
        Assert.Equal(["Docs/Old/my plan notes.txt"], await PathsAsync(_admin, "notes plan"));
        // Short words alone match the start of a name; next to a longer word they match anywhere.
        Assert.Equal(["plan.md", "Plans 2026", "Docs/planning.txt"], await PathsAsync(_admin, "pl"));
        Assert.Equal(["Docs/Old/my plan notes.txt"], await PathsAsync(_admin, "my p"));
        Assert.Equal(["explanation.pdf"], await PathsAsync(_admin, "f pla"));
        Assert.Empty(await PathsAsync(_admin, "   "));
        Assert.Empty(await PathsAsync(_admin, "100%"));

        var limited = await SearchAsync(_admin, "plan", limit: 2);
        Assert.Equal(2, limited.GetProperty("hits").GetArrayLength());
        Assert.True(limited.GetProperty("more").GetBoolean());
    }

    [Fact]
    public async Task HitsAreCheckedOnDiskAndAWalkCatchesUpWithChanges()
    {
        File.Delete(Path.Combine(_root, "plan.md"));
        Directory.Move(Path.Combine(_root, "Docs"), Path.Combine(_root, "Papers"));
        // Before the index knows: nothing that is gone is shown.
        Assert.Equal(["Plans 2026", "explanation.pdf"], await PathsAsync(_admin, "plan"));

        File.WriteAllText(Path.Combine(_root, "Plans 2026", "budget plan.ods"), "new");
        await CatchUpAsync();

        Assert.Equal(["Plans 2026", "Papers/planning.txt", "Plans 2026/budget plan.ods", "Papers/Old/my plan notes.txt", "explanation.pdf"], await PathsAsync(_admin, "plan"));
    }

    [Fact]
    public async Task OnlySharedFoldersTheUserCanReadAndTheirOwnHomeAreSearched()
    {
        using var rita = await UserAsync("rita");
        Assert.Empty(await PathsAsync(rita, "plan"));

        var ritaId = (await SharesAsync(rita)).Single(share => share.GetProperty("kind").GetString() == "home").GetProperty("id").GetInt64();
        using (var upload = await rita.PutAsync($"/api/files/{ritaId}/content?path=rita-plan.txt", new StringContent("mine"), Token))
        {
            Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync(Token));
        }
        JsonElement[] adminShares = [];
        await JobTestSupport.WaitUntilAsync(async () => (adminShares = await SharesAsync(_admin)).Any(share => share.GetProperty("kind").GetString() == "home"), "the administrator's home");
        var adminHome = adminShares.Single(share => share.GetProperty("kind").GetString() == "home");
        File.WriteAllText(Path.Combine(adminHome.GetProperty("path").GetString()!, "admin-plan.txt"), "mine");
        await CatchUpAsync();

        Assert.Equal(["rita-plan.txt"], await PathsAsync(rita, "plan"));
        // Administrators reach every home in Files, but search only their own.
        Assert.Contains("admin-plan.txt", await PathsAsync(_admin, "plan"));
        Assert.DoesNotContain("rita-plan.txt", await PathsAsync(_admin, "plan"));

        var ritaUser = (await _admin.GetFromJsonAsync<JsonElement>("/api/users", Token)).EnumerateArray().Single(user => user.GetProperty("username").GetString() == "rita").GetProperty("id").GetInt64();
        await _admin.SetShareAccessAsync(_share, ritaUser, "read");
        var granted = await PathsAsync(rita, "plan");
        Assert.Equal("plan.md", granted[0]);
        Assert.Contains("rita-plan.txt", granted);
    }

    [Fact]
    public async Task ARemovedShareLeavesTheIndexAndAHoldLetsGoOfItsFolder()
    {
        using (var hold = Index.Hold([_root]))
        {
            // Held: nothing below is read, the hits that are there are still checked on disk.
            File.WriteAllText(Path.Combine(_root, "held plan.txt"), "x");
            await CatchUpAsync();
            Assert.DoesNotContain("held plan.txt", await PathsAsync(_admin, "plan"));
        }
        await Index.IdleAsync();
        Assert.Contains("held plan.txt", await PathsAsync(_admin, "plan"));

        var shares = _factory.Services.GetRequiredService<ShareService>();
        Assert.True(await shares.UnregisterAsync(_share, Token));
        _factory.Services.GetRequiredService<ShareChanges>().Announce();
        await Index.IdleAsync();

        var stores = Index.Find([_share], SearchRanking.Terms("plan"), 10, out var complete);
        Assert.Empty(stores);
        Assert.False(complete);
    }
}
