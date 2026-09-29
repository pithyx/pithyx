using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Files;

public sealed class FileApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly string _root;
    private readonly string _outside;
    private long _share;

    public FileApiTests()
    {
        _admin = _factory.CreateApiClient();
        _root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "share")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "secret")).FullName;
        File.WriteAllText(Path.Combine(_outside, "passwords.txt"), "hunter2");
    }

    public async ValueTask InitializeAsync()
    {
        await _admin.SetupAdminAsync();
        using var created = await _admin.PostAsync("/api/shares", new { name = "Media", path = _root });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _share = (await created.JsonAsync()).GetProperty("id").GetInt64();
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private string Api(string rest) => $"/api/files/{_share}/{rest}";

    private static string Q(string value) => Uri.EscapeDataString(value);

    private async Task<JsonElement> ListAsync(HttpClient client, string path = "", string query = "")
    {
        using var response = await client.GetAsync($"/api/files/{_share}/list?path={Q(path)}{query}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static string[] Names(JsonElement page) =>
        [.. page.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!)];

    private async Task<(HttpClient Client, long Id)> UserAsync(string username, string? access)
    {
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var hash = await hasher.HashAsync(AdminPassword, Token);
        long id;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            id = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES (@username, @hash, 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z') RETURNING id",
                new { username, hash });
        }
        if (access is not null)
        {
            using var granted = await _admin.PutAsJsonAsync($"/api/shares/{_share}/access/{id}", new { access }, Token);
            Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        }
        var client = _factory.CreateApiClient();
        using var login = await client.LoginAsync(username, AdminPassword);
        login.EnsureSuccessStatusCode();
        return (client, id);
    }

    [Fact]
    public async Task ListsFoldersInNaturalOrderWithFoldersAndFilesAlike()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Holiday"));
        foreach (var name in new[] { "img10.jpg", "img2.jpg", "IMG1.jpg" })
        {
            File.WriteAllText(Path.Combine(_root, name), name);
        }
        var page = await ListAsync(_admin);
        Assert.Equal(["Holiday", "IMG1.jpg", "img2.jpg", "img10.jpg"], Names(page));
        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        Assert.StartsWith($"files/{_share}/", page.GetProperty("topic").GetString(), StringComparison.Ordinal);
        var entry = page.GetProperty("entries")[1];
        Assert.Equal("file", entry.GetProperty("kind").GetString());
        Assert.Equal(8, entry.GetProperty("size").GetInt64());

        var bySize = await ListAsync(_admin, "", "&sort=size&order=desc");
        Assert.Equal("img10.jpg", Names(bySize)[0]);
    }

    [Fact]
    public async Task PagesStayConsistentWhenEntriesComeAndGo()
    {
        for (var i = 0; i < 25; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i:00}"), "");
        }
        var first = await ListAsync(_admin, "", "&limit=10");
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"f{i:00}"), Names(first));
        var cursor = first.GetProperty("nextCursor").GetString()!;

        // Changes before the cursor position never shift the next page.
        File.Delete(Path.Combine(_root, "f09"));
        File.WriteAllText(Path.Combine(_root, "a-new"), "");
        var second = await ListAsync(_admin, "", $"&limit=10&cursor={Q(cursor)}");
        Assert.Equal(Enumerable.Range(10, 10).Select(i => $"f{i:00}"), Names(second));
        var third = await ListAsync(_admin, "", $"&limit=10&cursor={Q(second.GetProperty("nextCursor").GetString()!)}");
        Assert.Equal(Enumerable.Range(20, 5).Select(i => $"f{i:00}"), Names(third));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("nextCursor").ValueKind);

        using var wrongSort = await _admin.GetAsync(Api($"list?sort=size&cursor={Q(cursor)}"), Token);
        Assert.Equal("fs.cursor.invalid", await wrongSort.ErrorCodeAsync());
    }

    [Theory]
    [InlineData("../secret/passwords.txt", "fs.path.invalid")]
    [InlineData("a/../../secret/passwords.txt", "fs.path.invalid")]
    [InlineData("/proc/self/environ", "fs.entry.not_found")]
    [InlineData("escape/passwords.txt", "fs.entry.not_found")]
    [InlineData("secret-file", "fs.entry.not_found")]
    [InlineData("proc/1/root/etc/shadow", "fs.entry.not_found")]
    public async Task NeverServesAnythingOutsideTheShare(string path, string code)
    {
        File.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);
        File.CreateSymbolicLink(Path.Combine(_root, "secret-file"), Path.Combine(_outside, "passwords.txt"));
        File.CreateSymbolicLink(Path.Combine(_root, "proc"), "/proc");
        using var download = await _admin.GetAsync(Api($"content?path={Q(path)}"), Token);
        Assert.Equal(code, await download.ErrorCodeAsync());
        using var list = await _admin.GetAsync(Api("list?path=escape"), Token);
        Assert.Equal("fs.entry.not_found", await list.ErrorCodeAsync());
    }

    [Fact]
    public async Task FollowsSymlinksThatStayInsideTheShareAndListsThemAsSymlinks()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        File.WriteAllText(Path.Combine(_root, "real", "a.txt"), "inside");
        File.CreateSymbolicLink(Path.Combine(_root, "link"), "real");
        var page = await ListAsync(_admin);
        Assert.Equal("symlink", page.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "link").GetProperty("kind").GetString());
        Assert.Equal("inside", await _admin.GetStringAsync(Api("content?path=link/a.txt"), Token));
    }

    [Fact]
    public async Task ChecksShareAccessOnEveryRequest()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        var (none, _) = await UserAsync("nora", access: null);
        using var noneClient = none;
        using var hidden = await none.GetAsync(Api("list"), Token);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("fs.share.not_found", await hidden.ErrorCodeAsync());
        Assert.Empty((await (await none.GetAsync("/api/shares", Token)).JsonAsync()).EnumerateArray());

        var (reader, ritaId) = await UserAsync("rita", access: "read");
        using var readerClient = reader;
        Assert.Equal(["a.txt"], Names(await ListAsync(reader)));
        var shares = (await (await reader.GetAsync("/api/shares", Token)).JsonAsync()).EnumerateArray().ToList();
        Assert.Equal("read", shares.Single().GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Null, shares.Single().GetProperty("path").ValueKind);
        using var upload = await reader.PutAsync(Api("content?path=b.txt"), new StringContent("b"), Token);
        Assert.Equal("fs.share.read_only", await upload.ErrorCodeAsync());
        using var createShare = await reader.PostAsync("/api/shares", new { name = "Mine", path = _outside });
        Assert.Equal(HttpStatusCode.Forbidden, createShare.StatusCode);

        using var revoked = await _admin.PutAsJsonAsync($"/api/shares/{_share}/access/{ritaId}", new { access = "none" }, Token);
        using var afterRevoke = await reader.GetAsync(Api("list"), Token);
        Assert.Equal(HttpStatusCode.NotFound, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task DownloadsWithRangesTagsAndASandboxedContentPolicy()
    {
        File.WriteAllText(Path.Combine(_root, "page.html"), "<script>alert(1)</script>0123456789");
        using var full = await _admin.GetAsync(Api("content?path=page.html"), Token);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("application/octet-stream", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", full.Content.Headers.ContentDisposition!.DispositionType);
        Assert.StartsWith("sandbox", full.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", full.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.NotNull(full.Headers.ETag);

        using var inline = await _admin.GetAsync(Api("content?path=page.html&inline=true"), Token);
        // A page in a share is shown as its source, never rendered.
        Assert.Equal("text/plain", inline.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("sandbox", inline.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Get, Api("content?path=page.html"));
        request.Headers.Range = new RangeHeaderValue(25, 29);
        using var part = await _admin.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("01234", await part.Content.ReadAsStringAsync(Token));

        using var outOfRange = new HttpRequestMessage(HttpMethod.Get, Api("content?path=page.html"));
        outOfRange.Headers.Range = new RangeHeaderValue(1000, 2000);
        using var refused = await _admin.SendAsync(outOfRange, Token);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, refused.StatusCode);

        Directory.CreateDirectory(Path.Combine(_root, "folder"));
        using var folder = await _admin.GetAsync(Api("content?path=folder"), Token);
        Assert.Equal("fs.entry.is_folder", await folder.ErrorCodeAsync());
    }

    [Fact]
    public async Task UploadsAppearOnlyWhenCompleteAndNeverReplaceByAccident()
    {
        var content = new byte[3 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(content);
        using (var created = await _admin.PutAsync(Api("content?path=big.bin"), new ByteArrayContent(content), Token))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(content.Length, (await created.JsonAsync()).GetProperty("size").GetInt64());
        }
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_root, "big.bin"), Token));

        using var taken = await _admin.PutAsync(Api("content?path=big.bin"), new StringContent("x"), Token);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("fs.entry.exists", await taken.ErrorCodeAsync());
        using var replaced = await _admin.PutAsync(Api("content?path=big.bin&overwrite=true"), new StringContent("x"), Token);
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);
        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(_root, "big.bin"), Token));

        using var noParent = await _admin.PutAsync(Api("content?path=missing/a.txt"), new StringContent("x"), Token);
        Assert.Equal("fs.entry.not_found", await noParent.ErrorCodeAsync());
        using var badName = await _admin.PutAsync(Api($"content?path={Q(".pithyx-upload-x")}"), new StringContent("x"), Token);
        Assert.Equal("fs.name.invalid", await badName.ErrorCodeAsync());
        using var outside = await _admin.PutAsync(Api($"content?path={Q("../secret/new.txt")}"), new StringContent("x"), Token);
        Assert.Equal("fs.path.invalid", await outside.ErrorCodeAsync());
        File.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);
        using var throughLink = await _admin.PutAsync(Api("content?path=escape/new.txt"), new StringContent("x"), Token);
        Assert.Equal("fs.entry.not_found", await throughLink.ErrorCodeAsync());
        Assert.False(File.Exists(Path.Combine(_outside, "new.txt")));
    }

    [Fact]
    public async Task AnInterruptedUploadLeavesNothingBehind()
    {
        using var content = new StreamContent(new ThrowingStream(1024 * 1024));
        await Assert.ThrowsAnyAsync<Exception>(() => _admin.PutAsync(Api("content?path=broken.bin"), content, Token));
        // The daemon notices the aborted body shortly after the client gave up.
        for (var i = 0; i < 50 && Directory.EnumerateFileSystemEntries(_root).Any(); i++)
        {
            await Task.Delay(20, Token);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task CreatesAndRenamesWithoutReplacing()
    {
        using (var created = await _admin.PostAsync(Api("folders"), new { path = "Docs" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("folder", (await created.JsonAsync()).GetProperty("kind").GetString());
        }
        using var again = await _admin.PostAsync(Api("folders"), new { path = "Docs" });
        Assert.Equal("fs.entry.exists", await again.ErrorCodeAsync());
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        using var clash = await _admin.PostAsync(Api("rename"), new { path = "a.txt", name = "b.txt" });
        Assert.Equal("fs.entry.exists", await clash.ErrorCodeAsync());
        Assert.Equal("b", File.ReadAllText(Path.Combine(_root, "b.txt")));
        using var renamed = await _admin.PostAsync(Api("rename"), new { path = "a.txt", name = "c.txt" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.True(File.Exists(Path.Combine(_root, "c.txt")));
        using var slash = await _admin.PostAsync(Api("rename"), new { path = "c.txt", name = "../c.txt" });
        Assert.Equal("fs.name.invalid", await slash.ErrorCodeAsync());
    }

    [Fact]
    public async Task RefusesSharesInSystemFoldersOrOverlappingOthers()
    {
        foreach (var path in new[] { "/etc", "/var/lib", "/", "relative", Path.Combine(_root, "sub"), Path.GetDirectoryName(_root)! })
        {
            Directory.CreateDirectory(Path.Combine(_root, "sub"));
            using var response = await _admin.PostAsync("/api/shares", new { name = "X" + Math.Abs(path.GetHashCode(StringComparison.Ordinal)), path });
            Assert.Equal("fs.share.invalid", await response.ErrorCodeAsync());
        }
        using var duplicate = await _admin.PostAsync("/api/shares", new { name = "media", path = _outside });
        Assert.Equal("fs.share.exists", await duplicate.ErrorCodeAsync());
        File.CreateSymbolicLink(Path.Combine(_factory.DataDirectory, "linked"), _outside);
        using var viaLink = await _admin.PostAsync("/api/shares", new { name = "Linked", path = Path.Combine(_factory.DataDirectory, "linked") });
        Assert.Equal("fs.share.invalid", await viaLink.ErrorCodeAsync());
    }

    /// <summary>A request body that fails after some bytes, like a client that goes away mid-upload.</summary>
    private sealed class ThrowingStream(int failAfter) : Stream
    {
        private int _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _sent; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent >= failAfter)
            {
                throw new IOException("client went away");
            }
            var length = Math.Min(count, failAfter - _sent);
            buffer.AsSpan(offset, length).Fill((byte)'x');
            _sent += length;
            return length;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
