using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps.Catalog;

/// <summary>
/// A catalog repository on the local disk, written as a catalog's CI writes its branch <c>catalog</c>: the package,
/// manifest and icon per version and an unsigned <c>index.json</c> with a growing sequence.
/// </summary>
internal sealed class LocalCatalog
{
    public LocalCatalog(string folder, string catalogId)
    {
        Folder = folder;
        CatalogId = catalogId;
        Directory.CreateDirectory(folder);
        Git("init", "--quiet", "--initial-branch=catalog");
        File.WriteAllText(Path.Combine(folder, "catalog.json"), $$"""{ "catalogVersion": 1, "id": "{{catalogId}}", "name": { "en": "Test catalog" }, "images": "ghcr.io/example/apps" }""");
    }

    public string Folder { get; }

    public string CatalogId { get; }

    public string Url => "file://" + Folder;

    private readonly JsonObject _apps = [];

    public long Sequence { get; private set; }

    /// <summary>Adds a version and commits a new index; <paramref name="extra"/> goes into the manifest.</summary>
    public void Publish(string appId, string version, string extra = "", string pithyx = "*")
    {
        var manifest = TarBuilder.Manifest(appId, version, "\"notifications\"", extra);
        var package = TarBuilder.MinimalPackage(appId, version, manifest).End().Gzip();
        var folder = Path.Combine(Folder, "apps", appId, version);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"{appId}-{version}.pithyxapp"), package);
        File.WriteAllText(Path.Combine(folder, "pithyx.json"), manifest);
        File.WriteAllText(Path.Combine(folder, "icon.svg"), TarBuilder.Icon);
        var entry = new JsonObject
        {
            ["publishedAt"] = "2026-10-08T20:00:00Z",
            ["package"] = FileEntry(package),
            ["manifest"] = FileEntry(Encoding.UTF8.GetBytes(manifest)),
            ["icon"] = FileEntry(Encoding.UTF8.GetBytes(TarBuilder.Icon)),
            ["architectures"] = new JsonArray("amd64", "arm64"),
            ["pithyx"] = pithyx,
            ["license"] = "MIT",
            ["source"] = new JsonObject { ["repository"] = "https://example.com/apps", ["commit"] = new string('a', 40), ["path"] = $"apps/{appId}" },
        };
        if (_apps[appId] is not JsonObject app)
        {
            app = new JsonObject { ["versions"] = new JsonObject() };
            _apps[appId] = app;
        }
        ((JsonObject)app["versions"]!)[version] = entry;
        WriteIndex(++Sequence);
    }

    /// <summary>Writes and commits an index with the given sequence.</summary>
    public void WriteIndex(long sequence)
    {
        var index = new JsonObject
        {
            ["indexVersion"] = 1,
            ["catalog"] = CatalogId,
            ["channel"] = "stable",
            ["sequence"] = sequence,
            ["publishedAt"] = "2026-10-08T20:00:00Z",
            ["apps"] = _apps.DeepClone(),
        };
        File.WriteAllText(Path.Combine(Folder, "index.json"), index.ToJsonString());
        Commit();
    }

    public void Commit()
    {
        Git("add", "--all");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "--allow-empty", "-m", "publish");
    }

    private static JsonObject FileEntry(byte[] bytes) =>
        new() { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["size"] = bytes.Length };

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(Folder);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}

/// <summary>
/// The Store through the API in simulation, against catalogs in local git repositories: adding a catalog, its apps,
/// installing from it, updates by policy, the rollback, and fetches that fail without losing the verified copy.
/// </summary>
public sealed class StoreApiTests : IDisposable
{
    private const string AppId = "com.example.notes";
    private const string Pool = "pithyxtestm";
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly LocalCatalog _catalog;

    public StoreApiTests()
    {
        _admin = _factory.CreateApiClient();
        _catalog = new LocalCatalog(Path.Combine(_factory.DataDirectory, "remote-catalog"), "com.example.test");
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private JobService Jobs => _factory.Services.GetRequiredService<JobService>();

    private async Task<Job> FinishedAsync(JsonElement started, JobState expected = JobState.Succeeded)
    {
        var id = started.GetProperty("id").GetInt64();
        Job? job = null;
        await JobTestSupport.WaitUntilAsync(async () => (job = await Jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State is not (JobState.Queued or JobState.Running), $"job {id}");
        Assert.True(job!.State == expected, $"{job.Kind} ended {job.State}: {job.ErrorCode} {JsonSerializer.Serialize(job.ErrorParams)}");
        return job;
    }

    private async Task<JsonElement> StoreAsync() => await _admin.GetFromJsonAsync<JsonElement>("/api/store", Token);

    private async Task<JsonElement> CatalogAsync(string url) =>
        (await StoreAsync()).GetProperty("catalogs").EnumerateArray().Single(catalog => catalog.GetProperty("url").GetString() == url);

    private async Task<JsonElement> AppAsync() =>
        (await _admin.GetFromJsonAsync<JsonElement>("/api/apps", Token)).GetProperty("apps").EnumerateArray().Single(app => app.GetProperty("id").GetString() == AppId);

    private async Task<long> AddCatalogAsync()
    {
        using var added = await _admin.PostAsJsonAsync("/api/store/catalogs", new { url = _catalog.Url }, Token);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var key = (await added.JsonAsync()).GetProperty("key").GetInt64();
        await JobTestSupport.WaitUntilAsync(async () => (await CatalogAsync(_catalog.Url)).GetProperty("status").GetString() is "ok" or "failed", "the first fetch");
        return key;
    }

    private async Task CheckAsync()
    {
        using var check = await _admin.PostAsync("/api/store/check", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, check.StatusCode);
        await FinishedAsync(await check.JsonAsync());
    }

    private async Task InstallAsync(string version)
    {
        using var staged = await _admin.PostAsync($"/api/store/catalogs/{_catalog.CatalogId}/apps/{AppId}/{version}/package", null, Token);
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var preview = await staged.JsonAsync();
        Assert.Equal(_catalog.CatalogId, preview.GetProperty("catalog").GetString());
        using var install = await _admin.PostAsJsonAsync($"/api/apps/packages/{preview.GetProperty("packageId").GetString()}/install", new { pool = Pool }, Token);
        await FinishedAsync(await install.JsonAsync());
    }

    [Fact]
    public async Task ACustomCatalogIsFetchedListedInstalledUpdatedAndRolledBack()
    {
        await _admin.SetupAdminAsync();
        _catalog.Publish(AppId, "1.0.0", ",\n  \"dataVersion\": 1");
        await AddCatalogAsync();
        var catalog = await CatalogAsync(_catalog.Url);
        Assert.Equal("ok", catalog.GetProperty("status").GetString());
        Assert.Equal(_catalog.CatalogId, catalog.GetProperty("id").GetString());
        Assert.Equal("custom", catalog.GetProperty("kind").GetString());
        Assert.False(catalog.GetProperty("verified").GetBoolean());
        Assert.Equal(1, catalog.GetProperty("appCount").GetInt32());
        Assert.True(File.Exists(Path.Combine(_factory.DataDirectory, "catalogs", _catalog.CatalogId, "stable", "index.json")));

        var apps = await _admin.GetFromJsonAsync<JsonElement>("/api/store/apps?q=prob", Token);
        var listed = apps.GetProperty("apps").EnumerateArray().Single();
        Assert.Equal("1.0.0", listed.GetProperty("version").GetString());
        Assert.Empty((await _admin.GetFromJsonAsync<JsonElement>("/api/store/apps?q=nothing-like-it", Token)).GetProperty("apps").EnumerateArray());

        using (var icon = await _admin.GetAsync(listed.GetProperty("icon").GetString(), Token))
        {
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
            Assert.Equal("default-src 'none'; sandbox", icon.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", icon.Headers.GetValues("X-Content-Type-Options").Single());
        }
        var details = await _admin.GetFromJsonAsync<JsonElement>($"/api/store/catalogs/{_catalog.CatalogId}/apps/{AppId}", Token);
        Assert.Equal($"https://example.com/apps/tree/{new string('a', 40)}/apps/{AppId}", details.GetProperty("source").GetString());

        await InstallAsync("1.0.0");
        var app = await AppAsync();
        Assert.Equal("catalog", app.GetProperty("source").GetString());
        Assert.Equal(_catalog.CatalogId, app.GetProperty("catalog").GetString());
        await File.WriteAllTextAsync(Path.Combine(_factory.DataDirectory, "pools", Pool, ".pithyx", "apps", AppId, "volumes", "data.txt"), "one", Token);

        // A newer version: announced under the default policy, installed by the box under the automatic one.
        _catalog.Publish(AppId, "1.1.0", ",\n  \"dataVersion\": 1");
        await CheckAsync();
        var update = (await StoreAsync()).GetProperty("updates").EnumerateArray().Single();
        Assert.Equal("1.1.0", update.GetProperty("version").GetString());
        Assert.False(update.GetProperty("automatic").GetBoolean());
        using (var settings = await _admin.PutAsJsonAsync("/api/store/settings", new { policy = "automatic", time = "03:00", channel = "stable" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        }
        await CheckAsync();
        await JobTestSupport.WaitUntilAsync(async () => (await AppAsync()).GetProperty("version").GetString() == "1.1.0" && !(await AppAsync()).GetProperty("busy").GetBoolean(), "the automatic update");
        app = await AppAsync();
        Assert.Equal("1.0.0", app.GetProperty("previousVersion").GetString());
        Assert.Empty((await StoreAsync()).GetProperty("updates").EnumerateArray());

        using (var rollback = await _admin.PostAsync($"/api/apps/{AppId}/rollback", null, Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, rollback.StatusCode);
            await FinishedAsync(await rollback.JsonAsync());
        }
        app = await AppAsync();
        Assert.Equal("1.0.0", app.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, app.GetProperty("previousVersion").ValueKind);
        // Under the automatic policy the next check would bring 1.1.0 back: the app announces updates from now on.
        Assert.Equal("notify", app.GetProperty("updatePolicy").GetString());
        await CheckAsync();
        Assert.Equal("1.0.0", (await AppAsync()).GetProperty("version").GetString());
        using (var again = await _admin.PostAsync($"/api/apps/{AppId}/rollback", null, Token))
        {
            Assert.Equal("app.app.no_previous", await again.ErrorCodeAsync());
        }

        // The app's own policy wins over the box's: nothing is installed for it now.
        using (var policy = await _admin.PutAsJsonAsync($"/api/apps/{AppId}/update-policy", new { policy = "none" }, Token))
        {
            Assert.Equal("none", (await policy.JsonAsync()).GetProperty("updatePolicy").GetString());
        }
        await CheckAsync();
        Assert.Equal("1.0.0", (await AppAsync()).GetProperty("version").GetString());
        Assert.False((await StoreAsync()).GetProperty("updates").EnumerateArray().Single().GetProperty("automatic").GetBoolean());
    }

    [Fact]
    public async Task AFailedFetchKeepsTheVerifiedCopyAndSaysWhy()
    {
        await _admin.SetupAdminAsync();
        _catalog.Publish(AppId, "1.0.0");
        var key = await AddCatalogAsync();

        // A changed file that the index does not name.
        var package = Path.Combine(_catalog.Folder, "apps", AppId, "1.0.0", $"{AppId}-1.0.0.pithyxapp");
        await File.AppendAllTextAsync(package, "x", Token);
        _catalog.Commit();
        using (var sync = await _admin.PostAsync($"/api/store/catalogs/{key}/sync", null, Token))
        {
            var job = await FinishedAsync(await sync.JsonAsync(), JobState.Failed);
            Assert.Equal("store.package.hash_mismatch", job.ErrorCode);
        }
        var catalog = await CatalogAsync(_catalog.Url);
        Assert.Equal("failed", catalog.GetProperty("status").GetString());
        Assert.Equal("store.package.hash_mismatch", catalog.GetProperty("errorCode").GetString());
        Assert.Single((await _admin.GetFromJsonAsync<JsonElement>("/api/store/apps", Token)).GetProperty("apps").EnumerateArray());

        // Fixed, and a new version: the copy follows. An older index after it is a replay.
        File.Copy(Path.Combine(_factory.DataDirectory, "catalogs", _catalog.CatalogId, "stable", "apps", AppId, "1.0.0", $"{AppId}-1.0.0.pithyxapp"), package, overwrite: true);
        _catalog.Publish(AppId, "1.0.1");
        using (var sync = await _admin.PostAsync($"/api/store/catalogs/{key}/sync", null, Token))
        {
            await FinishedAsync(await sync.JsonAsync());
        }
        Assert.Equal(2, (await CatalogAsync(_catalog.Url)).GetProperty("sequence").GetInt64());
        _catalog.WriteIndex(1);
        using (var sync = await _admin.PostAsync($"/api/store/catalogs/{key}/sync", null, Token))
        {
            Assert.Equal("store.catalog.sequence_old", (await FinishedAsync(await sync.JsonAsync(), JobState.Failed)).ErrorCode);
        }
        Assert.Equal("1.0.1", (await _admin.GetFromJsonAsync<JsonElement>("/api/store/apps", Token)).GetProperty("apps").EnumerateArray().Single().GetProperty("version").GetString());

        // A missing repository.
        Directory.Move(_catalog.Folder, _catalog.Folder + "-renamed");
        using (var sync = await _admin.PostAsync($"/api/store/catalogs/{key}/sync", null, Token))
        {
            var job = await FinishedAsync(await sync.JsonAsync(), JobState.Failed);
            Assert.Equal("store.catalog.unreachable", job.ErrorCode);
        }
        Assert.Single((await _admin.GetFromJsonAsync<JsonElement>("/api/store/apps", Token)).GetProperty("apps").EnumerateArray());
    }

    [Fact]
    public async Task TheProjectsCatalogsNeedASignatureByABuiltInKey()
    {
        await _admin.SetupAdminAsync();
        var official = new LocalCatalog(Path.Combine(_factory.DataDirectory, "official"), "org.pithyx.official");
        official.Publish(AppId, "1.0.0");
        await _factory.ScalarAsync<long>("UPDATE catalogs SET url = @url WHERE kind = 'official' RETURNING id", new { url = official.Url });
        var key = await _factory.ScalarAsync<long>("SELECT id FROM catalogs WHERE kind = 'official'");
        using (var sync = await _admin.PostAsync($"/api/store/catalogs/{key}/sync", null, Token))
        {
            var job = await FinishedAsync(await sync.JsonAsync(), JobState.Failed);
            Assert.Equal("store.catalog.signature_invalid", job.ErrorCode);
        }
        Assert.Empty((await _admin.GetFromJsonAsync<JsonElement>("/api/store/apps", Token)).GetProperty("apps").EnumerateArray());
        var community = (await _admin.GetFromJsonAsync<JsonElement>("/api/store", Token)).GetProperty("catalogs").EnumerateArray().Single(catalog => catalog.GetProperty("kind").GetString() == "community");
        // Off and never fetched, it still has its name.
        Assert.Equal("Community", community.GetProperty("name").GetProperty("en").GetString());
        using var remove = await _admin.DeleteAsync($"/api/store/catalogs/{key}", Token);
        Assert.Equal("store.catalog.built_in", await remove.ErrorCodeAsync());
    }

    [Fact]
    public async Task AVersionForANewerBoxIsNotOffered()
    {
        await _admin.SetupAdminAsync();
        _catalog.Publish(AppId, "1.0.0");
        _catalog.Publish(AppId, "2.0.0", pithyx: ">=99.0.0");
        await AddCatalogAsync();
        Assert.Equal("1.0.0", (await _admin.GetFromJsonAsync<JsonElement>("/api/store/apps", Token)).GetProperty("apps").EnumerateArray().Single().GetProperty("version").GetString());
        using var staged = await _admin.PostAsync($"/api/store/catalogs/{_catalog.CatalogId}/apps/{AppId}/2.0.0/package", null, Token);
        Assert.Equal("app.package.box_too_old", await staged.ErrorCodeAsync());
    }

    [Fact]
    public async Task OnlyAdministratorsUseTheStore()
    {
        await _admin.SetupAdminAsync();
        var id = await _factory.InsertUserAsync("someone");
        using var user = _factory.ClientWithCookie(await _factory.SessionCookieAsync(id));
        using var response = await user.GetAsync("/api/store", Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
