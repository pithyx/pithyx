using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Shares;

/// <summary>
/// The special folder Apps (D409) in simulation, where each app's folder is a plain folder: administrators only until
/// an administrator confirms giving it to others (and the other administrators hear of it), its name and protocols
/// stay, its top level holds only the app folders, it has no recycle bin, and an uninstall that kept the data leaves
/// the folder as not installed.
/// </summary>
public sealed class AppsShareApiTests : IDisposable
{
    private const string Password = "river-candle-orbit-7";
    private const string AppId = "com.example.probe";
    private const string Pool = "pithyxtestm";
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public AppsShareApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private JobService Jobs => _factory.Services.GetRequiredService<JobService>();

    private async Task<long> UserAsync(string username, bool admin = false)
    {
        string[] rights = admin ? ["right:desktopLogin", "right:admin"] : ["right:desktopLogin"];
        using var response = await _admin.PostAsJsonAsync("/api/users", new { username, password = Password, grants = rights.Select(resource => new { resource, level = "allow" }) }, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetInt64();
    }

    private async Task<HttpClient> SignInAsync(string username)
    {
        var client = _factory.CreateApiClient();
        using var login = await client.LoginAsync(username, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task InstallAsync()
    {
        var package = TarBuilder.MinimalPackage(AppId, "1.0.0", TarBuilder.Manifest(AppId, "1.0.0", "\"notifications\"")).End().Gzip();
        using var upload = await _admin.PutAsync("/api/apps/packages", new ByteArrayContent(package), Token);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var packageId = (await upload.JsonAsync()).GetProperty("packageId").GetString();
        using var install = await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", new { pool = Pool }, Token);
        Assert.Equal(HttpStatusCode.Accepted, install.StatusCode);
        await FinishedAsync(await install.JsonAsync());
    }

    private async Task FinishedAsync(JsonElement started)
    {
        var id = started.GetProperty("id").GetInt64();
        Job? job = null;
        await JobTestSupport.WaitUntilAsync(async () => (job = await Jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State is not (JobState.Queued or JobState.Running), $"job {id}");
        Assert.True(job!.State == JobState.Succeeded, $"{job.Kind} ended {job.State}: {job.ErrorCode}");
    }

    private static async Task<JsonElement?> AppsOfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/shares", Token)).EnumerateArray()
            .Where(share => share.GetProperty("kind").GetString() == "apps")
            .Select(share => (JsonElement?)share)
            .SingleOrDefault();

    /// <summary>Apps once it shows the installed app's folder.</summary>
    private async Task<long> AppsReadyAsync()
    {
        long id = 0;
        await JobTestSupport.WaitUntilAsync(async () =>
        {
            using var response = await _admin.GetAsync("/api/shares/apps", Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }
            var state = await response.JsonAsync();
            id = state.GetProperty("shareId").GetInt64();
            return state.GetProperty("folders").EnumerateArray().Any(folder => folder.GetProperty("id").GetString() == AppId);
        }, "Apps to show the app");
        return id;
    }

    private static object ShareGrant(long userId, string level, bool confirm) =>
        new { grants = new[] { new { subjectKind = "user", subjectId = userId, level } }, confirmAppsAccess = confirm };

    [Fact]
    public async Task AppsIsForAdministratorsAndAppearsWithTheAppPool()
    {
        await _admin.SetupAdminAsync();
        await UserAsync("rita");
        using var rita = await SignInAsync("rita");
        Assert.Null(await AppsOfAsync(_admin));
        using (var before = await _admin.GetAsync("/api/shares/apps", Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        await InstallAsync();
        var appsId = await AppsReadyAsync();

        var apps = (await AppsOfAsync(_admin))!.Value;
        Assert.Equal("Apps", apps.GetProperty("name").GetString());
        Assert.Equal(appsId, apps.GetProperty("id").GetInt64());
        Assert.Equal("readWrite", apps.GetProperty("access").GetString());
        var state = await _admin.GetFromJsonAsync<JsonElement>("/api/shares/apps", Token);
        var folder = Assert.Single(state.GetProperty("folders").EnumerateArray());
        Assert.True(folder.GetProperty("installed").GetBoolean());
        Assert.False(folder.GetProperty("running").GetBoolean());
        Assert.Equal(0, state.GetProperty("smbConnections").GetInt32());
        var listing = await _admin.GetFromJsonAsync<JsonElement>($"/api/files/{appsId}/list", Token);
        Assert.Contains(listing.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("name").GetString() == AppId);

        Assert.Null(await AppsOfAsync(rita));
        using var refused = await rita.GetAsync("/api/shares/apps", Token);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        using var files = await rita.GetAsync($"/api/files/{appsId}/list", Token);
        Assert.Equal(HttpStatusCode.NotFound, files.StatusCode);
    }

    [Fact]
    public async Task GivingAppsToOthersNeedsTheConfirmationAndTellsTheOtherAdministrators()
    {
        await _admin.SetupAdminAsync();
        var rita = await UserAsync("rita");
        var ada = await UserAsync("ada", admin: true);
        await InstallAsync();
        var appsId = await AppsReadyAsync();

        using (var unconfirmed = await _admin.PutAsJsonAsync($"/api/shares/{appsId}/grants", ShareGrant(rita, "read", confirm: false), Token))
        {
            Assert.Equal(HttpStatusCode.Conflict, unconfirmed.StatusCode);
            Assert.Equal("fs.apps.confirmation_required", await unconfirmed.ErrorCodeAsync());
        }
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM grants WHERE resource = @resource", new { resource = $"share:{appsId}" }));

        using (var confirmed = await _admin.PutAsJsonAsync($"/api/shares/{appsId}/grants", ShareGrant(rita, "read", confirm: true), Token))
        {
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        }
        using (var ritaClient = await SignInAsync("rita"))
        {
            Assert.Equal("read", (await AppsOfAsync(ritaClient))!.Value.GetProperty("access").GetString());
            var state = await ritaClient.GetFromJsonAsync<JsonElement>("/api/shares/apps", Token);
            Assert.Equal(JsonValueKind.Null, state.GetProperty("smbConnections").ValueKind);
        }
        Assert.Equal(1, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE user_id = @ada AND code = 'shares.apps.granted'", new { ada }));
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE code = 'shares.apps.granted' AND user_id <> @ada", new { ada }));
        Assert.Contains("\"subject\":\"rita\"", await _factory.ScalarAsync<string>("SELECT params FROM notifications WHERE user_id = @ada AND code = 'shares.apps.granted'", new { ada }), StringComparison.Ordinal);

        // Raising read to read and write widens it again; keeping or narrowing it does not.
        using (var raise = await _admin.PutAsJsonAsync($"/api/shares/{appsId}/grants", ShareGrant(rita, "readWrite", confirm: false), Token))
        {
            Assert.Equal("fs.apps.confirmation_required", await raise.ErrorCodeAsync());
        }
        using (var same = await _admin.PutAsJsonAsync($"/api/shares/{appsId}/grants", ShareGrant(rita, "read", confirm: false), Token))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        }
        using (var none = await _admin.PutAsJsonAsync($"/api/shares/{appsId}/grants", new { grants = Array.Empty<object>() }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        }

        // The user and group editors and a new user ask the same.
        var resource = $"share:{appsId}";
        using (var userEditor = await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", new { grants = new object[] { new { resource = "right:desktopLogin", level = "allow" }, new { resource, level = "read" } } }, Token))
        {
            Assert.Equal("fs.apps.confirmation_required", await userEditor.ErrorCodeAsync());
        }
        using (var group = await _admin.PostAsJsonAsync("/api/groups", new { name = "media", memberIds = Array.Empty<long>() }, Token))
        {
            var groupId = (await group.JsonAsync()).GetProperty("id").GetInt64();
            using var groupEditor = await _admin.PutAsJsonAsync($"/api/groups/{groupId}/grants", new { grants = new[] { new { resource, level = "readWrite" } } }, Token);
            Assert.Equal("fs.apps.confirmation_required", await groupEditor.ErrorCodeAsync());
            using var confirmedGroup = await _admin.PutAsJsonAsync($"/api/groups/{groupId}/grants", new { grants = new[] { new { resource, level = "readWrite" } }, confirmAppsAccess = true }, Token);
            Assert.Equal(HttpStatusCode.OK, confirmedGroup.StatusCode);
        }
        using (var newUser = await _admin.PostAsJsonAsync("/api/users", new { username = "tom", password = Password, grants = new[] { new { resource, level = "read" } } }, Token))
        {
            Assert.Equal("fs.apps.confirmation_required", await newUser.ErrorCodeAsync());
        }
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM users WHERE username = 'tom'"));
        Assert.Equal(2, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE user_id = @ada AND code = 'shares.apps.granted'", new { ada }));
    }

    [Fact]
    public async Task AppsKeepsItsNameIsOfferedOverSmbOnlyAndIsNeverDeleted()
    {
        await _admin.SetupAdminAsync();
        await InstallAsync();
        var appsId = await AppsReadyAsync();

        var details = (await _admin.GetFromJsonAsync<JsonElement>("/api/shares/details", Token)).EnumerateArray().Single(share => share.GetProperty("id").GetInt64() == appsId);
        Assert.Equal("apps", details.GetProperty("system").GetString());
        Assert.Equal("Apps", details.GetProperty("name").GetString());
        Assert.True(details.GetProperty("smb").GetBoolean());
        Assert.False(details.GetProperty("sftp").GetBoolean());

        foreach (var change in new object[] { new { name = "Programs" }, new { description = "x" }, new { sftp = true }, new { webDav = true }, new { ftp = true } })
        {
            using var refused = await _admin.PatchAsJsonAsync($"/api/shares/{appsId}", change, Token);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("fs.share.system", await refused.ErrorCodeAsync());
        }
        using (var smb = await _admin.PatchAsJsonAsync($"/api/shares/{appsId}", new { smb = false }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, smb.StatusCode);
            Assert.False((await smb.JsonAsync()).GetProperty("smb").GetBoolean());
        }
        using (var delete = await _admin.PostAsJsonAsync("/api/shares/plans", new { action = "share.delete", shareId = appsId, confirmName = "Apps", data = "keep" }, Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }
        using var reserved = await _admin.PostAsJsonAsync("/api/shares", new { name = "apps", path = _factory.DataDirectory }, Token);
        Assert.Equal("fs.share.name_reserved", await reserved.ErrorCodeAsync());
    }

    [Fact]
    public async Task FilesChangesOnlyInsideAnAppsFolderAndDeletesThereForGood()
    {
        await _admin.SetupAdminAsync();
        await InstallAsync();
        var appsId = await AppsReadyAsync();

        using (var atTop = await _admin.PostAsJsonAsync($"/api/files/{appsId}/folders", new { path = "mine" }, Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, atTop.StatusCode);
            Assert.Equal("fs.apps.root_fixed", await atTop.ErrorCodeAsync());
        }
        using (var upload = await _admin.PutAsync($"/api/files/{appsId}/content?path=top.txt", new StringContent("x"), Token))
        {
            Assert.Equal("fs.apps.root_fixed", await upload.ErrorCodeAsync());
        }
        using (var rename = await _admin.PostAsJsonAsync($"/api/files/{appsId}/rename", new { path = AppId, name = "other" }, Token))
        {
            Assert.Equal("fs.apps.root_fixed", await rename.ErrorCodeAsync());
        }
        using (var deleteApp = await _admin.PostAsync("/api/files/operations", new { kind = "delete", items = new[] { new { shareId = appsId, path = AppId } }, permanent = true }))
        {
            Assert.Equal("fs.apps.root_fixed", await deleteApp.ErrorCodeAsync());
        }

        using (var inside = await _admin.PostAsJsonAsync($"/api/files/{appsId}/folders", new { path = $"{AppId}/config" }, Token))
        {
            Assert.Equal(HttpStatusCode.Created, inside.StatusCode);
        }
        using (var toBin = await _admin.PostAsync("/api/files/operations", new { kind = "delete", items = new[] { new { shareId = appsId, path = $"{AppId}/config" } } }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, toBin.StatusCode);
            Assert.Equal("fs.apps.trash_unsupported", await toBin.ErrorCodeAsync());
        }
        using (var forGood = await _admin.PostAsync("/api/files/operations", new { kind = "delete", items = new[] { new { shareId = appsId, path = $"{AppId}/config" } }, permanent = true }))
        {
            Assert.Equal(HttpStatusCode.Accepted, forGood.StatusCode);
            await FinishedAsync(await forGood.JsonAsync());
        }
        Assert.False(Directory.Exists(Path.Combine(_factory.DataDirectory, "apps", AppId, "config")));
    }

    [Fact]
    public async Task KeptDataStaysAsNotInstalledAndDeletedDataGoes()
    {
        await _admin.SetupAdminAsync();
        await InstallAsync();
        await AppsReadyAsync();

        using (var keep = await _admin.DeleteAsync($"/api/apps/{AppId}?data=keep", Token))
        {
            await FinishedAsync(await keep.JsonAsync());
        }
        var kept = await _admin.GetFromJsonAsync<JsonElement>("/api/shares/apps", Token);
        var folder = Assert.Single(kept.GetProperty("folders").EnumerateArray());
        Assert.False(folder.GetProperty("installed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, folder.GetProperty("name").ValueKind);

        await InstallAsync();
        using (var delete = await _admin.DeleteAsync($"/api/apps/{AppId}?data=delete", Token))
        {
            await FinishedAsync(await delete.JsonAsync());
        }
        await JobTestSupport.WaitUntilAsync(async () =>
            (await _admin.GetFromJsonAsync<JsonElement>("/api/shares/apps", Token)).GetProperty("folders").GetArrayLength() == 0, "the folder to go");
        await JobTestSupport.WaitUntilAsync(() => Task.FromResult(!Directory.Exists(Path.Combine(_factory.DataDirectory, "apps", AppId))), "the simulated folder to go");
    }
}
