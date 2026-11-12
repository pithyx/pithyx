using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Shares;

/// <summary>
/// Apps through Files as root on the dev VM (D409): what Files creates in an app's folder belongs to the folder's owner,
/// the app; no link the app made and no <c>..</c> leads out of the folder, and a user who may only read cannot write.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class AppsShareFilesIntegrationTests : IDisposable
{
    private const string Password = "river-candle-orbit-7";
    private const string AppId = "com.example.probe";
    private const string Pool = "pithyxtestm";
    private const int AppUid = 40950;
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public AppsShareFilesIntegrationTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FilesWritesAsTheAppFollowsNoLinkOutAndReadOnlyCannotWrite()
    {
        FileVdevs.RequireEnabled();
        await _admin.SetupAdminAsync();
        var package = TarBuilder.MinimalPackage(AppId, "1.0.0").End().Gzip();
        using (var upload = await _admin.PutAsync("/api/apps/packages", new ByteArrayContent(package), Token))
        {
            var packageId = (await upload.JsonAsync()).GetProperty("packageId").GetString();
            using var install = await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", new { pool = Pool }, Token);
            var id = (await install.JsonAsync()).GetProperty("id").GetInt64();
            var jobs = _factory.Services.GetRequiredService<JobService>();
            await JobTestSupport.WaitUntilAsync(async () => (await jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State == JobState.Succeeded, "the install");
        }
        long appsId = 0;
        await JobTestSupport.WaitUntilAsync(async () =>
        {
            using var response = await _admin.GetAsync("/api/shares/apps", Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }
            var state = await response.JsonAsync();
            appsId = state.GetProperty("shareId").GetInt64();
            return state.GetProperty("folders").EnumerateArray().Any(folder => folder.GetProperty("id").GetString() == AppId);
        }, "Apps to show the app");

        // The app's folder as its backend leaves it: its own, with links that point somewhere in its container.
        var folder = Path.Combine(_factory.DataDirectory, "apps", AppId);
        var other = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(other, "secret.txt"), "secret", Token);
        File.CreateSymbolicLink(Path.Combine(folder, "etc"), "/etc");
        File.CreateSymbolicLink(Path.Combine(folder, "passwd"), "/etc/passwd");
        File.CreateSymbolicLink(Path.Combine(folder, "up"), "../../outside");
        File.CreateSymbolicLink(Path.Combine(folder, "secret.txt"), Path.Combine(other, "secret.txt"));
        LinuxOwner(folder, AppUid);

        using (var passwd = await _admin.GetAsync($"/api/files/{appsId}/content?path={AppId}/passwd", Token))
        {
            Assert.NotEqual(HttpStatusCode.OK, passwd.StatusCode);
        }
        using (var secret = await _admin.GetAsync($"/api/files/{appsId}/content?path={AppId}/secret.txt", Token))
        {
            Assert.NotEqual(HttpStatusCode.OK, secret.StatusCode);
        }
        foreach (var path in new[] { $"{AppId}/etc", $"{AppId}/up", $"{AppId}/..", $"{AppId}/../..", "..", $"{AppId}/up/.." })
        {
            // A path that stays inside Apps may list; nothing outside it ever shows.
            using var listing = await _admin.GetAsync($"/api/files/{appsId}/list?path={Uri.EscapeDataString(path)}", Token);
            var body = await listing.Content.ReadAsStringAsync(Token);
            Assert.True(listing.StatusCode != HttpStatusCode.OK || !(body.Contains("secret.txt", StringComparison.Ordinal) || body.Contains("\"outside\"", StringComparison.Ordinal) || body.Contains("\"hosts\"", StringComparison.Ordinal)),
                $"{path} listed what lies outside Apps: {body}");
        }
        // Writing through a link goes nowhere either.
        using (var through = await _admin.PutAsync($"/api/files/{appsId}/content?path={Uri.EscapeDataString($"{AppId}/up/planted.txt")}", new StringContent("x"), Token))
        {
            Assert.NotEqual(HttpStatusCode.Created, through.StatusCode);
            Assert.False(File.Exists(Path.Combine(other, "planted.txt")));
        }

        // What Files creates belongs to the app, like Samba's inherit owner.
        using (var created = await _admin.PutAsync($"/api/files/{appsId}/content?path={Uri.EscapeDataString($"{AppId}/note.txt")}", new StringContent("note"), Token))
        {
            Assert.True(created.IsSuccessStatusCode, created.StatusCode.ToString());
        }
        using (var made = await _admin.PostAsJsonAsync($"/api/files/{appsId}/folders", new { path = $"{AppId}/config" }, Token))
        {
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }
        Assert.Equal(($"{AppUid}", $"{AppUid}"), await OwnerAsync(Path.Combine(folder, "note.txt")));
        Assert.Equal(($"{AppUid}", $"{AppUid}"), await OwnerAsync(Path.Combine(folder, "config")));

        // A user who may read sees the data and cannot change it.
        using var ritaCreated = await _admin.PostAsJsonAsync("/api/users", new { username = "rita", password = Password, grants = new[] { new { resource = "right:desktopLogin", level = "allow" } } }, Token);
        var rita = (await ritaCreated.JsonAsync()).GetProperty("id").GetInt64();
        using (var grant = await _admin.PutAsJsonAsync($"/api/shares/{appsId}/grants", new { grants = new[] { new { subjectKind = "user", subjectId = rita, level = "read" } }, confirmAppsAccess = true }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        }
        using var reader = _factory.CreateApiClient();
        using (var login = await reader.LoginAsync("rita", Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        Assert.Equal("note", await reader.GetStringAsync($"/api/files/{appsId}/content?path={Uri.EscapeDataString($"{AppId}/note.txt")}", Token));
        using (var write = await reader.PutAsync($"/api/files/{appsId}/content?path={Uri.EscapeDataString($"{AppId}/rita.txt")}", new StringContent("x"), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        }
        using (var rename = await reader.PostAsJsonAsync($"/api/files/{appsId}/rename", new { path = $"{AppId}/note.txt", name = "gone.txt" }, Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        }
        using (var delete = await reader.PostAsJsonAsync("/api/files/operations", new { kind = "delete", items = new[] { new { shareId = appsId, path = $"{AppId}/note.txt" } }, permanent = true }, Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }
        Assert.False(File.Exists(Path.Combine(folder, "rita.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "note.txt")));
    }

    private static void LinuxOwner(string path, int uid)
    {
        using var process = System.Diagnostics.Process.Start("chown", ["-h", $"{uid}:{uid}", path])!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<(string Uid, string Gid)> OwnerAsync(string path)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%u %g", path]) { RedirectStandardOutput = true })!;
        var output = (await process.StandardOutput.ReadToEndAsync(Token)).Trim().Split(' ');
        await process.WaitForExitAsync(Token);
        return (output[0], output[1]);
    }
}
