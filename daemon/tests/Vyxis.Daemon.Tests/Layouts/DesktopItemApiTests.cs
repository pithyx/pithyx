using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vyxis.Daemon.Database;
using Vyxis.Daemon.Layouts;
using Vyxis.Daemon.Tests.Support;
using static Vyxis.Daemon.Tests.Auth.AuthApi;

namespace Vyxis.Daemon.Tests.Layouts;

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

    private Task<HttpResponseMessage> ChangeAsync(params object[] changes) =>
        _client.PatchAsJsonAsync("/api/desktop/items", changes, Token);

    private async Task<long> InsertUserAsync(string username)
    {
        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES (@username, 'x', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z') RETURNING id",
            new { username });
    }

    [Fact]
    public async Task ANewUserStartsWithTheDefaultShortcutsInTheFirstColumn()
    {
        await _client.SetupAdminAsync();

        var items = await ListAsync();

        Assert.Equal(["disks", "about"], items.Select(item => item.GetProperty("app").GetString()));
        Assert.All(items, item => Assert.Equal("shortcut", item.GetProperty("kind").GetString()));
        Assert.Equal([0, 1], items.Select(item => item.GetProperty("row").GetInt32()));
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
    public async Task AChangeMovesItemsAndIncrementsTheirVersions()
    {
        await _client.SetupAdminAsync();
        var shortcut = (await ListAsync())[0];
        var note = await CreateNoteAsync();

        using var response = await ChangeAsync(
            new { id = shortcut.GetProperty("id").GetInt64(), version = 1, column = 5, row = 2 },
            new { id = note.GetProperty("id").GetInt64(), version = 1, text = "Done" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = (await response.JsonAsync()).EnumerateArray().ToArray();
        Assert.Equal((5, 2, 2L), (changed[0].GetProperty("column").GetInt32(), changed[0].GetProperty("row").GetInt32(), changed[0].GetProperty("version").GetInt64()));
        Assert.Equal(("Done", 3, 2L), (changed[1].GetProperty("text").GetString(), changed[1].GetProperty("column").GetInt32(), changed[1].GetProperty("version").GetInt64()));
    }

    [Fact]
    public async Task AStaleVersionRefusesTheWholeChange()
    {
        await _client.SetupAdminAsync();
        var items = await ListAsync();
        var first = items[0].GetProperty("id").GetInt64();
        var second = items[1].GetProperty("id").GetInt64();
        using (var other = await ChangeAsync(new { id = second, version = 1, row = 4 }))
        {
            other.EnsureSuccessStatusCode();
        }

        using var stale = await ChangeAsync(new { id = first, version = 1, column = 7 }, new { id = second, version = 1, row = 9 });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = await stale.JsonAsync();
        Assert.Equal("desktop.item.conflict", problem.GetProperty("code").GetString());
        Assert.Equal(second, problem.GetProperty("params").GetProperty("id").GetInt64());
        Assert.Equal(2, problem.GetProperty("params").GetProperty("version").GetInt64());
        var after = await ListAsync();
        Assert.Equal(0, after.Single(item => item.GetProperty("id").GetInt64() == first).GetProperty("column").GetInt32());
        Assert.Equal(4, after.Single(item => item.GetProperty("id").GetInt64() == second).GetProperty("row").GetInt32());
    }

    [Fact]
    public async Task ShortcutsGetAnOwnNameAndAnEmptyNameGoesBackToTheAppName()
    {
        await _client.SetupAdminAsync();
        var id = (await ListAsync())[0].GetProperty("id").GetInt64();

        using var renamed = await ChangeAsync(new { id, version = 1, name = "  My disks  " });
        using var reset = await ChangeAsync(new { id, version = 2, name = "" });

        Assert.Equal("My disks", (await renamed.JsonAsync())[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, (await reset.JsonAsync())[0].GetProperty("name").ValueKind);
    }

    [Theory]
    [InlineData("""{"kind":"shortcut","app":"disks","text":"no","column":0,"row":0}""")]
    [InlineData("""{"kind":"shortcut","app":"../x","column":0,"row":0}""")]
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
    public async Task FieldsOfTheOtherKindAreRefusedOnChange()
    {
        await _client.SetupAdminAsync();
        var shortcut = (await ListAsync())[0].GetProperty("id").GetInt64();
        var note = (await CreateNoteAsync()).GetProperty("id").GetInt64();

        using var textOnShortcut = await ChangeAsync(new { id = shortcut, version = 1, text = "x" });
        using var nameOnNote = await ChangeAsync(new { id = note, version = 1, name = "x" });
        using var twice = await ChangeAsync(new { id = note, version = 1, row = 1 }, new { id = note, version = 1, row = 2 });

        Assert.Equal("desktop.item.invalid", await textOnShortcut.ErrorCodeAsync());
        Assert.Equal("desktop.item.invalid", await nameOnNote.ErrorCodeAsync());
        Assert.Equal("desktop.item.invalid", await twice.ErrorCodeAsync());
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

        using var change = await ChangeAsync(new { id = otherItem, version = 1, text = "mine" });
        using var delete = await _client.PostAsync("/api/desktop/items/delete", new { ids = new[] { otherItem } });

        Assert.DoesNotContain(await ListAsync(), item => item.GetProperty("id").GetInt64() == otherItem);
        Assert.Equal(HttpStatusCode.NotFound, change.StatusCode);
        Assert.Equal("desktop.item.not_found", await change.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await using var check = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        Assert.Equal("{\"text\":\"secret\"}", await check.ExecuteScalarAsync<string>("SELECT data FROM desktop_items WHERE id = @otherItem", new { otherItem }));
    }

    [Fact]
    public async Task DeletingIgnoresItemsThatAreAlreadyGone()
    {
        await _client.SetupAdminAsync();
        var ids = (await ListAsync()).Select(item => item.GetProperty("id").GetInt64()).ToArray();

        using var first = await _client.PostAsync("/api/desktop/items/delete", new { ids });
        using var again = await _client.PostAsync("/api/desktop/items/delete", new { ids });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
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
                INSERT INTO desktop_items (user_id, kind, data, created_at, updated_at) SELECT 1, 'note', '{"text":""}', 'x', 'x' FROM n
                """,
                new { count = DesktopItemService.MaxItems - DesktopItemService.DefaultShortcuts.Count });
        }

        using var response = await _client.PostAsync("/api/desktop/items", new { kind = "note", text = "", column = 0, row = 0 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.JsonAsync();
        Assert.Equal("desktop.item.limit_reached", problem.GetProperty("code").GetString());
        Assert.Equal(DesktopItemService.MaxItems, problem.GetProperty("params").GetProperty("max").GetInt32());
    }

    [Fact]
    public async Task TheMigrationGivesExistingUsersTheDefaultShortcuts()
    {
        var directory = Directory.CreateTempSubdirectory("vyxis-db-").FullName;
        try
        {
            var connections = new SqliteConnectionFactory(Path.Combine(directory, "vyxis.db"));
            var runner = new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance);
            await runner.RunAsync([.. Migration.Embedded().Where(migration => migration.Version < 4)], Token);
            await using (var connection = await connections.OpenAsync(Token))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES ('ada', 'x', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')");
            }

            await runner.RunAsync(Migration.Embedded(), Token);

            await using var check = await connections.OpenAsync(Token);
            var rows = await check.QueryAsync<(string Data, long Row)>("SELECT data, grid_row FROM desktop_items ORDER BY grid_row");
            Assert.Equal([("{\"app\":\"disks\"}", 0L), ("{\"app\":\"about\"}", 1L)], rows);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
