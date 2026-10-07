using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>
/// What the Store changes on an installed app (A12): the prefilled access of D304, revoking permissions, mapping folders
/// to storage requests, the debug switch and who may use the app. Administrators only.
/// </summary>
public sealed class StoreApiTests : IAsyncLifetime
{
    private const string AppId = "com.example.probe";
    private const string Pool = "pithyxtestm";
    private const string Storage = ",\n  \"storage\": [{ \"id\": \"media\", \"name\": { \"en\": \"Media\" }, \"access\": \"read\", \"required\": false }, { \"id\": \"inbox\", \"name\": { \"en\": \"Inbox\" }, \"access\": \"readWrite\", \"required\": true }]";
    private readonly DaemonFactory _factory = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateApiClient();
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Package(string permissions = "\"notifications\", \"files.read\"", string extra = Storage) =>
        TarBuilder.MinimalPackage(AppId, "1.0.0", TarBuilder.Manifest(AppId, "1.0.0", permissions, extra)).End().Gzip();

    private async Task<JsonElement> UploadAsync(byte[] package)
    {
        using var response = await _admin.PutAsync("/api/apps/packages", new ByteArrayContent(package), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task InstallAsync(byte[] package, object request)
    {
        var packageId = (await UploadAsync(package)).GetProperty("packageId").GetString();
        using var response = await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", request, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.JsonAsync()).GetProperty("id").GetInt64();
        var jobs = _factory.Services.GetRequiredService<JobService>();
        Job? job = null;
        await JobTestSupport.WaitUntilAsync(async () => (job = await jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State is not (JobState.Queued or JobState.Running), $"job {id}");
        Assert.Equal(JobState.Succeeded, job!.State);
    }

    private async Task<HttpClient> UserClientAsync(string username)
    {
        var id = await _factory.InsertUserAsync(username);
        return _factory.ClientWithCookie(await _factory.SessionCookieAsync(id));
    }

    private async Task<(long Id, string Path)> SharedAsync()
    {
        var share = (await _admin.GetFromJsonAsync<JsonElement>("/api/shares/details", Token)).EnumerateArray()
            .Single(share => share.GetProperty("name").GetString() == "Shared");
        return (share.GetProperty("id").GetInt64(), share.GetProperty("path").GetString()!);
    }

    private async Task<long> UsersGroupAsync() =>
        (await _admin.GetFromJsonAsync<JsonElement>("/api/groups", Token)).EnumerateArray()
            .Single(group => group.GetProperty("builtin").GetBoolean()).GetProperty("id").GetInt64();

    private AppSummary? Summary() => _factory.Services.GetRequiredService<AppRegistry>().List().Apps.SingleOrDefault(app => app.Id == AppId);

    [Theory]
    [InlineData("\"notifications\"", "", AppAccess.Everybody)]
    [InlineData("\"notifications\", \"network.internet\", \"files.write\"", "", AppAccess.Everybody)]
    [InlineData("\"notifications\", \"directory.read\"", "", AppAccess.Admins)]
    [InlineData("", ", \"backend\": { \"root\": false }", AppAccess.Everybody)]
    [InlineData("", ", \"backend\": { \"root\": true }", AppAccess.Admins)]
    [InlineData("", ", \"backend\": { \"elevated\": [] }", AppAccess.Everybody)]
    [InlineData("", ", \"backend\": { \"elevated\": [\"device:/dev/dri/renderD128\"] }", AppAccess.Admins)]
    public void AppsWithElevatedRightsRootOrTheDirectoryAreSuggestedForAdministratorsOnly(string permissions, string backend, AppAccess expected)
    {
        using var manifest = JsonDocument.Parse($"{{ \"permissions\": [{permissions}]{backend} }}");
        Assert.Equal(expected, AppService.SuggestedAccess(manifest.RootElement));
    }

    [Fact]
    public async Task AnInstallWithoutAccessTakesTheSuggestedOne()
    {
        var preview = await UploadAsync(Package("\"directory.read\""));
        Assert.Equal("admins", preview.GetProperty("suggestedAccess").GetString());
        await InstallAsync(Package("\"directory.read\""), new { pool = Pool });
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM grants WHERE resource = 'app:' || @AppId", new { AppId }));
    }

    [Fact]
    public async Task EveryStoreChangeIsForAdministratorsOnly()
    {
        await InstallAsync(Package(), new { pool = Pool, access = "everybody" });
        using var rita = await UserClientAsync("rita");
        var (shareId, _) = await SharedAsync();
        var calls = new Func<HttpClient, Task<HttpResponseMessage>>[]
        {
            client => client.PutAsJsonAsync($"/api/apps/{AppId}/permissions", new { permissions = new[] { new { permission = "notifications", granted = false } } }, Token),
            client => client.PutAsJsonAsync($"/api/apps/{AppId}/storage/media", new { shareId, path = "", access = "read" }, Token),
            client => client.DeleteAsync($"/api/apps/{AppId}/storage/media", Token),
            client => client.PutAsJsonAsync($"/api/apps/{AppId}/log-level", new { level = "debug" }, Token),
            client => client.GetAsync($"/api/apps/{AppId}/grants", Token),
            client => client.PutAsJsonAsync($"/api/apps/{AppId}/grants", new { grants = Array.Empty<object>() }, Token),
        };
        foreach (var call in calls)
        {
            using var refused = await call(rita);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            using var allowed = await call(_admin);
            Assert.True(allowed.IsSuccessStatusCode, $"{allowed.RequestMessage!.Method} {allowed.RequestMessage.RequestUri}: {await allowed.Content.ReadAsStringAsync(Token)}");
        }
    }

    [Fact]
    public async Task ARevokedPermissionLeavesTheAppsListAndOnlyRequestedOnesChange()
    {
        await InstallAsync(Package(), new { pool = Pool });
        Assert.Equal(["files.read", "notifications"], Summary()!.Permissions);

        using (var revoke = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/permissions", new { permissions = new[] { new { permission = "files.read", granted = false } } }, Token))
        {
            var permissions = (await revoke.JsonAsync()).GetProperty("permissions").EnumerateArray()
                .Select(permission => (permission.GetProperty("permission").GetString(), permission.GetProperty("granted").GetBoolean()));
            Assert.Equal([("files.read", false), ("notifications", true)], permissions);
        }
        Assert.Equal(["notifications"], Summary()!.Permissions);

        using (var unknown = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/permissions", new { permissions = new[] { new { permission = "directory.read", granted = true } } }, Token))
        {
            Assert.Equal("app.permission.not_requested", await unknown.ErrorCodeAsync());
        }
        using (var grantAgain = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/permissions", new { permissions = new[] { new { permission = "files.read", granted = true } } }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, grantAgain.StatusCode);
        }
        Assert.Equal(["files.read", "notifications"], Summary()!.Permissions);
        using var missing = await _admin.PutAsJsonAsync("/api/apps/com.example.missing/permissions", new { permissions = Array.Empty<object>() }, Token);
        Assert.Equal("app.app.not_found", await missing.ErrorCodeAsync());
    }

    [Fact]
    public async Task OnlyExistingFoldersMapToStorageRequestsAndNeverWithMoreAccessThanAsked()
    {
        await InstallAsync(Package(), new { pool = Pool });
        var (shareId, sharePath) = await SharedAsync();
        Directory.CreateDirectory(Path.Combine(sharePath, "Photos", "2026"));
        File.CreateSymbolicLink(Path.Combine(sharePath, "outside"), Path.GetTempPath());

        using (var mapped = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/storage/media", new { shareId, path = "/Photos/2026/", access = "read" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, mapped.StatusCode);
            var storage = (await mapped.JsonAsync()).GetProperty("storage").EnumerateArray().Single();
            Assert.Equal(("media", shareId, "Photos/2026", "read"), (storage.GetProperty("storageId").GetString(), storage.GetProperty("shareId").GetInt64(), storage.GetProperty("path").GetString(), storage.GetProperty("access").GetString()));
        }
        var refusals = new (string StorageId, object Body, string Code)[]
        {
            ("media", new { shareId, path = "Photos", access = "readWrite" }, "app.mapping.invalid"),
            ("other", new { shareId, path = "", access = "read" }, "app.mapping.invalid"),
            ("inbox", new { shareId, path = "Missing", access = "readWrite" }, "fs.entry.not_found"),
            ("inbox", new { shareId, path = "outside", access = "readWrite" }, "fs.entry.not_found"),
            ("inbox", new { shareId, path = "../..", access = "readWrite" }, "fs.path.invalid"),
            ("inbox", new { shareId = 999, path = "", access = "readWrite" }, "fs.share.not_found"),
        };
        foreach (var (storageId, body, code) in refusals)
        {
            using var refused = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/storage/{storageId}", body, Token);
            Assert.Equal(code, await refused.ErrorCodeAsync());
        }

        // A read-write request may be mapped read-only, and the root of the shared folder is a folder too.
        using (var inbox = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/storage/inbox", new { shareId, access = "read" }, Token))
        {
            Assert.Equal(["inbox", "media"], (await inbox.JsonAsync()).GetProperty("storage").EnumerateArray().Select(mapping => mapping.GetProperty("storageId").GetString()));
        }
        using (var unmapped = await _admin.DeleteAsync($"/api/apps/{AppId}/storage/media", Token))
        {
            Assert.Equal(["inbox"], (await unmapped.JsonAsync()).GetProperty("storage").EnumerateArray().Select(mapping => mapping.GetProperty("storageId").GetString()));
        }

        // An update that drops a request drops its mapping.
        var update = TarBuilder.MinimalPackage(AppId, "1.1.0", TarBuilder.Manifest(AppId, "1.1.0", "\"notifications\"")).End().Gzip();
        await InstallAsync(update, new { });
        Assert.Empty(_factory.Services.GetRequiredService<AppRegistry>().Find(AppId)!.Storage);
    }

    [Fact]
    public async Task TheDebugSwitchReachesTheAppsList()
    {
        await InstallAsync(Package(), new { pool = Pool });
        Assert.Equal(AppLogLevel.Info, Summary()!.LogLevel);
        using (var debug = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/log-level", new { level = "debug" }, Token))
        {
            Assert.Equal("debug", (await debug.JsonAsync()).GetProperty("logLevel").GetString());
        }
        Assert.Equal(AppLogLevel.Debug, Summary()!.LogLevel);
        using var invalid = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/log-level", new { level = "trace" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task WhoMayUseAnAppIsAGrantOfUsersAndGroups()
    {
        await InstallAsync(Package(), new { pool = Pool, access = "admins" });
        var rita = await _factory.InsertUserAsync("rita");
        var everybody = await UsersGroupAsync();
        var permissions = _factory.Services.GetRequiredService<Pithyx.Daemon.Permissions.PermissionService>();
        var registry = _factory.Services.GetRequiredService<AppRegistry>();
        async Task<bool> RitaMayUseAsync() => registry.Allows(await permissions.ForUserAsync(rita, Token), AppId);

        var grants = await _admin.GetFromJsonAsync<JsonElement>($"/api/apps/{AppId}/grants", Token);
        Assert.Empty(grants.GetProperty("grants").EnumerateArray());
        Assert.Contains(grants.GetProperty("users").EnumerateArray(), user => user.GetProperty("administrator").GetBoolean() && user.GetProperty("allowed").GetBoolean());
        Assert.False(await RitaMayUseAsync());

        using (var toRita = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/grants", new { grants = new[] { new { subjectKind = "user", subjectId = rita, level = "allow" } } }, Token))
        {
            var ritaAccess = (await toRita.JsonAsync()).GetProperty("users").EnumerateArray().Single(user => user.GetProperty("userId").GetInt64() == rita);
            Assert.True(ritaAccess.GetProperty("allowed").GetBoolean());
        }
        Assert.True(await RitaMayUseAsync());

        using (var toEverybody = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/grants", new { grants = new[] { new { subjectKind = "group", subjectId = everybody, level = "allow" } } }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, toEverybody.StatusCode);
        }
        Assert.True(await RitaMayUseAsync());
        Assert.Equal(1, await _factory.ScalarAsync<long>("SELECT count(*) FROM grants WHERE resource = 'app:' || @AppId", new { AppId }));

        var refusals = new (string AppId, object Body, string Code)[]
        {
            (AppId, new { grants = new[] { new { subjectKind = "user", subjectId = rita, level = "read" } } }, "permissions.grant.invalid"),
            (AppId, new { grants = new[] { new { subjectKind = "user", subjectId = rita, level = "allow" }, new { subjectKind = "user", subjectId = rita, level = "allow" } } }, "permissions.grant.invalid"),
            (AppId, new { grants = new[] { new { subjectKind = "user", subjectId = 999L, level = "allow" } } }, "users.user.not_found"),
            (AppId, new { grants = new[] { new { subjectKind = "group", subjectId = 999L, level = "allow" } } }, "users.group.not_found"),
            ("com.example.missing", new { grants = Array.Empty<object>() }, "app.app.not_found"),
            ("org.pithyx.files", new { grants = Array.Empty<object>() }, "app.app.not_found"),
        };
        foreach (var (appId, body, code) in refusals)
        {
            using var refused = await _admin.PutAsJsonAsync($"/api/apps/{appId}/grants", body, Token);
            Assert.Equal(code, await refused.ErrorCodeAsync());
        }

        using (var nobody = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/grants", new { grants = Array.Empty<object>() }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, nobody.StatusCode);
        }
        Assert.False(await RitaMayUseAsync());
    }
}
