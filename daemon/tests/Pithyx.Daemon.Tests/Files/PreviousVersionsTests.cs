using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Files;

/// <summary>
/// "Previous versions" against a folder that imitates a mounted dataset with <c>.zfs/snapshot</c>. The dataset is the
/// simulated <c>pithyxtestm</c>, moved to that folder, so its snapshots are the ones of the simulation fixture.
/// </summary>
public sealed class PreviousVersionsTests : IAsyncLifetime
{
    private const string Dataset = "pithyxtestm";
    private const string Manual = "pithyx-manual-20260920T101500Z";
    private const string Held = "before-upgrade";
    private const string Daily1 = "pithyx-daily-20260926T000000Z";
    private const string Daily2 = "pithyx-daily-20260927T000000Z";
    private static readonly DateTimeOffset _old = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _newer = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly string _mount;
    private readonly string _share;
    private long _shareId;

    public PreviousVersionsTests()
    {
        _admin = _factory.CreateApiClient();
        _mount = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool")).FullName;
        _share = Directory.CreateDirectory(Path.Combine(_mount, "shares", "docs")).FullName;
    }

    public async ValueTask InitializeAsync()
    {
        await _admin.SetupAdminAsync();
        using var created = await _admin.PostAsync("/api/shares", new { name = "Docs", path = _share });
        _shareId = (await created.JsonAsync()).GetProperty("id").GetInt64();
        var pools = _factory.Services.GetRequiredService<PoolInventory>();
        pools.Replace(new PoolList([], [MountedAt(Dataset, _mount)]));
        await WaitForAsync(() => _factory.Services.GetRequiredService<SnapshotInventory>().Of(Dataset).Count == 4);
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    private static Dataset MountedAt(string name, string? mountPoint, bool mounted = true) =>
        new(name, name.Split('/')[0], "filesystem", 0, 0, 0, mountPoint, mounted, "off", null, null, null, null, "lz4", 131072, null, null, 0);

    /// <summary>Writes a file into a snapshot of the dataset, below the share's folder.</summary>
    private void InSnapshot(string snapshot, string path, string content, DateTimeOffset modified)
    {
        var file = Path.Combine(_mount, ".zfs", "snapshot", snapshot, "shares", "docs", path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        File.SetLastWriteTimeUtc(file, modified.UtcDateTime);
    }

    private string Current(string path) => Path.Combine(_share, path);

    private async Task<JsonElement> VersionsAsync(string path, HttpClient? client = null)
    {
        using var response = await (client ?? _admin).GetAsync($"/api/files/{_shareId}/versions?path={Uri.EscapeDataString(path)}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static string[] Snapshots(JsonElement list) =>
        [.. list.GetProperty("versions").EnumerateArray().Select(version => version.GetProperty("snapshot").GetString()!)];

    private async Task<JsonElement> RestoreAsync(object body)
    {
        using var started = await _admin.PostAsync($"/api/files/{_shareId}/versions/restore", body);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.JsonAsync()).GetProperty("id").GetInt64();
        JsonElement job = default;
        await JobTestSupport.WaitUntilAsync(async () =>
        {
            using var response = await _admin.GetAsync($"/api/jobs/{id}", Token);
            job = await response.JsonAsync();
            return job.GetProperty("state").GetString() is not ("queued" or "running");
        }, $"job {id}");
        Assert.Equal("succeeded", job.GetProperty("state").GetString());
        return job;
    }

    private async Task<HttpClient> UserAsync(string username, string? access)
    {
        var hash = await _factory.Services.GetRequiredService<PasswordHasher>().HashAsync(AdminPassword, Token);
        long id;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            id = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES (@username, @hash, 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z') RETURNING id",
                new { username, hash });
        }
        if (access is not null)
        {
            using var granted = await _admin.PutAsync($"/api/shares/{_shareId}/access/{id}", System.Net.Http.Json.JsonContent.Create(new { access }), Token);
            Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        }
        var client = _factory.CreateApiClient();
        (await client.LoginAsync(username, AdminPassword)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task EachDistinctVersionIsListedOnceNewestFirstWithoutTheCurrentOne()
    {
        InSnapshot(Manual, "notes/plan.txt", "one", _old);
        InSnapshot(Held, "notes/plan.txt", "one", _old);
        InSnapshot(Daily1, "notes/plan.txt", "two!", _newer);
        Directory.CreateDirectory(Path.Combine(_mount, ".zfs", "snapshot", Daily2));
        Directory.CreateDirectory(Current("notes"));
        File.WriteAllText(Current("notes/plan.txt"), "three");

        var list = await VersionsAsync("notes/plan.txt");

        Assert.Equal([Daily1, Held], Snapshots(list));
        var newest = list.GetProperty("versions")[0];
        Assert.Equal(("file", 4L), (newest.GetProperty("kind").GetString(), newest.GetProperty("size").GetInt64()));
        Assert.Equal(_newer, newest.GetProperty("modified").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero), newest.GetProperty("createdAt").GetDateTimeOffset());

        // While the current file equals a version, that version is not offered.
        File.WriteAllText(Current("notes/plan.txt"), "two!");
        File.SetLastWriteTimeUtc(Current("notes/plan.txt"), _newer.UtcDateTime);
        Assert.Equal([Held], Snapshots(await VersionsAsync("notes/plan.txt")));
    }

    [Fact]
    public async Task DeletedFilesAndFoldersHaveVersionsToo()
    {
        InSnapshot(Held, "gone/a.txt", "a", _old);
        InSnapshot(Daily2, "gone/a.txt", "a", _old);

        Assert.Equal([Daily2], Snapshots(await VersionsAsync("gone/a.txt")));
        var folder = await VersionsAsync("gone");
        Assert.Equal([Daily2, Held], Snapshots(folder));
        Assert.Equal("folder", folder.GetProperty("versions")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ASymlinkInASnapshotCannotLeaveTheShare()
    {
        var other = Path.Combine(_mount, ".zfs", "snapshot", Held, "shares", "secret");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "passwords.txt"), "hunter2");
        Directory.CreateDirectory(Path.Combine(_mount, ".zfs", "snapshot", Held, "shares", "docs"));
        File.CreateSymbolicLink(Path.Combine(_mount, ".zfs", "snapshot", Held, "shares", "docs", "escape"), "../secret");

        Assert.Empty(Snapshots(await VersionsAsync("escape/passwords.txt")));
        using var restore = await _admin.PostAsync($"/api/files/{_shareId}/versions/restore", new { path = "escape/passwords.txt", snapshot = Held });
        Assert.Equal("fs.version.not_found", (await restore.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task RestoringPutsTheVersionNextToTheCurrentOne()
    {
        InSnapshot(Held, "report.txt", "old", _old);
        File.WriteAllText(Current("report.txt"), "new");

        await RestoreAsync(new { path = "report.txt", snapshot = Held });
        await RestoreAsync(new { path = "report.txt", snapshot = Held });

        Assert.Equal("new", File.ReadAllText(Current("report.txt")));
        Assert.Equal("old", File.ReadAllText(Current("report (version 2026-09-24 08.30 UTC).txt")));
        Assert.Equal("old", File.ReadAllText(Current("report (version 2026-09-24 08.30 UTC) (2).txt")));
    }

    [Fact]
    public async Task RestoringWithOverwriteReplacesTheFileAndMergesFolders()
    {
        InSnapshot(Held, "project/a.txt", "old a", _old);
        InSnapshot(Held, "project/sub/b.txt", "old b", _old);
        Directory.CreateDirectory(Current("project"));
        File.WriteAllText(Current("project/a.txt"), "new a");
        File.WriteAllText(Current("project/c.txt"), "new c");

        await RestoreAsync(new { path = "project", snapshot = Held, overwrite = true });

        Assert.Equal("old a", File.ReadAllText(Current("project/a.txt")));
        Assert.Equal("old b", File.ReadAllText(Current("project/sub/b.txt")));
        Assert.Equal("new c", File.ReadAllText(Current("project/c.txt")));
    }

    [Fact]
    public async Task RestoringAMissingVersionIsRefused()
    {
        InSnapshot(Held, "report.txt", "old", _old);

        using var unknownSnapshot = await _admin.PostAsync($"/api/files/{_shareId}/versions/restore", new { path = "report.txt", snapshot = "nope" });
        using var notInSnapshot = await _admin.PostAsync($"/api/files/{_shareId}/versions/restore", new { path = "other.txt", snapshot = Held });

        Assert.Equal(HttpStatusCode.NotFound, unknownSnapshot.StatusCode);
        Assert.Equal("fs.version.not_found", (await unknownSnapshot.JsonAsync()).GetProperty("code").GetString());
        Assert.Equal("fs.version.not_found", (await notInSnapshot.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReadersListButOnlyWritersRestore()
    {
        InSnapshot(Held, "report.txt", "old", _old);
        using var reader = await UserAsync("rita", "read");
        using var stranger = await UserAsync("sam", null);

        Assert.Equal([Held], Snapshots(await VersionsAsync("report.txt", reader)));
        using var restore = await reader.PostAsync($"/api/files/{_shareId}/versions/restore", new { path = "report.txt", snapshot = Held });
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);
        Assert.Equal("fs.share.read_only", (await restore.JsonAsync()).GetProperty("code").GetString());
        using var list = await stranger.GetAsync($"/api/files/{_shareId}/versions?path=report.txt", Token);
        Assert.Equal("fs.share.not_found", (await list.JsonAsync()).GetProperty("code").GetString());
        using var anonymous = _factory.CreateApiClient();
        using var unauthenticated = await anonymous.GetAsync($"/api/files/{_shareId}/versions?path=report.txt", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task ASharesNotOnADatasetHasNoVersions()
    {
        InSnapshot(Held, "report.txt", "old", _old);
        _factory.Services.GetRequiredService<PoolInventory>().Replace(new PoolList([], [MountedAt(Dataset, _mount, mounted: false)]));

        Assert.Empty(Snapshots(await VersionsAsync("report.txt")));
    }

    [Fact]
    public void TheDeepestMountedDatasetHoldsAnEntry()
    {
        Dataset[] datasets =
        [
            MountedAt("tank", "/tank"),
            MountedAt("tank/pithyx/shares/media", "/tank/media"),
            MountedAt("tank/pithyx/shares/media/raw", "/tank/media/photos/raw"),
            MountedAt("tank/locked", "/tank/locked", mounted: false),
            MountedAt("root", "/"),
        ];

        Assert.Equal(new PreviousVersions.Location("tank/pithyx/shares/media", "/tank/media", "", "photos/a.jpg"), PreviousVersions.Locate(datasets, "/tank/media", "photos/a.jpg"));
        Assert.Equal(new PreviousVersions.Location("tank/pithyx/shares/media/raw", "/tank/media/photos/raw", "", "b.cr2"), PreviousVersions.Locate(datasets, "/tank/media", "photos/raw/b.cr2"));
        Assert.Equal(new PreviousVersions.Location("tank/pithyx/shares/media", "/tank/media", "photos", "a.jpg"), PreviousVersions.Locate(datasets, "/tank/media/photos", "a.jpg"));
        Assert.Equal(new PreviousVersions.Location("tank", "/tank", "locked", "x"), PreviousVersions.Locate(datasets, "/tank/locked", "x"));
        Assert.Equal(new PreviousVersions.Location("root", "/", "srv/share", "x"), PreviousVersions.Locate(datasets, "/srv/share", "x"));
        Assert.Null(PreviousVersions.Locate(datasets[..3], "/srv/share", "x"));
        Assert.Null(PreviousVersions.Locate(datasets[..3], "/tankx", "x"));
    }

    [Fact]
    public void VersionNamesKeepTheExtension()
    {
        var at = new DateTimeOffset(2026, 9, 27, 23, 5, 0, TimeSpan.FromHours(2));

        Assert.Equal("Report (version 2026-09-27 21.05 UTC).pdf", PreviousVersions.VersionName("Report.pdf", at));
        Assert.Equal("Photos (version 2026-09-27 21.05 UTC)", PreviousVersions.VersionName("Photos", at));
        Assert.Equal(".bashrc (version 2026-09-27 21.05 UTC)", PreviousVersions.VersionName(".bashrc", at));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Timed out.");
            await Task.Delay(10, Token);
        }
    }
}
