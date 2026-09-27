using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vyxis.Daemon.Tests.Jobs;
using Vyxis.Daemon.Tests.Support;
using static Vyxis.Daemon.Tests.Auth.AuthApi;

namespace Vyxis.Daemon.Tests.Files;

public sealed class FileOperationTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly string _media;
    private readonly string _backup;
    private long _mediaId;
    private long _backupId;

    public FileOperationTests()
    {
        _admin = _factory.CreateApiClient();
        _media = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "media")).FullName;
        _backup = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "backup")).FullName;
    }

    public async ValueTask InitializeAsync()
    {
        await _admin.SetupAdminAsync();
        _mediaId = await CreateShareAsync("Media", _media);
        _backupId = await CreateShareAsync("Backup", _backup);
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<long> CreateShareAsync(string name, string path)
    {
        using var response = await _admin.PostAsync("/api/shares", new { name, path });
        return (await response.JsonAsync()).GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> RunAsync(object request, string expectedState = "succeeded")
    {
        using var started = await _admin.PostAsync("/api/files/operations", request);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.JsonAsync()).GetProperty("id").GetInt64();
        JsonElement job = default;
        await JobTestSupport.WaitUntilAsync(async () =>
        {
            using var response = await _admin.GetAsync($"/api/jobs/{id}", Token);
            job = await response.JsonAsync();
            return job.GetProperty("state").GetString() is not ("queued" or "running");
        }, $"job {id}");
        Assert.Equal(expectedState, job.GetProperty("state").GetString());
        return job;
    }

    private void Write(string relative, string content = "x")
    {
        var path = Path.Combine(_media, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task CopiesFoldersAcrossSharesWithProgress()
    {
        Write("Album/a.jpg", "aaaa");
        Write("Album/Sub/b.jpg", "bb");
        var job = await RunAsync(new { kind = "copy", items = new[] { new { shareId = _mediaId, path = "Album" } }, target = new { shareId = _backupId, path = "" } });
        Assert.Equal("files.copy", job.GetProperty("kind").GetString());
        Assert.Equal("aaaa", File.ReadAllText(Path.Combine(_backup, "Album", "a.jpg")));
        Assert.Equal("bb", File.ReadAllText(Path.Combine(_backup, "Album", "Sub", "b.jpg")));
        Assert.True(File.Exists(Path.Combine(_media, "Album", "a.jpg")));
        var progress = job.GetProperty("progress");
        Assert.Equal(4, progress.GetProperty("itemsTotal").GetInt64());
        Assert.Equal(6, progress.GetProperty("bytesTotal").GetInt64());
        Assert.Equal(6, progress.GetProperty("bytesDone").GetInt64());
        Assert.Empty(Directory.EnumerateFiles(_backup, ".vyxis-upload-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MovesByRenamingAndHandlesNameConflicts()
    {
        Write("a.txt", "new");
        Write("Target/a.txt", "old");
        var failed = await RunAsync(new { kind = "move", items = new[] { new { shareId = _mediaId, path = "a.txt" } }, target = new { shareId = _mediaId, path = "Target" } }, "failed");
        Assert.Equal("fs.entry.exists", failed.GetProperty("errorCode").GetString());
        Assert.Equal("new", File.ReadAllText(Path.Combine(_media, "a.txt")));

        await RunAsync(new { kind = "move", items = new[] { new { shareId = _mediaId, path = "a.txt" } }, target = new { shareId = _mediaId, path = "Target" }, onConflict = "rename" });
        Assert.Equal("new", File.ReadAllText(Path.Combine(_media, "Target", "a (2).txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_media, "Target", "a.txt")));
        Assert.False(File.Exists(Path.Combine(_media, "a.txt")));

        Write("a.txt", "newer");
        var skipped = await RunAsync(new { kind = "copy", items = new[] { new { shareId = _mediaId, path = "a.txt" } }, target = new { shareId = _mediaId, path = "Target" }, onConflict = "skip" });
        Assert.Equal(["a.txt"], skipped.GetProperty("result").GetProperty("skipped").EnumerateArray().Select(e => e.GetString()));
        await RunAsync(new { kind = "copy", items = new[] { new { shareId = _mediaId, path = "a.txt" } }, target = new { shareId = _mediaId, path = "Target" }, onConflict = "overwrite" });
        Assert.Equal("newer", File.ReadAllText(Path.Combine(_media, "Target", "a.txt")));
    }

    [Fact]
    public async Task MovesAcrossSharesAndMergesFoldersOnOverwrite()
    {
        Write("Photos/1.jpg", "one");
        Write("Photos/2.jpg", "two");
        Directory.CreateDirectory(Path.Combine(_backup, "Photos"));
        File.WriteAllText(Path.Combine(_backup, "Photos", "1.jpg"), "stale");
        File.WriteAllText(Path.Combine(_backup, "Photos", "keep.jpg"), "keep");
        await RunAsync(new { kind = "move", items = new[] { new { shareId = _mediaId, path = "Photos" } }, target = new { shareId = _backupId, path = "" }, onConflict = "overwrite" });
        Assert.Equal("one", File.ReadAllText(Path.Combine(_backup, "Photos", "1.jpg")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(_backup, "Photos", "2.jpg")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_backup, "Photos", "keep.jpg")));
        Assert.False(Directory.Exists(Path.Combine(_media, "Photos")));
    }

    [Fact]
    public async Task DeletesRecursivelyWithoutFollowingSymlinks()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
        Write("Old/a.txt");
        Write("Old/Deep/b.txt");
        File.CreateSymbolicLink(Path.Combine(_media, "Old", "link"), outside);
        var job = await RunAsync(new { kind = "delete", items = new[] { new { shareId = _mediaId, path = "Old" } } });
        Assert.False(Directory.Exists(Path.Combine(_media, "Old")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
        Assert.Equal(job.GetProperty("progress").GetProperty("itemsTotal").GetInt64(), job.GetProperty("progress").GetProperty("itemsDone").GetInt64());
    }

    [Fact]
    public async Task CopiesSkipSymlinksInsteadOfFollowingThem()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        Write("Docs/a.txt");
        File.CreateSymbolicLink(Path.Combine(_media, "Docs", "escape"), outside);
        var job = await RunAsync(new { kind = "copy", items = new[] { new { shareId = _mediaId, path = "Docs" } }, target = new { shareId = _backupId, path = "" } });
        Assert.Equal(["Docs/escape"], job.GetProperty("result").GetProperty("skipped").EnumerateArray().Select(e => e.GetString()));
        Assert.False(Path.Exists(Path.Combine(_backup, "Docs", "escape")));
    }

    [Theory]
    [InlineData("Album", "Album")]
    [InlineData("Album", "Album/Sub")]
    public async Task RefusesToMoveAFolderIntoItself(string source, string target)
    {
        Write("Album/Sub/a.txt");
        using var response = await _admin.PostAsync("/api/files/operations", new { kind = "move", items = new[] { new { shareId = _mediaId, path = source } }, target = new { shareId = _mediaId, path = target } });
        Assert.Equal("fs.operation.into_itself", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task ChecksAccessBeforeTheJobStarts()
    {
        Write("a.txt");
        var userId = await _factory.InsertUserAsync("rita");
        using (var grant = await _admin.PutAsJsonAsync($"/api/shares/{_mediaId}/access/{userId}", new { access = "read" }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        }
        using var reader = _factory.ClientWithCookie(await _factory.SessionCookieAsync(userId));
        using var delete = await reader.PostAsync("/api/files/operations", new { kind = "delete", items = new[] { new { shareId = _mediaId, path = "a.txt" } } });
        Assert.Equal("fs.share.read_only", await delete.ErrorCodeAsync());
        using var copyOut = await reader.PostAsync("/api/files/operations", new { kind = "copy", items = new[] { new { shareId = _mediaId, path = "a.txt" } }, target = new { shareId = _backupId, path = "" } });
        Assert.Equal("fs.share.not_found", await copyOut.ErrorCodeAsync());
        Assert.True(File.Exists(Path.Combine(_media, "a.txt")));
        using var noItems = await _admin.PostAsync("/api/files/operations", new { kind = "delete", items = Array.Empty<object>() });
        Assert.Equal("fs.operation.invalid", await noItems.ErrorCodeAsync());
    }
}
