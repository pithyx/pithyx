using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Users;

public sealed class GroupApiTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public GroupApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private const long BuiltinGroup = 100;
    private const long UnknownUser = 4711;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<long> CreateUserAsync(string username)
    {
        using var response = await _admin.PostAsync("/api/users", new { username, password = "river-candle-orbit-7" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> CreateGroupAsync(string name, params long[] memberIds)
    {
        using var response = await _admin.PostAsync("/api/groups", new { name, description = "", memberIds });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<JsonElement> GroupsAsync() => await _admin.GetFromJsonAsync<JsonElement>("/api/groups", Token);

    [Fact]
    public async Task GroupsGetGidsFromTheRangeThatAreNeverGivenOutAgain()
    {
        await _admin.SetupAdminAsync();

        var media = await CreateGroupAsync("media");
        Assert.Equal(30000, media.GetProperty("id").GetInt64());
        using (var deleted = await _admin.DeleteAsync("/api/groups/30000", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        var again = await CreateGroupAsync("media");

        Assert.Equal(30001, again.GetProperty("id").GetInt64());
        Assert.Equal(
            [("users", 100L, true), ("media", 30001L, false)],
            (await GroupsAsync()).EnumerateArray().Select(group => (group.GetProperty("name").GetString()!, group.GetProperty("id").GetInt64(), group.GetProperty("builtin").GetBoolean())));
    }

    [Fact]
    public async Task MembersAreSetFromTheGroupAndFromTheUser()
    {
        await _admin.SetupAdminAsync();
        var rita = await CreateUserAsync("rita");
        var tom = await CreateUserAsync("tom");
        var media = (await CreateGroupAsync("media", rita)).GetProperty("id").GetInt64();
        var office = (await CreateGroupAsync("office")).GetProperty("id").GetInt64();

        using (var members = await _admin.PatchAsJsonAsync($"/api/groups/{media}", new { memberIds = new[] { rita, tom } }, Token))
        {
            Assert.Equal([rita, tom], (await members.JsonAsync()).GetProperty("memberIds").EnumerateArray().Select(id => id.GetInt64()));
        }
        using (var user = await _admin.PatchAsJsonAsync($"/api/users/{tom}", new { groupIds = new[] { office } }, Token))
        {
            Assert.Equal([office], (await user.JsonAsync()).GetProperty("groupIds").EnumerateArray().Select(id => id.GetInt64()));
        }

        var groups = (await GroupsAsync()).EnumerateArray().ToDictionary(group => group.GetProperty("name").GetString()!, group => group.GetProperty("memberIds").EnumerateArray().Select(id => id.GetInt64()).ToList());
        Assert.Equal([rita], groups["media"]);
        Assert.Equal([tom], groups["office"]);
        // Everybody is in the built-in group, the administrator from the setup included.
        Assert.Equal(3, groups["users"].Count);

        // A deleted user leaves its groups.
        using (var deleted = await _admin.DeleteAsync($"/api/users/{rita}", Token))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }
        Assert.Empty((await GroupsAsync()).EnumerateArray().Single(group => group.GetProperty("name").GetString() == "media").GetProperty("memberIds").EnumerateArray());
    }

    [Fact]
    public async Task TheBuiltInGroupCannotBeChanged()
    {
        await _admin.SetupAdminAsync();
        var rita = await CreateUserAsync("rita");

        using var rename = await _admin.PatchAsJsonAsync("/api/groups/100", new { name = "everybody" }, Token);
        Assert.Equal("users.group.builtin", await rename.ErrorCodeAsync());
        using var delete = await _admin.DeleteAsync("/api/groups/100", Token);
        Assert.Equal("users.group.builtin", await delete.ErrorCodeAsync());
        using var join = await _admin.PatchAsJsonAsync($"/api/users/{rita}", new { groupIds = new long[] { BuiltinGroup } }, Token);
        Assert.Equal("users.group.builtin", await join.ErrorCodeAsync());
    }

    [Fact]
    public async Task NamesOfSystemGroupsTakenNamesAndUnknownIdsAreRefused()
    {
        await _admin.SetupAdminAsync();
        await CreateGroupAsync("media");

        // The simulation's /etc/group has the machine owner's group outside the Pithyx range.
        using var foreign = await _admin.PostAsync("/api/groups", new { name = "owner" });
        Assert.Equal("users.group.refused", await foreign.ErrorCodeAsync());
        using var reserved = await _admin.PostAsync("/api/groups", new { name = "sudo" });
        Assert.Equal("users.group.name_reserved", await reserved.ErrorCodeAsync());
        using var ours = await _admin.PostAsync("/api/groups", new { name = "pithyx-sftp" });
        Assert.Equal("users.group.name_reserved", await ours.ErrorCodeAsync());
        using var invalid = await _admin.PostAsync("/api/groups", new { name = "two words" });
        Assert.Equal("users.group.name_invalid", await invalid.ErrorCodeAsync());
        using var taken = await _admin.PostAsync("/api/groups", new { name = "Media" });
        Assert.Equal("users.group.exists", await taken.ErrorCodeAsync());
        using var nobody = await _admin.PostAsync("/api/groups", new { name = "office", memberIds = new long[] { UnknownUser } });
        Assert.Equal("users.user.not_found", await nobody.ErrorCodeAsync());
        using var description = await _admin.PostAsync("/api/groups", new { name = "office", description = new string('x', 201) });
        Assert.Equal("users.group.description_invalid", await description.ErrorCodeAsync());
        using var missing = await _admin.DeleteAsync("/api/groups/39999", Token);
        Assert.Equal("users.group.not_found", await missing.ErrorCodeAsync());
    }

    [Fact]
    public async Task OnlyAdministratorsManageGroups()
    {
        await _admin.SetupAdminAsync();
        var rita = await CreateUserAsync("rita");
        using var ritaClient = _factory.ClientWithCookie(await _factory.SessionCookieAsync(rita));

        using var list = await ritaClient.GetAsync("/api/groups", Token);
        Assert.Equal("auth.permission.denied", await list.ErrorCodeAsync());
        using var create = await ritaClient.PostAsync("/api/groups", new { name = "media" });
        Assert.Equal("auth.permission.denied", await create.ErrorCodeAsync());
    }
}
