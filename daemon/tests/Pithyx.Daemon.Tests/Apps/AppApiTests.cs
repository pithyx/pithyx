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
/// The app registry through the API in simulation (apps are folders there): upload, check, install, update, disable,
/// remove, the 15 minutes a checked package waits, and the headers of every frontend answer.
/// </summary>
public sealed class AppApiTests : IDisposable
{
    private const string AppId = "com.example.probe";
    private const string Pool = "pithyxtestm";
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public AppApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private JobService Jobs => _factory.Services.GetRequiredService<JobService>();

    private async Task<HttpResponseMessage> UploadAsync(byte[] package, HttpClient? client = null) =>
        await (client ?? _admin).PutAsync("/api/apps/packages", new ByteArrayContent(package), Token);

    private async Task<string> CheckedAsync(byte[] package)
    {
        using var response = await UploadAsync(package);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("packageId").GetString()!;
    }

    private async Task<Job> InstallAsync(byte[] package, object? request = null, JobState expected = JobState.Succeeded)
    {
        var packageId = await CheckedAsync(package);
        using var response = await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", request ?? new { pool = Pool }, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await FinishedAsync(await response.JsonAsync(), expected);
    }

    private async Task<Job> FinishedAsync(JsonElement started, JobState expected = JobState.Succeeded)
    {
        var id = started.GetProperty("id").GetInt64();
        Job? job = null;
        await JobTestSupport.WaitUntilAsync(async () => (job = await Jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State is not (JobState.Queued or JobState.Running), $"job {id}");
        Assert.True(job!.State == expected, $"{job.Kind} ended {job.State}: {job.ErrorCode} {JsonSerializer.Serialize(job.ErrorParams)}");
        return job;
    }

    private static byte[] Package(string version = "1.0.0", string permissions = "\"notifications\"", string id = AppId) =>
        TarBuilder.MinimalPackage(id, version, TarBuilder.Manifest(id, version, permissions))
            .File("frontend/style.css", "body{}")
            .File("frontend/data.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")
            .File("frontend/blob", "x")
            .End().Gzip();

    private string AppFolder => Path.Combine(_factory.DataDirectory, "pools", Pool, ".pithyx", "apps", AppId);

    [Fact]
    public async Task APackageIsCheckedInstalledServedAndRemoved()
    {
        await _admin.SetupAdminAsync();

        using (var preview = await UploadAsync(Package()))
        {
            var body = await preview.JsonAsync();
            Assert.Equal(AppId, body.GetProperty("id").GetString());
            Assert.Equal("install", body.GetProperty("action").GetString());
            Assert.Equal(["notifications"], body.GetProperty("addedPermissions").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(JsonValueKind.Null, body.GetProperty("pool").ValueKind);
        }

        var install = await _admin.PostAsJsonAsync($"/api/apps/packages/{await CheckedAsync(Package())}/install", new { }, Token);
        Assert.Equal("app.pool.required", await install.ErrorCodeAsync());

        await InstallAsync(Package());
        var apps = await _admin.GetFromJsonAsync<JsonElement>("/api/apps", Token);
        Assert.Equal(Pool, apps.GetProperty("pool").GetString());
        var app = apps.GetProperty("apps").EnumerateArray().Single();
        Assert.Equal("sideload", app.GetProperty("source").GetString());
        Assert.InRange(app.GetProperty("uid").GetInt64(), 40000, 49999);
        Assert.True(app.GetProperty("available").GetBoolean());
        Assert.True(File.Exists(Path.Combine(AppFolder, "versions", "1.0.0", "frontend", "index.html")));
        Assert.Equal("versions/1.0.0", new FileInfo(Path.Combine(AppFolder, "current")).LinkTarget);

        using (var page = await _admin.GetAsync($"/apps/{AppId}/index.html", Token))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType!.ToString());
            AssertSandboxed(page);
        }

        // Removing with the data keeps the dataset's volumes; the frontend and the grant go.
        using (var remove = await _admin.DeleteAsync($"/api/apps/{AppId}?data=keep", Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, remove.StatusCode);
            await FinishedAsync(await remove.JsonAsync());
        }
        Assert.True(Directory.Exists(Path.Combine(AppFolder, "volumes")));
        Assert.False(Directory.Exists(Path.Combine(AppFolder, "versions")));
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM grants WHERE resource = 'app:' || @AppId", new { AppId }));
        using (var gone = await _admin.GetAsync($"/apps/{AppId}/index.html", Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            AssertSandboxed(gone);
        }

        // The same app again gets its uid back.
        var uid = app.GetProperty("uid").GetInt64();
        await InstallAsync(Package());
        Assert.Equal(uid, (await _admin.GetFromJsonAsync<JsonElement>("/api/apps", Token)).GetProperty("apps")[0].GetProperty("uid").GetInt64());
        using (var remove = await _admin.DeleteAsync($"/api/apps/{AppId}?data=delete", Token))
        {
            await FinishedAsync(await remove.JsonAsync());
        }
        Assert.False(Directory.Exists(AppFolder));
    }

    [Fact]
    public async Task AnUpdateShowsItsPermissionChangesAndKeepsRevokedOnesRevoked()
    {
        await _admin.SetupAdminAsync();
        await InstallAsync(Package(permissions: "\"notifications\", \"files.read\""));
        await _factory.ScalarAsync<long>("UPDATE app_permissions SET granted = 0 WHERE permission = 'files.read' RETURNING 1");
        await _factory.Services.GetRequiredService<AppRegistry>().ReloadAsync(Token);

        using (var preview = await UploadAsync(Package("1.1.0", "\"files.read\", \"directory.read\"")))
        {
            var body = await preview.JsonAsync();
            Assert.Equal("update", body.GetProperty("action").GetString());
            Assert.Equal("1.0.0", body.GetProperty("installedVersion").GetString());
            Assert.Equal(["directory.read"], body.GetProperty("addedPermissions").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(["notifications"], body.GetProperty("removedPermissions").EnumerateArray().Select(value => value.GetString()));
        }
        await InstallAsync(Package("1.1.0", "\"files.read\", \"directory.read\""), new { });

        var app = (await _admin.GetFromJsonAsync<JsonElement>("/api/apps", Token)).GetProperty("apps")[0];
        Assert.Equal("1.1.0", app.GetProperty("version").GetString());
        Assert.Equal(
            [("directory.read", true), ("files.read", false)],
            app.GetProperty("permissions").EnumerateArray().Select(permission => (permission.GetProperty("permission").GetString(), permission.GetProperty("granted").GetBoolean())));
        // Only the installed version stays.
        Assert.Equal(["1.0.0", "1.1.0"], Directory.GetDirectories(Path.Combine(AppFolder, "versions")).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        using var older = await UploadAsync(Package("0.9.0"));
        Assert.Equal("downgrade", (await older.JsonAsync()).GetProperty("action").GetString());
    }

    [Fact]
    public async Task ACheckedPackageWaitsFifteenMinutesForTheAdministratorWhoUploadedIt()
    {
        await _admin.SetupAdminAsync();
        var packageId = await CheckedAsync(Package());

        await using (var connection = await _factory.Services.GetRequiredService<Pithyx.Daemon.Database.SqliteConnectionFactory>().OpenAsync(Token))
        {
            var other = await TestUsers.InsertAsync(connection, "other", admin: true);
            using var otherAdmin = _factory.ClientWithCookie(await _factory.SessionCookieAsync(other));
            using var stolen = await otherAdmin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", new { pool = Pool }, Token);
            Assert.Equal("app.package.not_found", await stolen.ErrorCodeAsync());
        }

        _factory.Time.Advance(AppPackagesLifetime - TimeSpan.FromSeconds(1));
        using (var stillThere = await _admin.GetAsync($"/api/apps/packages/{packageId}", Token))
        {
            Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
        }
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        using var expired = await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", new { pool = Pool }, Token);
        Assert.Equal("app.package.not_found", await expired.ErrorCodeAsync());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_factory.DataDirectory, "cache", "app-packages")));
    }

    private static TimeSpan AppPackagesLifetime => Pithyx.Daemon.Apps.Packages.AppPackages.Lifetime;

    [Theory]
    [InlineData("org.pithyx.files", "app.manifest.id_reserved")]
    [InlineData("org.pithyx.store", "app.manifest.id_reserved")]
    public async Task SideloadsUnderTheReservedNamespaceAreRefused(string id, string code)
    {
        await _admin.SetupAdminAsync();
        using var response = await UploadAsync(Package(id: id));
        Assert.Equal(code, await response.ErrorCodeAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_factory.DataDirectory, "cache", "app-packages")));
    }

    [Fact]
    public async Task APackageWithABackendIsCheckedAndInstalls()
    {
        await _admin.SetupAdminAsync();
        using var response = await UploadAsync(TarBuilder.BackendPackage(AppId).End().Gzip());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.JsonAsync();
        Assert.Equal("web", preview.GetProperty("manifest").GetProperty("backend").GetProperty("endpoints").GetProperty("api").GetProperty("service").GetString());
        // In simulation nothing runs: the install and an update succeed, and the backend never becomes reachable.
        await InstallAsync(TarBuilder.BackendPackage(AppId).End().Gzip());
        await InstallAsync(TarBuilder.BackendPackage(AppId, "1.1.0").End().Gzip());
        Assert.True(File.Exists(Path.Combine(AppFolder, "versions", "1.1.0", "backend", "images.lock.json")));
        Assert.False(_factory.Services.GetRequiredService<Pithyx.Daemon.Apps.Proxy.AppBackends>().IsRunning(AppId));

        // A lock without this box's architecture cannot run here.
        var architecture = Pithyx.Daemon.Apps.Docker.ImagesLock.HostArchitecture;
        var other = architecture == "amd64" ? "arm64" : "amd64";
        var oneArchitecture = $$"""
            { "lockVersion": 1, "images": { "{{TarBuilder.BackendImage}}": { "{{other}}": { "manifest": "sha256:{{new string('b', 64)}}", "config": "sha256:{{new string('c', 64)}}" } } } }
            """;
        using var refused = await UploadAsync(TarBuilder.BackendPackage("com.example.other", lockJson: oneArchitecture).End().Gzip());
        var problem = await refused.JsonAsync();
        Assert.Equal("app.package.lock_invalid", problem.GetProperty("code").GetString());
        Assert.Equal("platform", problem.GetProperty("params").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task PackagesThatFailTheirChecksAreRefused()
    {
        await _admin.SetupAdminAsync();
        var backend = TarBuilder.Manifest(extra: ",\n  \"backend\": { \"compose\": \"backend/compose.yaml\", \"endpoints\": { \"api\": { \"service\": \"web\", \"port\": 8080 } }, \"lifecycle\": \"onDemand\" }");
        var cases = new (byte[] Package, string Code)[]
        {
            (TarBuilder.MinimalPackage(manifest: backend).File("backend/compose.yaml", "services: {}").End().Gzip(), "app.compose.value_invalid"),
            (TarBuilder.MinimalPackage(manifest: backend).End().Gzip(), "app.package.layout_invalid"),
            (TarBuilder.BackendPackage(withLock: false).End().Gzip(), "app.package.lock_invalid"),
            (TarBuilder.BackendPackage(lockJson: "{ \"lockVersion\": 2, \"images\": {} }").End().Gzip(), "app.package.lock_invalid"),
            (TarBuilder.BackendPackage(lockJson: TarBuilder.BackendLock("ghcr.io/example/other:1@sha256:" + new string('f', 64))).End().Gzip(), "app.package.lock_invalid"),
            (new TarBuilder().File("pithyx.json", TarBuilder.Manifest()).File("icon.svg", TarBuilder.Icon).End().Gzip(), "app.package.layout_invalid"),
            (TarBuilder.MinimalPackage().File("docs/readme.txt", "x").End().Gzip(), "app.package.layout_invalid"),
            (TarBuilder.MinimalPackage().File("frontend/api/data.json", "{}").End().Gzip(), "app.package.layout_invalid"),
            (new TarBuilder().File("pithyx.json", TarBuilder.Manifest()).File("icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"><script/></svg>").File("frontend/index.html", "x").End().Gzip(), "app.icon.invalid"),
            (new TarBuilder().File("pithyx.json", "{ not json").File("icon.svg", TarBuilder.Icon).File("frontend/index.html", "x").End().Gzip(), "app.manifest.invalid_json"),
            ("not a package"u8.ToArray(), "app.package.invalid"),
        };
        foreach (var (package, code) in cases)
        {
            using var response = await UploadAsync(package);
            Assert.Equal(code, await response.ErrorCodeAsync());
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_factory.DataDirectory, "cache", "app-packages")));
    }

    [Fact]
    public async Task EveryFrontendAnswerIsSandboxedAndNothingOutsideTheFrontendIsServed()
    {
        await _admin.SetupAdminAsync();
        await InstallAsync(Package());
        await File.WriteAllTextAsync(Path.Combine(AppFolder, "secret.txt"), "secret", Token);
        await File.WriteAllTextAsync(Path.Combine(AppFolder, "versions", "1.0.0", "pithyx.json.bak"), "x", Token);
        File.CreateSymbolicLink(Path.Combine(AppFolder, "versions", "1.0.0", "frontend", "link.txt"), "/etc/hostname");

        using var anonymous = _factory.CreateClient();
        var answers = new Dictionary<string, HttpStatusCode>
        {
            [$"/apps/{AppId}/index.html"] = HttpStatusCode.OK,
            [$"/apps/{AppId}/style.css"] = HttpStatusCode.OK,
            [$"/apps/{AppId}/"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}"] = HttpStatusCode.NotFound,
            ["/apps/"] = HttpStatusCode.NotFound,
            ["/apps"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}/missing.js"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}/link.txt"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}/../secret.txt"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}/..%2f..%2fsecret.txt"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}/%252e%252e/%252e%252e/secret.txt"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}/..%5c..%5csecret.txt"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}/%2e%2e%2fpithyx.json.bak"] = HttpStatusCode.NotFound,
            [$"/apps/{AppId}/INDEX.html"] = HttpStatusCode.NotFound,
            // The backend's URLs belong to the proxy, which wants a token.
            [$"/apps/{AppId}/api/x"] = HttpStatusCode.Unauthorized,
            [$"/apps/{AppId}/api-metrics/x"] = HttpStatusCode.Unauthorized,
            ["/apps/com.example.other/index.html"] = HttpStatusCode.NotFound,
            ["/apps/..%2f..%2fetc/passwd"] = HttpStatusCode.NotFound,
        };
        foreach (var (path, status) in answers)
        {
            using var response = await anonymous.GetAsync(path, Token);
            Assert.True(status == response.StatusCode, $"{path}: {response.StatusCode}");
            AssertSandboxed(response);
            Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        // Kestrel decodes and removes dot segments first; a path that leaves /apps/ that way is no app file at all.
        using (var escaped = await anonymous.GetAsync($"/apps/{AppId}/%2e%2e/%2e%2e/secret.txt", Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, escaped.StatusCode);
            Assert.DoesNotContain("secret", await escaped.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        // Content types come from the name; the SVG with a script is still sandboxed and never sniffed.
        using (var svg = await anonymous.GetAsync($"/apps/{AppId}/data.svg", Token))
        {
            Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType!.MediaType);
            AssertSandboxed(svg);
        }
        using (var unknown = await anonymous.GetAsync($"/apps/{AppId}/blob", Token))
        {
            Assert.Equal("application/octet-stream", unknown.Content.Headers.ContentType!.MediaType);
        }
        using (var post = await anonymous.PostAsync($"/apps/{AppId}/index.html", new StringContent("x"), Token))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            AssertSandboxed(post);
        }

        // A disabled app serves nothing.
        using (var disable = await _admin.PatchAsJsonAsync($"/api/apps/{AppId}", new { enabled = false }, Token))
        {
            Assert.False((await disable.JsonAsync()).GetProperty("enabled").GetBoolean());
        }
        using var disabled = await anonymous.GetAsync($"/apps/{AppId}/index.html", Token);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        AssertSandboxed(disabled);
    }

    [Fact]
    public async Task TextFilesGoOutCompressedAndOnlyHashedAssetsAreCachedForAYear()
    {
        await _admin.SetupAdminAsync();
        var script = string.Concat(Enumerable.Repeat("console.log('a long line of the bundle');\n", 200));
        await InstallAsync(TarBuilder.MinimalPackage(AppId, "1.0.0", TarBuilder.Manifest(AppId, "1.0.0", "\"notifications\""))
            .File("frontend/assets/index-B4kz_x9Q.js", script)
            .File("frontend/plain.js", script)
            .End().Gzip());
        Assert.True(File.Exists(Path.Combine(AppFolder, "versions", "1.0.0", ".encoded", "assets", "index-B4kz_x9Q.js.br")));

        using var anonymous = _factory.CreateClient();
        async Task<HttpResponseMessage> GetAsync(string path, string? acceptEncoding)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/apps/{AppId}/{path}");
            if (acceptEncoding is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
            }
            return await anonymous.SendAsync(request, Token);
        }

        using (var brotli = await GetAsync("assets/index-B4kz_x9Q.js", "gzip, deflate, br"))
        {
            Assert.Equal(["br"], brotli.Content.Headers.ContentEncoding);
            Assert.Equal("public, max-age=31536000, immutable", brotli.Headers.CacheControl!.ToString());
            Assert.Equal("text/javascript; charset=utf-8", brotli.Content.Headers.ContentType!.ToString());
            AssertSandboxed(brotli);
            var bytes = await brotli.Content.ReadAsByteArrayAsync(Token);
            Assert.True(bytes.Length < script.Length / 5, $"{bytes.Length} bytes");
            await using var decoded = new System.IO.Compression.BrotliStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(decoded);
            Assert.Equal(script, await reader.ReadToEndAsync(Token));
        }
        using (var gzip = await GetAsync("plain.js", "gzip;q=1, br;q=0"))
        {
            Assert.Equal(["gzip"], gzip.Content.Headers.ContentEncoding);
            Assert.Equal("no-cache", gzip.Headers.CacheControl!.ToString());
            Assert.Contains("Accept-Encoding", gzip.Headers.Vary);
        }
        using (var identity = await GetAsync("plain.js", null))
        {
            Assert.Empty(identity.Content.Headers.ContentEncoding);
            Assert.Equal(script, await identity.Content.ReadAsStringAsync(Token));
        }
        // Too small to be worth it: index.html of the minimal package has no copy.
        using var small = await GetAsync("index.html", "br");
        Assert.Empty(small.Content.Headers.ContentEncoding);
        Assert.Equal("no-cache", small.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task AVersionInstalledWithoutCopiesGetsThemAtItsFirstRequest()
    {
        await _admin.SetupAdminAsync();
        var script = string.Concat(Enumerable.Repeat("console.log('a long line of the bundle');\n", 200));
        await InstallAsync(TarBuilder.MinimalPackage(AppId, "1.0.0", TarBuilder.Manifest(AppId, "1.0.0", "\"notifications\""))
            .File("frontend/plain.js", script)
            .End().Gzip());
        var encoded = Path.Combine(AppFolder, "versions", "1.0.0", ".encoded");
        Directory.Delete(encoded, recursive: true);

        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/apps/{AppId}/plain.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
        using (var first = await anonymous.SendAsync(request, Token))
        {
            Assert.Empty(first.Content.Headers.ContentEncoding);
            Assert.Equal(script, await first.Content.ReadAsStringAsync(Token));
        }
        await JobTestSupport.WaitUntilAsync(() => Task.FromResult(File.Exists(Path.Combine(encoded, "plain.js.br"))), "the copies");
    }

    [Fact]
    public async Task OnlyAnAppWithNetworkInternetMayConnectOutside()
    {
        await _admin.SetupAdminAsync();
        await InstallAsync(Package(permissions: "\"network.internet\""));
        using var page = await _admin.GetAsync($"/apps/{AppId}/index.html", Token);
        Assert.EndsWith("connect-src 'self' https:", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppsReachOnlyTheUsersWithTheirGrantAndIconsComeSandboxed()
    {
        await _admin.SetupAdminAsync();
        await InstallAsync(Package(), new { pool = Pool, access = "admins" });
        var rita = await _factory.InsertUserAsync("rita");
        using var ritaClient = _factory.ClientWithCookie(await _factory.SessionCookieAsync(rita));

        using (var refused = await ritaClient.GetAsync($"/api/apps/{AppId}/icon", Token))
        {
            Assert.Equal("app.app.not_found", await refused.ErrorCodeAsync());
        }
        using (var icon = await _admin.GetAsync($"/api/apps/{AppId}/icon", Token))
        {
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
            Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType!.MediaType);
            Assert.Equal("default-src 'none'; sandbox", icon.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", icon.Headers.GetValues("X-Content-Type-Options").Single());
        }
        var registry = _factory.Services.GetRequiredService<AppRegistry>();
        var permissions = _factory.Services.GetRequiredService<Pithyx.Daemon.Permissions.PermissionService>();
        Assert.False(registry.Allows(await permissions.ForUserAsync(rita, Token), AppId));

        await using (var connection = await _factory.Services.GetRequiredService<Pithyx.Daemon.Database.SqliteConnectionFactory>().OpenAsync(Token))
        {
            await TestUsers.GrantAsync(connection, rita, "app:" + AppId);
        }
        permissions.Invalidate();
        Assert.True(registry.Allows(await permissions.ForUserAsync(rita, Token), AppId));
        using (var icon = await ritaClient.GetAsync($"/api/apps/{AppId}/icon", Token))
        {
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        }
    }

    [Fact]
    public async Task AThirdPartyAppIsGivenOnlyByAGrantAndOnlyWhileInstalled()
    {
        await _admin.SetupAdminAsync();
        var rita = await _factory.InsertUserAsync("rita");
        object Grants(params string[] resources) => new { grants = resources.Select(resource => new { resource, level = "allow" }) };

        // Not installed yet: no grant names it, and no token is given for it.
        using (var early = await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", Grants("right:desktopLogin", "app:" + AppId), Token))
        {
            Assert.Equal("permissions.grant.invalid", await early.ErrorCodeAsync());
        }
        await InstallAsync(Package(), new { pool = Pool, access = "admins" });
        using var ritaClient = _factory.ClientWithCookie(await _factory.SessionCookieAsync(rita));
        using (var refused = await ritaClient.PostAsJsonAsync($"/api/apps/{AppId}/tokens", new { kind = "host", instance = "w1" }, Token))
        {
            Assert.Equal("app.app.not_found", await refused.ErrorCodeAsync());
        }

        using (var granted = await _admin.PutAsJsonAsync($"/api/users/{rita}/grants", Grants("right:desktopLogin", "app:" + AppId), Token))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
            Assert.Contains(AppId, (await granted.JsonAsync()).GetProperty("effective").GetProperty("apps").EnumerateArray().Select(app => app.GetString()));
        }
        using (var allowed = await ritaClient.PostAsJsonAsync($"/api/apps/{AppId}/tokens", new { kind = "host", instance = "w1" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        // Removing the app takes its grants along.
        using (var remove = await _admin.DeleteAsync($"/api/apps/{AppId}?data=delete", Token))
        {
            await FinishedAsync(await remove.JsonAsync());
        }
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM grants WHERE resource = 'app:' || @AppId", new { AppId }));
    }

    [Fact]
    public async Task ThePoolIsChosenOnceAndABusyAppTakesNoSecondJob()
    {
        await _admin.SetupAdminAsync();
        using (var missing = await _admin.PostAsJsonAsync($"/api/apps/packages/{await CheckedAsync(Package())}/install", new { pool = "nopool" }, Token))
        {
            Assert.Equal("app.pool.unavailable", await missing.ErrorCodeAsync());
        }
        await InstallAsync(Package());
        using (var other = await _admin.PostAsJsonAsync($"/api/apps/packages/{await CheckedAsync(Package(id: "com.example.second"))}/install", new { pool = "pithyxtests" }, Token))
        {
            Assert.Equal("app.pool.fixed", await other.ErrorCodeAsync());
        }

        var registry = _factory.Services.GetRequiredService<AppRegistry>();
        Assert.True(registry.TryBegin(AppId));
        using (var busy = await _admin.DeleteAsync($"/api/apps/{AppId}?data=keep", Token))
        {
            Assert.Equal("app.app.busy", await busy.ErrorCodeAsync());
        }
        registry.End(AppId);
        using var unknown = await _admin.DeleteAsync("/api/apps/com.example.unknown?data=keep", Token);
        Assert.Equal("app.app.not_found", await unknown.ErrorCodeAsync());
    }

    private static void AssertSandboxed(HttpResponseMessage response)
    {
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("sandbox allow-scripts allow-forms; default-src 'self';", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
