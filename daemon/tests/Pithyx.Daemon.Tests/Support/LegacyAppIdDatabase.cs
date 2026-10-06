using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.Tests.Support;

/// <summary>
/// A database at schema 0021, before the system apps got their <c>org.pithyx.*</c> ids (migration 0022), with every
/// kind of row that names an app: grants of users and groups, window preferences, desktop shortcuts, layouts and
/// notifications. The users are <c>admin</c> (id 1, an administrator) and <c>bob</c> (id 2).
/// </summary>
public static class LegacyAppIdDatabase
{
    public const int SchemaVersion = 21;

    /// <summary>Layout of bob's device <c>laptop</c>: Files with a folder, a third-party app, About and the gone Disks app.</summary>
    public const string BobLayout =
        """{"version":1,"viewport":{"width":1440,"height":900},"taskbar":"bottom","windows":[{"id":"w1","app":"files","screen":1,"title":"Büro","rect":{"x":10,"y":20,"width":900,"height":600},"placement":{"kind":"free"},"minimized":false,"args":{"share":3,"path":"/Büro"}},{"id":"w2","app":"com.mueller.chat","screen":1,"title":null,"rect":{"x":40,"y":50,"width":500,"height":400},"placement":{"kind":"maximized"},"minimized":false,"args":null},{"id":"w3","app":"about","screen":1,"title":null,"rect":{"x":60,"y":70,"width":560,"height":620},"placement":{"kind":"snapped","zone":"left"},"minimized":true,"args":{"tab":"licenses"}},{"id":"w4","app":"disks","screen":1,"title":null,"rect":{"x":0,"y":0,"width":300,"height":300},"placement":{"kind":"free"},"minimized":false,"args":null}],"order":["w4","w2","w1","w3"],"focused":"w1"}""";

    /// <summary>Layout of the administrator's device <c>desk</c>, without windows.</summary>
    public const string AdminLayout =
        """{"version":1,"viewport":{"width":1920,"height":1080},"taskbar":"left","windows":[],"order":[],"focused":null}""";

    /// <summary>Creates the database at <paramref name="path"/> with schema 0021 and the rows above.</summary>
    public static async Task CreateAsync(string path, CancellationToken cancellationToken)
    {
        var connections = new SqliteConnectionFactory(path);
        var runner = new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance);
        await runner.RunAsync([.. Migration.Embedded().Where(migration => migration.Version <= SchemaVersion)], cancellationToken);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO users (id, username, password_hash, created_at, updated_at) VALUES
                    (1, 'admin', 'x', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
                    (2, 'bob', 'x', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                INSERT INTO groups (id, name, created_at, updated_at) VALUES (30000, 'staff', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                INSERT INTO group_members (group_id, user_id) VALUES (30000, 2);
                INSERT INTO grants (subject_kind, subject_id, resource, level) VALUES
                    ('user', 1, 'right:admin', 'allow'),
                    ('user', 1, 'right:desktopLogin', 'allow'),
                    ('user', 2, 'right:desktopLogin', 'allow'),
                    ('user', 2, 'protocol:smb', 'allow'),
                    ('user', 2, 'app:files', 'allow'),
                    ('user', 2, 'app:viewer', 'allow'),
                    ('user', 2, 'app:demo', 'allow'),
                    ('group', 30000, 'app:files', 'allow'),
                    ('group', 100, 'app:viewer', 'allow');
                INSERT INTO window_preferences (user_id, app_id, width, height, updated_at) VALUES
                    (1, 'files', 800, 500, '2026-10-01T00:00:00Z'),
                    (1, 'org.pithyx.files', 1000, 700, '2026-10-02T00:00:00Z'),
                    (1, 'storage', 1100, 720, '2026-10-01T00:00:00Z'),
                    (2, 'files', 900, 600, '2026-10-01T00:00:00Z'),
                    (2, 'viewer', 700, 500, '2026-10-01T00:00:00Z'),
                    (2, 'com.mueller.chat', 400, 300, '2026-10-01T00:00:00Z');
                INSERT INTO desktop_items (id, user_id, kind, data, grid_column, grid_row, version, created_at, updated_at) VALUES
                    (1, 1, 'shortcut', '{"app":"storage"}', 0, 0, 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
                    (2, 2, 'shortcut', '{"app":"files","name":"Büro"}', 0, 0, 3, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
                    (3, 2, 'shortcut', '{"app":"about"}', 0, 1, 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
                    (4, 2, 'note', '{"text":"files"}', 1, 0, 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
                    (5, 2, 'shortcut', '{"app":"com.mueller.chat"}', 1, 1, 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                INSERT INTO desktop_layouts (user_id, device_id, schema_version, layout, window_count, updated_at, expires_at, version) VALUES
                    (2, 'laptop', 1, @BobLayout, 4, '2026-10-01T00:00:00Z', '2099-01-01T00:00:00Z', 5),
                    (1, 'desk', 1, @AdminLayout, 0, '2026-10-01T00:00:00Z', '2099-01-01T00:00:00Z', 2);
                INSERT INTO notifications (id, user_id, code, params, tone, app, created_at) VALUES
                    (1, 2, 'app.message', '{"title":"Hello","body":""}', 'info', 'demo', '2026-10-01T00:00:00Z'),
                    (2, 2, 'jobs.job.failed', '{"kind":"files.copy"}', 'critical', NULL, '2026-10-01T00:00:00Z'),
                    (3, 2, 'app.message', '{"title":"Hi","body":""}', 'info', 'com.mueller.chat', '2026-10-01T00:00:00Z');
                """,
                new { BobLayout, AdminLayout });
        }
        SqliteConnection.ClearAllPools();
    }
}
