using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Layouts;

public sealed class DesktopFileShortcutTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly string _media;
    private readonly string _backup;
    private long _mediaId;
    private long _backupId;

    public DesktopFileShortcutTests()
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

    private void Write(string relative)
    {
        var path = Path.Combine(_media, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, long share, string path, int column = 2)
    {
        using var response = await client.PostAsync("/api/desktop/items", new { kind = "file", share, path, column, row = 0 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private static async Task<JsonElement> ItemAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync("/api/desktop/items", Token);
        response.EnsureSuccessStatusCode();
        return (await response.JsonAsync()).EnumerateArray().Single(item => item.GetProperty("id").GetInt64() == id);
    }

    private static (long Share, string Path, string Target) Where(JsonElement item) =>
        (item.GetProperty("share").GetInt64(), item.GetProperty("path").GetString()!, item.GetProperty("target").GetString()!);

    private async Task RunAsync(object request)
    {
        using var started = await _admin.PostAsync("/api/files/operations", request);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.JsonAsync()).GetProperty("id").GetInt64();
        await JobTestSupport.WaitUntilAsync(async () =>
        {
            using var response = await _admin.GetAsync($"/api/jobs/{id}", Token);
            return (await response.JsonAsync()).GetProperty("state").GetString() == "succeeded";
        }, $"job {id}");
    }

    [Fact]
    public async Task AShortcutSaysWhetherItsTargetIsAFileAFolderOrMissing()
    {
        Write("Docs/report.pdf");
        var file = await CreateAsync(_admin, _mediaId, "Docs/report.pdf");
        var folder = await CreateAsync(_admin, _mediaId, "/Docs/", column: 3);

        Assert.Equal("file", file.GetProperty("kind").GetString());
        Assert.Equal((_mediaId, "Docs/report.pdf", "file"), Where(file));
        Assert.Equal((_mediaId, "Docs", "folder"), Where(folder));

        // Gone past the daemon, for example over SMB.
        File.Delete(Path.Combine(_media, "Docs", "report.pdf"));
        Assert.Equal("missing", Where(await ItemAsync(_admin, file.GetProperty("id").GetInt64())).Target);
    }

    [Fact]
    public async Task OnlyAReadableTargetBecomesAShortcut()
    {
        using var gone = await _admin.PostAsync("/api/desktop/items", new { kind = "file", share = _mediaId, path = "nothing.txt", column = 2, row = 0 });
        Assert.Equal("fs.entry.not_found", await gone.ErrorCodeAsync());

        Write("a.txt");
        foreach (var body in new object[]
        {
            new { kind = "file", share = _mediaId, path = "", column = 2, row = 0 },
            new { kind = "file", share = _mediaId, path = "a.txt", app = "org.pithyx.files", column = 2, row = 0 },
            new { kind = "note", text = "x", share = _mediaId, column = 2, row = 0 },
        })
        {
            using var response = await _admin.PostAsync("/api/desktop/items", body);
            Assert.Equal("desktop.item.invalid", await response.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task ShortcutsFollowARenameOfTheTargetAndOfAFolderAboveIt()
    {
        Write("Docs/report.pdf");
        var file = await CreateAsync(_admin, _mediaId, "Docs/report.pdf");
        var id = file.GetProperty("id").GetInt64();

        using (var renamed = await _admin.PostAsync($"/api/files/{_mediaId}/rename", new { path = "Docs/report.pdf", name = "final.pdf" }))
        {
            renamed.EnsureSuccessStatusCode();
        }
        var item = await ItemAsync(_admin, id);
        Assert.Equal((_mediaId, "Docs/final.pdf", "file"), Where(item));
        Assert.True(item.GetProperty("version").GetInt64() > file.GetProperty("version").GetInt64());

        using (var renamed = await _admin.PostAsync($"/api/files/{_mediaId}/rename", new { path = "Docs", name = "Papers" }))
        {
            renamed.EnsureSuccessStatusCode();
        }
        Assert.Equal((_mediaId, "Papers/final.pdf", "file"), Where(await ItemAsync(_admin, id)));
    }

    [Fact]
    public async Task ShortcutsOfEveryUserFollowAMoveIntoAnotherShare()
    {
        Write("Album/a.jpg");
        Write("Albums/b.jpg");
        var album = await CreateAsync(_admin, _mediaId, "Album");
        var inside = await CreateAsync(_admin, _mediaId, "Album/a.jpg", column: 3);
        var similar = await CreateAsync(_admin, _mediaId, "Albums", column: 4);

        long userId;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            userId = await TestUsers.InsertAsync(connection, "anna", hash: await _factory.Services.GetRequiredService<PasswordHasher>().HashAsync(AdminPassword, Token));
        }
        await _admin.SetShareAccessAsync(_mediaId, userId, "read");
        using var anna = _factory.CreateApiClient();
        (await anna.LoginAsync("anna", AdminPassword)).Dispose();
        var annas = await CreateAsync(anna, _mediaId, "Album/a.jpg");

        await RunAsync(new { kind = "move", items = new[] { new { shareId = _mediaId, path = "Album" } }, target = new { shareId = _backupId, path = "" } });

        Assert.Equal((_backupId, "Album", "folder"), Where(await ItemAsync(_admin, album.GetProperty("id").GetInt64())));
        Assert.Equal((_backupId, "Album/a.jpg", "file"), Where(await ItemAsync(_admin, inside.GetProperty("id").GetInt64())));
        Assert.Equal((_mediaId, "Albums", "folder"), Where(await ItemAsync(_admin, similar.GetProperty("id").GetInt64())));
        // Anna's shortcut followed too, but she may not read the backup share.
        Assert.Equal((_backupId, "Album/a.jpg", "missing"), Where(await ItemAsync(anna, annas.GetProperty("id").GetInt64())));
    }

    [Fact]
    public async Task AnOwnLabelStaysUntilItIsCleared()
    {
        Write("a.txt");
        var id = (await CreateAsync(_admin, _mediaId, "a.txt")).GetProperty("id").GetInt64();

        using (var named = await _admin.PatchAsync($"/api/desktop/items/{id}", JsonContent.Create(new { name = "Todo" }), Token))
        {
            named.EnsureSuccessStatusCode();
        }
        using (var renamed = await _admin.PostAsync($"/api/files/{_mediaId}/rename", new { path = "a.txt", name = "b.txt" }))
        {
            renamed.EnsureSuccessStatusCode();
        }
        var item = await ItemAsync(_admin, id);
        Assert.Equal("Todo", item.GetProperty("name").GetString());
        Assert.Equal("b.txt", item.GetProperty("path").GetString());

        using (var cleared = await _admin.PatchAsync($"/api/desktop/items/{id}", JsonContent.Create(new { name = "" }), Token))
        {
            cleared.EnsureSuccessStatusCode();
        }
        Assert.Equal(JsonValueKind.Null, (await ItemAsync(_admin, id)).GetProperty("name").ValueKind);
    }
}
