using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Database;

/// <summary>Migration 0022 (A3): the system apps' short ids become <c>org.pithyx.*</c> wherever an app id is stored.</summary>
public sealed class SystemAppIdMigrationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-app-ids-").FullName;

    private string DatabasePath => Path.Combine(_directory, "pithyx.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<SqliteConnection> UpgradeAsync()
    {
        await LegacyAppIdDatabase.CreateAsync(DatabasePath, Token);
        var connections = new SqliteConnectionFactory(DatabasePath);
        await new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
        return await connections.OpenAsync(Token);
    }

    [Fact]
    public async Task GrantsOfUsersAndGroupsNameTheNewIds()
    {
        await using var connection = await UpgradeAsync();

        var grants = await connection.QueryAsync<(string Kind, long Subject, string Resource, string Level)>(
            "SELECT subject_kind, subject_id, resource, level FROM grants ORDER BY subject_kind, subject_id, resource");
        Assert.Equal(
            [
                ("group", 100L, "app:org.pithyx.viewer", "allow"),
                ("group", 30000L, "app:org.pithyx.files", "allow"),
                ("user", 1L, "right:admin", "allow"),
                ("user", 1L, "right:desktopLogin", "allow"),
                ("user", 2L, "app:org.pithyx.demo", "allow"),
                ("user", 2L, "app:org.pithyx.files", "allow"),
                ("user", 2L, "app:org.pithyx.viewer", "allow"),
                ("user", 2L, "protocol:smb", "allow"),
                ("user", 2L, "right:desktopLogin", "allow"),
            ],
            grants);
    }

    [Fact]
    public async Task WindowPreferencesMoveToTheNewIdsAndAnExistingNewOneWins()
    {
        await using var connection = await UpgradeAsync();

        var preferences = await connection.QueryAsync<(long User, string App, long Width, long Height)>(
            "SELECT user_id, app_id, width, height FROM window_preferences ORDER BY user_id, app_id");
        Assert.Equal(
            [
                (1L, "org.pithyx.files", 1000L, 700L),
                (1L, "org.pithyx.storage", 1100L, 720L),
                (2L, "com.mueller.chat", 400L, 300L),
                (2L, "org.pithyx.files", 900L, 600L),
                (2L, "org.pithyx.viewer", 700L, 500L),
            ],
            preferences);
    }

    [Fact]
    public async Task DesktopShortcutsNameTheNewIdsWithANewVersionAndKeepTheirLabels()
    {
        await using var connection = await UpgradeAsync();

        var items = await connection.QueryAsync<(long Id, string Kind, string Data, long Version)>(
            "SELECT id, kind, data, version FROM desktop_items ORDER BY id");
        Assert.Equal(
            [
                (1L, "shortcut", """{"app":"org.pithyx.storage"}""", 2L),
                (2L, "shortcut", """{"app":"org.pithyx.files","name":"Büro"}""", 4L),
                (3L, "shortcut", """{"app":"org.pithyx.about"}""", 2L),
                (4L, "note", """{"text":"files"}""", 1L),
                (5L, "shortcut", """{"app":"com.mueller.chat"}""", 1L),
            ],
            items);
    }

    [Fact]
    public async Task LayoutsRenameTheAppsOfTheirWindowsAndKeepEverythingElse()
    {
        await using var connection = await UpgradeAsync();

        var layouts = (await connection.QueryAsync<(string Device, string Layout, long Version, long Windows)>(
            "SELECT device_id, layout, version, window_count FROM desktop_layouts ORDER BY device_id")).ToList();
        Assert.Equal(("desk", LegacyAppIdDatabase.AdminLayout, 2L, 0L), layouts[0]);
        var (device, json, version, windows) = layouts[1];
        Assert.Equal(("laptop", 6L, 4L), (device, version, windows));
        // Text is kept as it was, not escaped.
        Assert.Contains("\"title\":\"Büro\"", json, StringComparison.Ordinal);

        var expected = JsonNodeOf(LegacyAppIdDatabase.BobLayout);
        expected["windows"]![0]!["app"] = "org.pithyx.files";
        expected["windows"]![2]!["app"] = "org.pithyx.about";
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, JsonNodeOf(json)), json);
    }

    [Fact]
    public async Task NotificationsOfTheSystemAppsNameTheNewIds()
    {
        await using var connection = await UpgradeAsync();

        var notifications = await connection.QueryAsync<(long Id, string? App, string Params)>("SELECT id, app, params FROM notifications ORDER BY id");
        Assert.Equal(
            [
                (1L, "org.pithyx.demo", """{"title":"Hello","body":""}"""),
                (2L, null, """{"kind":"files.copy"}"""),
                (3L, "com.mueller.chat", """{"title":"Hi","body":""}"""),
            ],
            notifications);
    }

    [Fact]
    public async Task NoShortIdOfASystemAppIsLeft()
    {
        await using var connection = await UpgradeAsync();

        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            """
            SELECT (SELECT count(*) FROM grants WHERE resource GLOB 'app:*' AND resource NOT GLOB 'app:*.*.*')
                + (SELECT count(*) FROM window_preferences WHERE app_id NOT GLOB '*.*.*')
                + (SELECT count(*) FROM desktop_items WHERE kind = 'shortcut' AND json_extract(data, '$.app') NOT GLOB '*.*.*')
                + (SELECT count(*) FROM notifications WHERE app NOT GLOB '*.*.*')
                + (SELECT count(*) FROM desktop_layouts, json_each(layout, '$.windows') AS w
                    WHERE json_extract(w.value, '$.app') IN ('files', 'viewer', 'storage', 'settings', 'monitor', 'logs', 'about', 'demo'))
            """));
    }

    private static System.Text.Json.Nodes.JsonNode JsonNodeOf(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json) ?? throw new JsonException("Empty JSON.");
}
