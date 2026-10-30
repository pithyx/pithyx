using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Layouts;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Layouts;

public sealed class DesktopItemApiTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _client;

    public DesktopItemApiTests()
    {
        _client = _factory.CreateApiClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<JsonElement[]> ListAsync()
    {
        using var response = await _client.GetAsync("/api/desktop/items", Token);
        response.EnsureSuccessStatusCode();
        return [.. (await response.JsonAsync()).EnumerateArray()];
    }

    private async Task<JsonElement> CreateNoteAsync(string text = "Order disks", int column = 3, int row = 0)
    {
        using var response = await _client.PostAsync("/api/desktop/items", new { kind = "note", text, column, row });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<HttpResponseMessage> ChangeAsync(long id, object change, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/desktop/items/{id}") { Content = JsonContent.Create(change) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        return await _client.SendAsync(request, Token);
    }

    private Task<HttpResponseMessage> DeleteAsync(long id) => _client.DeleteAsync($"/api/desktop/items/{id}", Token);

    private async Task<long> InsertUserAsync(string username)
    {
        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return await TestUsers.InsertAsync(connection, username);
    }

    [Fact]
    public async Task ANewAdministratorStartsWithTheDefaultShortcutsStorageAndSettingsInTheFirstColumn()
    {
        await _client.SetupAdminAsync();

        var items = await ListAsync();

        Assert.Equal(["org.pithyx.files", "org.pithyx.settings", "org.pithyx.monitor", "org.pithyx.about"], items.Select(item => item.GetProperty("app").GetString()));
        Assert.All(items, item => Assert.Equal("shortcut", item.GetProperty("kind").GetString()));
        Assert.Equal([0, 1, 2, 3], items.Select(item => item.GetProperty("row").GetInt32()));
        Assert.All(items, item => Assert.Equal(0, item.GetProperty("column").GetInt32()));
        Assert.All(items, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("name").ValueKind));
    }

    [Fact]
    public async Task NotesKeepTheirTextUpToTheLimit()
    {
        await _client.SetupAdminAsync();
        var text = "Grüße 👋\n" + new string('x', DesktopItemService.MaxNoteLength - 9);

        var note = await CreateNoteAsync(text);
        using var tooLong = await _client.PostAsync("/api/desktop/items", new { kind = "note", text = text + "x", column = 0, row = 3 });

        Assert.Equal(text, note.GetProperty("text").GetString());
        Assert.Equal(1, note.GetProperty("version").GetInt64());
        Assert.Contains(await ListAsync(), item => item.GetProperty("id").GetInt64() == note.GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var problem = await tooLong.JsonAsync();
        Assert.Equal("desktop.note.too_long", problem.GetProperty("code").GetString());
        Assert.Equal(DesktopItemService.MaxNoteLength, problem.GetProperty("params").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public async Task CreatingAnswersWithTheItemsUrlAndVersion()
    {
        await _client.SetupAdminAsync();

        using var response = await _client.PostAsync("/api/desktop/items", new { kind = "note", text = "", column = 1, row = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.JsonAsync()).GetProperty("id").GetInt64();
        Assert.Equal($"/api/desktop/items/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task AChangeMovesAnItemAndAnswersWithItsNewVersion()
    {
        await _client.SetupAdminAsync();
        var shortcut = (await ListAsync())[0].GetProperty("id").GetInt64();
        var note = (await CreateNoteAsync()).GetProperty("id").GetInt64();

        using var moved = await ChangeAsync(shortcut, new { column = 5, row = 2 }, "\"1\"");
        using var written = await ChangeAsync(note, new { text = "Done" }, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("\"2\"", moved.Headers.ETag?.Tag);
        var item = await moved.JsonAsync();
        Assert.Equal((shortcut, 5, 2, 2L), (item.GetProperty("id").GetInt64(), item.GetProperty("column").GetInt32(), item.GetProperty("row").GetInt32(), item.GetProperty("version").GetInt64()));
        var changedNote = await written.JsonAsync();
        Assert.Equal(("Done", 3, 2L), (changedNote.GetProperty("text").GetString(), changedNote.GetProperty("column").GetInt32(), changedNote.GetProperty("version").GetInt64()));
    }

    [Fact]
    public async Task AStaleOrMalformedIfMatchIsRefusedWithTheCurrentVersion()
    {
        await _client.SetupAdminAsync();
        var id = (await ListAsync())[0].GetProperty("id").GetInt64();
        using (var other = await ChangeAsync(id, new { row = 4 }, "\"1\""))
        {
            other.EnsureSuccessStatusCode();
        }

        using var stale = await ChangeAsync(id, new { column = 7 }, "\"1\"");
        using var malformed = await ChangeAsync(id, new { column = 7 }, "W/\"2\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = await stale.JsonAsync();
        Assert.Equal("desktop.item.conflict", problem.GetProperty("code").GetString());
        Assert.Equal(2, problem.GetProperty("params").GetProperty("version").GetInt64());
        Assert.False(problem.GetProperty("params").TryGetProperty("id", out _));
        Assert.Equal(HttpStatusCode.PreconditionFailed, malformed.StatusCode);
        Assert.Equal("desktop.item.conflict", await malformed.ErrorCodeAsync());
        var after = (await ListAsync()).Single(item => item.GetProperty("id").GetInt64() == id);
        Assert.Equal((0, 4, 2L), (after.GetProperty("column").GetInt32(), after.GetProperty("row").GetInt32(), after.GetProperty("version").GetInt64()));
    }

    [Fact]
    public async Task AChangeWithoutIfMatchIsUnconditional()
    {
        await _client.SetupAdminAsync();
        var id = (await ListAsync())[0].GetProperty("id").GetInt64();
        using (var other = await ChangeAsync(id, new { row = 4 }, "\"1\""))
        {
            other.EnsureSuccessStatusCode();
        }

        using var unconditional = await ChangeAsync(id, new { column = 6 });

        Assert.Equal(HttpStatusCode.OK, unconditional.StatusCode);
        Assert.Equal("\"3\"", unconditional.Headers.ETag?.Tag);
        var item = await unconditional.JsonAsync();
        Assert.Equal((6, 4), (item.GetProperty("column").GetInt32(), item.GetProperty("row").GetInt32()));
    }

    [Fact]
    public async Task ShortcutsGetAnOwnNameAndAnEmptyNameGoesBackToTheAppName()
    {
        await _client.SetupAdminAsync();
        var id = (await ListAsync())[0].GetProperty("id").GetInt64();

        using var renamed = await ChangeAsync(id, new { name = "  My disks  " }, "\"1\"");
        using var reset = await ChangeAsync(id, new { name = "" }, "\"2\"");

        Assert.Equal("My disks", (await renamed.JsonAsync()).GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, (await reset.JsonAsync()).GetProperty("name").ValueKind);
    }

    [Theory]
    [InlineData("""{"kind":"shortcut","app":"org.pithyx.files","text":"no","column":0,"row":0}""")]
    [InlineData("""{"kind":"shortcut","app":"../x","column":0,"row":0}""")]
    [InlineData("""{"kind":"shortcut","app":"files","column":0,"row":0}""")]
    [InlineData("""{"kind":"shortcut","column":0,"row":0}""")]
    [InlineData("""{"kind":"note","app":"disks","text":"","column":0,"row":0}""")]
    [InlineData("""{"kind":"note","column":0,"row":0}""")]
    [InlineData("""{"kind":"note","text":"","column":-1,"row":0}""")]
    [InlineData("""{"kind":"note","text":"","column":0,"row":256}""")]
    public async Task MalformedItemsAreRefused(string body)
    {
        await _client.SetupAdminAsync();

        using var response = await _client.PostAsync("/api/desktop/items", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("desktop.item.invalid", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task InvalidChangesAreRefused()
    {
        await _client.SetupAdminAsync();
        var shortcut = (await ListAsync())[0].GetProperty("id").GetInt64();
        var note = (await CreateNoteAsync()).GetProperty("id").GetInt64();

        using var textOnShortcut = await ChangeAsync(shortcut, new { text = "x" }, "\"1\"");
        using var nameOnNote = await ChangeAsync(note, new { name = "x" }, "\"1\"");
        using var outside = await ChangeAsync(note, new { column = DesktopItemService.MaxCell + 1 }, "\"1\"");
        using var tooLong = await ChangeAsync(note, new { text = new string('x', DesktopItemService.MaxNoteLength + 1) }, "\"1\"");

        Assert.Equal("desktop.item.invalid", await textOnShortcut.ErrorCodeAsync());
        Assert.Equal("desktop.item.invalid", await nameOnNote.ErrorCodeAsync());
        Assert.Equal("desktop.item.invalid", await outside.ErrorCodeAsync());
        Assert.Equal("desktop.note.too_long", await tooLong.ErrorCodeAsync());
        Assert.Equal(1, (await ListAsync()).Single(item => item.GetProperty("id").GetInt64() == note).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task ItemsOfOtherUsersCannotBeSeenChangedOrDeleted()
    {
        await _client.SetupAdminAsync();
        var otherUser = await InsertUserAsync("bob");
        long otherItem;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            otherItem = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO desktop_items (user_id, kind, data, created_at, updated_at) VALUES (@otherUser, 'note', '{\"text\":\"secret\"}', 'x', 'x') RETURNING id",
                new { otherUser });
        }

        using var change = await ChangeAsync(otherItem, new { text = "mine" }, "\"1\"");
        using var unconditional = await ChangeAsync(otherItem, new { text = "mine" });
        using var missing = await ChangeAsync(otherItem + 1000, new { row = 1 });
        using var delete = await DeleteAsync(otherItem);

        Assert.DoesNotContain(await ListAsync(), item => item.GetProperty("id").GetInt64() == otherItem);
        Assert.Equal(HttpStatusCode.NotFound, change.StatusCode);
        Assert.Equal("desktop.item.not_found", await change.ErrorCodeAsync());
        Assert.Equal("desktop.item.not_found", await unconditional.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("desktop.item.not_found", await missing.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await using var check = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        Assert.Equal("{\"text\":\"secret\"}", await check.ExecuteScalarAsync<string>("SELECT data FROM desktop_items WHERE id = @otherItem", new { otherItem }));
    }

    [Fact]
    public async Task DeletingIgnoresItemsThatAreAlreadyGone()
    {
        await _client.SetupAdminAsync();
        var ids = (await ListAsync()).Select(item => item.GetProperty("id").GetInt64()).ToArray();

        foreach (var id in ids)
        {
            using var first = await DeleteAsync(id);
            using var again = await DeleteAsync(id);
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }
        using var unknown = await DeleteAsync(ids.Max() + 1000);

        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task TheDesktopHoldsALimitedNumberOfItems()
    {
        await _client.SetupAdminAsync();
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            await connection.ExecuteAsync(
                """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < @count)
                INSERT INTO desktop_items (user_id, kind, data, created_at, updated_at) SELECT (SELECT id FROM users WHERE username = 'admin'), 'note', '{"text":""}', 'x', 'x' FROM n
                """,
                new { count = DesktopItemService.MaxItems - DesktopItemService.DefaultAdminShortcuts.Count });
        }

        using var response = await _client.PostAsync("/api/desktop/items", new { kind = "note", text = "", column = 0, row = 0 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.JsonAsync();
        Assert.Equal("desktop.item.limit_reached", problem.GetProperty("code").GetString());
        Assert.Equal(DesktopItemService.MaxItems, problem.GetProperty("params").GetProperty("max").GetInt32());
    }

    [Fact]
    public async Task TheMigrationsGiveExistingUsersTheDefaultShortcutsAndTurnDisksIntoStorageForAdministrators()
    {
        var directory = Directory.CreateTempSubdirectory("pithyx-db-").FullName;
        try
        {
            var connections = new SqliteConnectionFactory(Path.Combine(directory, "pithyx.db"));
            var runner = new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance);
            await runner.RunAsync([.. Migration.Embedded().Where(migration => migration.Version < 4)], Token);
            await using (var connection = await connections.OpenAsync(Token))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES ('ada', 'x', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'), ('bob', 'x', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')");
            }

            await runner.RunAsync([.. Migration.Embedded().Where(migration => migration.Version < 10)], Token);
            await using (var before = await connections.OpenAsync(Token))
            {
                Assert.Equal(2, await before.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM desktop_items WHERE data = '{\"app\":\"disks\"}'"));
            }

            await runner.RunAsync([.. Migration.Embedded().Where(migration => migration.Version < 22)], Token);

            await using var check = await connections.OpenAsync(Token);
            var rows = await check.QueryAsync<(string User, string Data, long Row, long Version)>(
                "SELECT users.username, data, grid_row, version FROM desktop_items JOIN users ON users.id = user_id ORDER BY users.username, grid_row");
            Assert.Equal(
                [("ada", "{\"app\":\"storage\"}", 0L, 2L), ("ada", "{\"app\":\"about\"}", 1L, 1L), ("bob", "{\"app\":\"about\"}", 1L, 1L)],
                rows);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
