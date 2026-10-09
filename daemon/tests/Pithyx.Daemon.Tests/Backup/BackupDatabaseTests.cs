using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Backup;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Backup;

/// <summary>Restoring categories of a backup's database into the live one (D293).</summary>
public sealed class BackupDatabaseTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pithyx-backup-").FullName;
    private SqliteConnection _live = null!;
    private SqliteConnection _backup = null!;

    private string BackupPath => Path.Combine(_directory, "backup.db");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _live = await MigratedAsync(Path.Combine(_directory, "live.db"));
        _backup = await MigratedAsync(BackupPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _live.DisposeAsync();
        await _backup.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static async Task<SqliteConnection> MigratedAsync(string path)
    {
        await new MigrationRunner(new SqliteConnectionFactory(path), TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
        await connection.OpenAsync(Token);
        return connection;
    }

    private static Task<long> UserAsync(SqliteConnection connection, string name, long id) =>
        TestUsers.InsertAsync(connection, name, id: id, uid: id);

    private static async Task ShareAsync(SqliteConnection connection, long id, string name)
    {
        await connection.ExecuteAsync(
            "INSERT INTO shares (id, name, path, created_at, updated_at, acl) VALUES (@id, @name, @path, 'x', 'x', 'u::rwx')",
            new { id, name, path = $"/tank/pithyx/shares/{name.ToLowerInvariant()}" });
    }

    private static async Task GrantAsync(SqliteConnection connection, string kind, long subject, string resource, string level = "allow") =>
        await connection.ExecuteAsync(
            "INSERT INTO grants (subject_kind, subject_id, resource, level) VALUES (@kind, @subject, @resource, @level)",
            new { kind, subject, resource, level });

    private static async Task GroupAsync(SqliteConnection connection, long id, string name, params long[] members)
    {
        await connection.ExecuteAsync("INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (@id, 'group', @name, 'x')", new { id, name });
        await connection.ExecuteAsync("INSERT INTO groups (id, name, created_at, updated_at) VALUES (@id, @name, 'x', 'x')", new { id, name });
        foreach (var member in members)
        {
            await connection.ExecuteAsync("INSERT INTO group_members (group_id, user_id) VALUES (@id, @member)", new { id, member });
        }
    }

    private static async Task AppAsync(SqliteConnection connection, string id, long uid)
    {
        await connection.ExecuteAsync("INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (@uid, 'app', @id, 'x')", new { uid, id });
        await connection.ExecuteAsync(
            """
            INSERT INTO apps (id, version, manifest, source, icon, package_sha256, uid, installed_at, updated_at)
            VALUES (@id, '1.0.0', '{}', 'sideload', x'3c7376672f3e', @sha, @uid, 'x', 'x')
            """,
            new { id, uid, sha = new string('a', 64) });
    }

    private async Task<List<string>> GrantsAsync() =>
        [.. await _live.QueryAsync<string>("SELECT subject_kind || ':' || subject_id || ' ' || resource || ' ' || level FROM grants ORDER BY 1")];

    private Task RestoreAsync(params BackupCategory[] categories) =>
        BackupDatabase.RestoreAsync(_live, BackupPath, categories.ToHashSet(), Token);

    [Fact]
    public async Task EveryTableBelongsToACategoryOrIsRuntimeState()
    {
        var tables = (await _live.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")).ToHashSet();
        var classified = BackupDatabase.Tables.Values.SelectMany(names => names).Concat(BackupDatabase.Special).Concat(BackupDatabase.NotBackedUp).ToList();

        // A new table needs a decision: which category restores it, or that it is runtime state.
        Assert.Empty(tables.Except(classified));
        Assert.Empty(classified.Except(tables));
        Assert.Equal(classified.Count, classified.Distinct().Count());
    }

    [Fact]
    public async Task RestoringEverythingBringsBackTheConfigurationAndSignsEverybodyOut()
    {
        await UserAsync(_backup, "ada", 20000);
        await GroupAsync(_backup, 30000, "media", 20000);
        await ShareAsync(_backup, 1, "Media");
        await GrantAsync(_backup, "user", 20000, "share:1", "readWrite");
        await GrantAsync(_backup, "group", 30000, "share:1", "read");
        await _backup.ExecuteAsync("UPDATE file_services SET smb_workgroup = 'HOME'; INSERT INTO system_region (id, language, region, updated_at) VALUES (1, 'de', 'AT', 'x')");
        await _backup.ExecuteAsync("INSERT INTO settings (key, value) VALUES ('recovery_key_hash', 'backup'), ('logs.settings', '{}')");

        var bob = await UserAsync(_live, "bob", 20001);
        await _live.ExecuteAsync("INSERT INTO settings (key, value) VALUES ('recovery_key_hash', 'live'), ('other', 'kept')");
        await _live.ExecuteAsync("INSERT INTO sessions (id, user_id, created_at, last_seen_at, expires_at, absolute_expires_at) VALUES (randomblob(32), @bob, 'x', 'x', 'x', 'x')", new { bob });
        await _live.ExecuteAsync("INSERT INTO notifications (user_id, code, params, tone, created_at) VALUES (@bob, 'c', '{}', 'info', 'x')", new { bob });

        await RestoreAsync(Enum.GetValues<BackupCategory>());

        Assert.Equal(["ada"], await _live.QueryAsync<string>("SELECT username FROM users"));
        Assert.Equal(["users", "media"], await _live.QueryAsync<string>("SELECT name FROM groups ORDER BY id"));
        Assert.Equal(["Media"], await _live.QueryAsync<string>("SELECT name FROM shares"));
        Assert.Equal(["group:30000 share:1 read", "user:20000 right:desktopLogin allow", "user:20000 share:1 readWrite"], await GrantsAsync());
        Assert.Equal("HOME", await _live.ExecuteScalarAsync<string>("SELECT smb_workgroup FROM file_services"));
        Assert.Equal("AT", await _live.ExecuteScalarAsync<string>("SELECT region FROM system_region"));
        Assert.Equal(["logs.settings={}", "other=kept", "recovery_key_hash=backup", "store.channel=stable", "store.updates.policy=notify", "store.updates.time=03:00"], await _live.QueryAsync<string>("SELECT key || '=' || value FROM settings ORDER BY key"));
        Assert.Equal(0, await _live.ExecuteScalarAsync<int>("SELECT count(*) FROM sessions"));
        Assert.Equal(0, await _live.ExecuteScalarAsync<int>("SELECT count(*) FROM notifications"));
        // Numbers are never given out twice: bob's uid stays allocated although bob is gone.
        Assert.Equal([20000, 20001, 30000], await _live.QueryAsync<long>("SELECT id FROM id_allocations ORDER BY id"));
        // The ACL is set again from the restored rights.
        Assert.Null(await _live.ExecuteScalarAsync<string?>("SELECT acl FROM shares"));
        Assert.Equal(1, await _live.ExecuteScalarAsync<int>("PRAGMA foreign_keys"));
    }

    [Fact]
    public async Task RestoringUsersKeepsTheirRightsOnTheSharedFoldersOfThisBox()
    {
        await UserAsync(_backup, "ada", 20000);
        await GrantAsync(_backup, "user", 20000, "protocol:sftp");
        await ShareAsync(_backup, 1, "Old");
        await GrantAsync(_backup, "user", 20000, "share:1", "readWrite");

        var ada = await UserAsync(_live, "ada", 20000);
        var cara = await UserAsync(_live, "cara", 20002);
        await ShareAsync(_live, 1, "Live");
        await GrantAsync(_live, "user", ada, "share:1", "read");
        await GrantAsync(_live, "user", cara, "share:1", "readWrite");
        await GrantAsync(_live, "user", ada, "protocol:smb");
        await _live.ExecuteAsync("INSERT INTO jobs (user_id, kind, state, request_id, created_at) VALUES (@ada, 'k', 'succeeded', 'r', 'x'), (@cara, 'k', 'succeeded', 'r', 'x')", new { ada, cara });

        await RestoreAsync(BackupCategory.Users);

        // The shared folder of this box and ada's right on it stay; her other rights come from the backup.
        Assert.Equal(["Live"], await _live.QueryAsync<string>("SELECT name FROM shares"));
        Assert.Equal(["user:20000 protocol:sftp allow", "user:20000 right:desktopLogin allow", "user:20000 share:1 read"], await GrantsAsync());
        Assert.Equal([ada], await _live.QueryAsync<long>("SELECT user_id FROM jobs"));
    }

    [Fact]
    public async Task RestoringUsersKeepsRightsOnSystemAppsAndOnAppsInstalledAfterTheRestoreOnly()
    {
        await GrantAsync(_backup, "group", 100, "app:org.pithyx.files");
        await GrantAsync(_backup, "group", 100, "app:com.example.gone");
        await GrantAsync(_backup, "group", 100, "app:com.example.chat");
        await AppAsync(_live, "com.example.chat", 40000);

        await RestoreAsync(BackupCategory.Users);

        // com.example.gone was uninstalled after the backup; its right must not wait for an app installed later.
        Assert.Equal(["group:100 app:com.example.chat allow", "group:100 app:org.pithyx.files allow"], await GrantsAsync());
    }

    [Fact]
    public async Task ARightOfAnotherUserWithTheSameIdIsDropped()
    {
        await UserAsync(_backup, "ada", 20000);
        var eve = await UserAsync(_live, "eve", 20000);
        await ShareAsync(_live, 1, "Live");
        await GrantAsync(_live, "user", eve, "share:1", "readWrite");
        await _live.ExecuteAsync("INSERT INTO notifications (user_id, code, params, tone, created_at) VALUES (@eve, 'c', '{}', 'info', 'x')", new { eve });

        await RestoreAsync(BackupCategory.Users);

        Assert.Equal(["user:20000 right:desktopLogin allow"], await GrantsAsync());
        Assert.Equal(0, await _live.ExecuteScalarAsync<int>("SELECT count(*) FROM notifications"));
    }

    [Fact]
    public async Task RestoringSharedFoldersKeepsRightsOfUsersThatStillExist()
    {
        await UserAsync(_backup, "ada", 20000);
        await UserAsync(_backup, "zed", 20005);
        await ShareAsync(_backup, 1, "Media");
        await ShareAsync(_backup, 2, "Backups");
        await _backup.ExecuteAsync("INSERT INTO id_allocations (id, kind, name, allocated_at) VALUES (30010, 'group', 'share-1-read', 'x'), (30011, 'group', 'share-1-write', 'x')");
        await _backup.ExecuteAsync("UPDATE shares SET read_gid = 30010, write_gid = 30011 WHERE id = 1");
        await GrantAsync(_backup, "user", 20000, "share:1", "readWrite");
        await GrantAsync(_backup, "user", 20005, "share:2", "read");

        var ada = await UserAsync(_live, "ada", 20000);
        await ShareAsync(_live, 1, "Media");
        await ShareAsync(_live, 3, "Scratch");
        await GrantAsync(_live, "user", ada, "share:3", "read");
        await _live.ExecuteAsync(
            "INSERT INTO trash_items (share_id, original_path, kind, size, deleted_by, deleted_at) VALUES (1, 'a.txt', 'file', 1, @ada, 'x'), (3, 'b.txt', 'file', 1, @ada, 'x')",
            new { ada });

        await RestoreAsync(BackupCategory.SharedFolders);

        Assert.Equal(["ada"], await _live.QueryAsync<string>("SELECT username FROM users"));
        Assert.Equal(["Media", "Backups"], await _live.QueryAsync<string>("SELECT name FROM shares ORDER BY id"));
        Assert.Equal(["user:20000 right:desktopLogin allow", "user:20000 share:1 readWrite"], await GrantsAsync());
        // The recycle bin stays with the shared folder it belongs to; the share gids of the backup are allocated.
        Assert.Equal(["a.txt"], await _live.QueryAsync<string>("SELECT original_path FROM trash_items"));
        Assert.Equal([30010, 30011], await _live.QueryAsync<long>("SELECT id FROM id_allocations WHERE kind = 'group' ORDER BY id"));
    }

    [Fact]
    public async Task StorageAndSystemLeaveUsersAndSharedFoldersAlone()
    {
        await _backup.ExecuteAsync("INSERT INTO storage_schedules (kind, target, frequency, updated_at) VALUES ('scrub', 'tank', 'monthly', 'x')");
        await _backup.ExecuteAsync("INSERT INTO snapshot_schedules (dataset, kind, enabled, keep, updated_at) VALUES ('tank/pithyx/shares/media', 'daily', 1, 7, 'x')");
        var ada = await UserAsync(_live, "ada", 20000);
        await ShareAsync(_live, 1, "Media");
        await GrantAsync(_live, "user", ada, "share:1", "read");
        await _live.ExecuteAsync("INSERT INTO storage_schedules (kind, target, frequency, updated_at) VALUES ('scrub', 'old', 'weekly', 'x')");

        await RestoreAsync(BackupCategory.Storage, BackupCategory.System);

        Assert.Equal(["tank monthly"], await _live.QueryAsync<string>("SELECT target || ' ' || frequency FROM storage_schedules"));
        Assert.Equal(1, await _live.ExecuteScalarAsync<int>("SELECT count(*) FROM snapshot_schedules"));
        Assert.Equal(["user:20000 right:desktopLogin allow", "user:20000 share:1 read"], await GrantsAsync());
        Assert.Equal("u::rwx", await _live.ExecuteScalarAsync<string>("SELECT acl FROM shares"));
    }

    [Fact]
    public async Task ARestoreThatWouldBreakAKeyChangesNothing()
    {
        await _backup.ExecuteAsync("PRAGMA foreign_keys = OFF; INSERT INTO groups (id, name, created_at, updated_at) VALUES (30000, 'media', 'x', 'x'); INSERT INTO group_members (group_id, user_id) VALUES (30000, 29999)");
        await UserAsync(_live, "ada", 20000);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RestoreAsync(BackupCategory.Users));

        Assert.Equal(["ada"], await _live.QueryAsync<string>("SELECT username FROM users"));
        Assert.Equal(["user:20000 right:desktopLogin allow"], await GrantsAsync());
        Assert.Equal(1, await _live.ExecuteScalarAsync<int>("PRAGMA foreign_keys"));
    }

    [Fact]
    public async Task TheBackupCopyLeavesRuntimeStateBehind()
    {
        var ada = await UserAsync(_live, "ada", 20000);
        await _live.ExecuteAsync("INSERT INTO sessions (id, user_id, created_at, last_seen_at, expires_at, absolute_expires_at) VALUES (randomblob(32), @ada, 'x', 'x', 'x', 'x')", new { ada });
        await _live.ExecuteAsync("INSERT INTO jobs (user_id, kind, state, request_id, created_at) VALUES (@ada, 'k', 'succeeded', 'r', 'x')", new { ada });
        var copy = Path.Combine(_directory, "copy.db");

        await BackupDatabase.CopyAsync(_live, copy, Token);

        await using var connection = BackupDatabase.Open(copy);
        await connection.OpenAsync(Token);
        Assert.Equal(["ada"], await connection.QueryAsync<string>("SELECT username FROM users"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sessions"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM jobs"));
        Assert.Equal((1, 0, 0), await BackupDatabase.CountAsync(connection, Token));
    }
}
