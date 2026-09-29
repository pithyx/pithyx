using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Files.Thumbnails;
using Pithyx.Daemon.Tests.Support;
using SkiaSharp;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Files.Thumbnails;

/// <summary>Thumbnails through the API: queued, never rendered in the request, checked like downloads.</summary>
public sealed class ThumbnailApiTests : IAsyncLifetime
{
    private static readonly string[] _beach = ["beach.jpg"];

    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly string _root;
    private long _share;

    public ThumbnailApiTests()
    {
        _admin = _factory.CreateApiClient();
        _root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "photos")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "trip"));
        File.WriteAllBytes(Path.Combine(_root, "trip", "beach.jpg"), TestImages.Encode(SKEncodedImageFormat.Jpeg, 800, 600));
        File.WriteAllBytes(Path.Combine(_root, "trip", "sunset.png"), TestImages.Encode(SKEncodedImageFormat.Png, 300, 900));
        File.WriteAllBytes(Path.Combine(_root, "trip", "bomb.png"), TestImages.PngBomb(40_000, 40_000));
        File.WriteAllText(Path.Combine(_root, "trip", "fake.jpg"), "<script>alert(1)</script>");
        File.WriteAllText(Path.Combine(_root, "trip", "notes.txt"), "hello");
    }

    public async ValueTask InitializeAsync()
    {
        await _admin.SetupAdminAsync();
        using var created = await _admin.PostAsync("/api/shares", new { name = "Photos", path = _root });
        _share = (await created.JsonAsync()).GetProperty("id").GetInt64();
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<Dictionary<string, (string Status, string? Version)>> RequestAsync(HttpClient client, string size = "large", params string[] names)
    {
        using var response = await client.PostAsJsonAsync($"/api/files/{_share}/thumbnails", new { folder = "trip", names, size }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.StartsWith($"thumbnails/{_share}/", body.GetProperty("topic").GetString(), StringComparison.Ordinal);
        return body.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!,
            item => (item.GetProperty("status").GetString()!, item.GetProperty("version").GetString()));
    }

    private async Task<Dictionary<string, (string Status, string? Version)>> SettledAsync(params string[] names)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var states = await RequestAsync(_admin, names: names);
            if (states.Values.All(state => state.Status != "pending"))
            {
                return states;
            }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "Thumbnails never settled.");
            await Task.Delay(50, Token);
        }
    }

    private Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string size = "large") =>
        client.GetAsync($"/api/files/{_share}/thumbnail?path={Uri.EscapeDataString(path)}&size={size}", Token);

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
            using var granted = await _admin.PutAsJsonAsync($"/api/shares/{_share}/access/{id}", new { access }, Token);
            Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        }
        var client = _factory.CreateApiClient();
        (await client.LoginAsync(username, AdminPassword)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task ImagesAreQueuedAndServedOnceRenderedOthersGetNone()
    {
        var first = await RequestAsync(_admin, names: ["beach.jpg", "sunset.png", "notes.txt", "missing.jpg", "../escape.jpg"]);

        Assert.Equal("none", first["notes.txt"].Status);
        Assert.Equal("none", first["missing.jpg"].Status);
        Assert.Equal("none", first["../escape.jpg"].Status);
        Assert.True(first["beach.jpg"].Status is "pending" or "ready");

        var settled = await SettledAsync("beach.jpg", "sunset.png", "bomb.png", "fake.jpg");
        Assert.Equal("ready", settled["beach.jpg"].Status);
        Assert.Equal("ready", settled["sunset.png"].Status);
        Assert.Equal(("none", (string?)null), settled["bomb.png"]);
        Assert.Equal(("none", (string?)null), settled["fake.jpg"]);

        using var image = await GetAsync(_admin, "trip/beach.jpg");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/webp", image.Content.Headers.ContentType!.MediaType);
        Assert.Contains("sandbox", image.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        var bytes = await image.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal((256, 192, SKEncodedImageFormat.Webp), TestImages.Inspect(bytes));
        Assert.StartsWith(settled["beach.jpg"].Version!, image.Headers.ETag!.Tag.Trim('"'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedFileNeedsANewThumbnail()
    {
        var before = (await SettledAsync("beach.jpg"))["beach.jpg"];
        File.WriteAllBytes(Path.Combine(_root, "trip", "beach.jpg"), TestImages.Encode(SKEncodedImageFormat.Jpeg, 100, 400));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "trip", "beach.jpg"), DateTime.UtcNow.AddMinutes(1));

        using var stale = await GetAsync(_admin, "trip/beach.jpg");
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal("fs.thumbnail.not_found", (await stale.JsonAsync()).GetProperty("code").GetString());

        var after = (await SettledAsync("beach.jpg"))["beach.jpg"];
        Assert.NotEqual(before.Version, after.Version);
        using var fresh = await GetAsync(_admin, "trip/beach.jpg");
        Assert.Equal((64, 256), TestImages.Inspect(await fresh.Content.ReadAsByteArrayAsync(Token)) is var (w, h, _) ? (w, h) : default);
    }

    [Fact]
    public async Task AThumbnailNeverShowsMoreThanTheFile()
    {
        await SettledAsync("beach.jpg");
        using var none = await UserAsync("nora", null);
        using var reader = await UserAsync("rita", "read");
        using var anonymous = _factory.CreateApiClient();

        using var refused = await GetAsync(none, "trip/beach.jpg");
        using var refusedDownload = await none.GetAsync($"/api/files/{_share}/content?path=trip/beach.jpg", Token);
        using var refusedRequest = await none.PostAsJsonAsync($"/api/files/{_share}/thumbnails", new { folder = "trip", names = _beach, size = "large" }, Token);
        using var unauthenticated = await GetAsync(anonymous, "trip/beach.jpg");
        using var allowed = await GetAsync(reader, "trip/beach.jpg");
        using var escape = await GetAsync(reader, "../../pithyx.db");

        // Refused exactly like the download: the share is as unknown as one that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, refusedDownload.StatusCode);
        Assert.Equal(refusedDownload.StatusCode, refused.StatusCode);
        Assert.Equal(refusedDownload.StatusCode, refusedRequest.StatusCode);
        Assert.Equal("fs.share.not_found", (await refused.JsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);
    }

    [Fact]
    public async Task ARequestIsLimitedToAScreenOfNames()
    {
        using var response = await _admin.PostAsJsonAsync(
            $"/api/files/{_share}/thumbnails",
            new { folder = "trip", names = Enumerable.Range(0, ThumbnailService.MaxNames + 1).Select(i => $"{i}.jpg").ToArray(), size = "small" },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("fs.thumbnail.too_many", (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("page.html", "<html><script>alert(1)</script></html>", "text/plain")]
    [InlineData("drawing.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>", "image/svg+xml")]
    [InlineData("data.xml", "<a/>", "text/plain")]
    [InlineData("script.js", "alert(1)", "text/plain")]
    [InlineData("program.exe", "MZ", "application/octet-stream")]
    [InlineData("notes.txt", "hello", "text/plain")]
    public async Task InlineContentNeverRunsAndDownloadsKeepTheSandbox(string name, string content, string type)
    {
        File.WriteAllText(Path.Combine(_root, name), content);

        using var inline = await _admin.GetAsync($"/api/files/{_share}/content?path={name}&inline=true", Token);
        using var download = await _admin.GetAsync($"/api/files/{_share}/content?path={name}", Token);

        Assert.Equal(type, inline.Content.Headers.ContentType!.MediaType);
        foreach (var response in new[] { inline, download })
        {
            Assert.StartsWith("sandbox", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task RangeRequestsStreamAudioAndVideo()
    {
        File.WriteAllBytes(Path.Combine(_root, "clip.mp4"), Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{_share}/content?path=clip.mp4&inline=true");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199);

        using var response = await _admin.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync(Token)).Length);
    }
}
