using System.Collections.Frozen;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pithyx.Daemon.Backup;

/// <summary>A part of the configuration that can be restored on its own.</summary>
public enum BackupCategory
{
    /// <summary>Users, groups, passwords (also for SMB and SFTP), 2FA, the recovery key and personal settings.</summary>
    Users,

    /// <summary>Shared folders with their rights, and the file service settings.</summary>
    SharedFolders,

    /// <summary>Restore points, scrub and self-test schedules.</summary>
    Storage,

    /// <summary>System language, region, time zone, time servers and log retention.</summary>
    System,

    /// <summary>The TLS certificates and the SSH host keys.</summary>
    Certificates,
}

/// <summary>
/// Which table of <c>pithyx.db</c> belongs to which category, and the restore of the chosen categories inside one
/// transaction (D293). A category replaces its tables completely; nothing is merged. Rights (<c>grants</c>) connect
/// users and groups with shared folders: a right survives a restore when the user or group and the shared folder it
/// names still have the same id and name afterwards. Allocated uids and gids are merged, so no number is ever given
/// out twice. Runtime state (sessions, jobs, notifications, the recycle bin) is never part of a backup.
/// </summary>
public static class BackupDatabase
{
    public static readonly FrozenDictionary<BackupCategory, string[]> Tables = new Dictionary<BackupCategory, string[]>
    {
        [BackupCategory.Users] =
        [
            "users", "recovery_codes", "user_appearance", "window_preferences", "desktop_layouts", "desktop_items",
            "groups", "group_members", "storage_alert_mutes",
        ],
        [BackupCategory.SharedFolders] = ["shares", "file_services"],
        [BackupCategory.Storage] = ["snapshot_schedules", "storage_schedules"],
        [BackupCategory.System] = ["system_region"],
        [BackupCategory.Certificates] = [],
    }.ToFrozenDictionary();

    /// <summary>Rows of the <c>settings</c> table by category.</summary>
    public static readonly FrozenDictionary<BackupCategory, string[]> SettingKeys = new Dictionary<BackupCategory, string[]>
    {
        [BackupCategory.Users] = [Auth.AuthService.RecoveryKeySetting],
        [BackupCategory.System] = [Logs.LogService.SettingsKey],
    }.ToFrozenDictionary();

    /// <summary>Tables a restore handles on their own: rights are rebuilt, allocations merged, settings go by key.</summary>
    public static readonly FrozenSet<string> Special = new[] { "grants", "id_allocations", "settings", "schema_version" }.ToFrozenSet();

    /// <summary>Runtime state: emptied in the backup's copy and never restored.</summary>
    public static readonly FrozenSet<string> NotBackedUp = new[]
    {
        "sessions", "jobs", "notifications", "storage_alert_notices", "storage_schedule_runs", "trash_items",
    }.ToFrozenSet();

    /// <summary>Copies the live database to <paramref name="path"/> without its runtime state.</summary>
    public static async Task CopyAsync(SqliteConnection connection, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await connection.ExecuteAsync(new CommandDefinition("VACUUM INTO @path", new { path }, cancellationToken: cancellationToken));
        await using var copy = Open(path);
        await copy.OpenAsync(cancellationToken);
        foreach (var table in NotBackedUp.Order(StringComparer.Ordinal))
        {
            await copy.ExecuteAsync(new CommandDefinition($"DELETE FROM {table}", cancellationToken: cancellationToken));
        }
        await copy.ExecuteAsync(new CommandDefinition("VACUUM", cancellationToken: cancellationToken));
    }

    /// <summary>A connection to a backup's database file outside the pool, so the file can be deleted right after.</summary>
    public static SqliteConnection Open(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());

    /// <summary>
    /// Replaces the chosen categories of the live database with those of the backup at <paramref name="backupPath"/>,
    /// which already has the live schema. Signs everybody out when users are restored. Throws and changes nothing
    /// when the result would break a foreign key.
    /// </summary>
    public static async Task RestoreAsync(SqliteConnection connection, string backupPath, IReadOnlySet<BackupCategory> categories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(categories);
        var users = categories.Contains(BackupCategory.Users);
        var shares = categories.Contains(BackupCategory.SharedFolders);
        // Rows are deleted and inserted table by table; the keys are checked once at the end, before the commit.
        await connection.ExecuteAsync("PRAGMA foreign_keys = OFF");
        await connection.ExecuteAsync("ATTACH DATABASE @backupPath AS backup", new { backupPath });
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await CaptureAsync(connection, transaction);
            foreach (var category in categories.Order())
            {
                foreach (var table in Tables[category])
                {
                    await ReplaceAsync(connection, transaction, table);
                }
                foreach (var key in SettingKeys.GetValueOrDefault(category) ?? [])
                {
                    await connection.ExecuteAsync("DELETE FROM main.settings WHERE key = @key", new { key }, transaction);
                    await connection.ExecuteAsync("INSERT INTO main.settings (key, value) SELECT key, value FROM backup.settings WHERE key = @key", new { key }, transaction);
                }
            }
            if (users || shares)
            {
                await connection.ExecuteAsync("INSERT OR IGNORE INTO main.id_allocations (id, kind, name, allocated_at) SELECT id, kind, name, allocated_at FROM backup.id_allocations", transaction: transaction);
            }
            await connection.ExecuteAsync(RebuildGrants, new { users, shares }, transaction);
            if (users)
            {
                await connection.ExecuteAsync(
                    """
                    DELETE FROM main.sessions;
                    DELETE FROM main.jobs WHERE NOT EXISTS (SELECT 1 FROM main.users u JOIN temp.restore_users o ON o.id = u.id AND o.username = u.username WHERE u.id = jobs.user_id);
                    DELETE FROM main.notifications WHERE NOT EXISTS (SELECT 1 FROM main.users u JOIN temp.restore_users o ON o.id = u.id AND o.username = u.username WHERE u.id = notifications.user_id);
                    UPDATE main.trash_items SET deleted_by = NULL WHERE NOT EXISTS (SELECT 1 FROM main.users u JOIN temp.restore_users o ON o.id = u.id AND o.username = u.username WHERE u.id = trash_items.deleted_by);
                    """,
                    transaction: transaction);
            }
            if (shares)
            {
                // The recycle bin lists files on the pools; it stays only with the shared folder it belongs to.
                await connection.ExecuteAsync(
                    """
                    DELETE FROM main.trash_items WHERE NOT EXISTS (SELECT 1 FROM main.shares s JOIN temp.restore_shares o ON o.id = s.id AND o.name = s.name WHERE s.id = trash_items.share_id);
                    UPDATE main.shares SET acl = NULL;
                    """,
                    transaction: transaction);
            }
            // The first column of each row names the table with a broken key.
            var broken = (await connection.QueryAsync<string>("PRAGMA main.foreign_key_check", transaction: transaction)).Distinct(StringComparer.Ordinal).ToList();
            if (broken.Count > 0)
            {
                throw new InvalidOperationException($"The restore would break foreign keys in {string.Join(", ", broken)}.");
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await connection.ExecuteAsync("DROP TABLE IF EXISTS temp.restore_grants; DROP TABLE IF EXISTS temp.restore_users; DROP TABLE IF EXISTS temp.restore_shares");
            await connection.ExecuteAsync("DETACH DATABASE backup");
            await connection.ExecuteAsync("PRAGMA foreign_keys = ON");
        }
    }

    /// <summary>What a backup holds, for the restore dialog.</summary>
    public static async Task<(int Users, int Groups, int SharedFolders)> CountAsync(SqliteConnection backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return await backup.QuerySingleAsync<(int, int, int)>(new CommandDefinition(
            "SELECT (SELECT count(*) FROM users), (SELECT count(*) FROM groups WHERE builtin = 0), (SELECT count(*) FROM shares)",
            cancellationToken: cancellationToken));
    }

    // The rights of both sides with the names their subject and shared folder had, and the users and shared folders
    // before the restore, so rows that point at a reused id can be told apart afterwards.
    private static async Task CaptureAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction) =>
        await connection.ExecuteAsync(
            """
            CREATE TEMP TABLE restore_grants AS
            SELECT 'main' AS side, g.subject_kind, g.subject_id, g.resource, g.level,
                CASE g.subject_kind
                    WHEN 'user' THEN (SELECT username FROM main.users WHERE id = g.subject_id)
                    WHEN 'group' THEN (SELECT name FROM main.groups WHERE id = g.subject_id)
                END AS subject_name,
                CASE WHEN g.resource GLOB 'share:*' THEN (SELECT name FROM main.shares WHERE id = CAST(substr(g.resource, 7) AS INTEGER)) END AS share_name
            FROM main.grants g
            UNION ALL
            SELECT 'backup', g.subject_kind, g.subject_id, g.resource, g.level,
                CASE g.subject_kind
                    WHEN 'user' THEN (SELECT username FROM backup.users WHERE id = g.subject_id)
                    WHEN 'group' THEN (SELECT name FROM backup.groups WHERE id = g.subject_id)
                END,
                CASE WHEN g.resource GLOB 'share:*' THEN (SELECT name FROM backup.shares WHERE id = CAST(substr(g.resource, 7) AS INTEGER)) END
            FROM backup.grants g;
            CREATE TEMP TABLE restore_users AS SELECT id, username FROM main.users;
            CREATE TEMP TABLE restore_shares AS SELECT id, name FROM main.shares;
            """,
            transaction: transaction);

    private static async Task ReplaceAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string table)
    {
        var columns = string.Join(", ", (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info(@table, 'main')", new { table }, transaction))
            .Select(column => $"\"{column}\""));
        await connection.ExecuteAsync($"DELETE FROM main.{table}", transaction: transaction);
        await connection.ExecuteAsync($"INSERT INTO main.{table} ({columns}) SELECT {columns} FROM backup.{table}", transaction: transaction);
    }

    // Rights on shared folders come from the side whose shared folders are kept, all other rights from the side whose
    // users are kept. A right stays when its subject and shared folder have the same id and name as on its side.
    private const string RebuildGrants =
        """
        DELETE FROM main.grants;
        INSERT INTO main.grants (subject_kind, subject_id, resource, level)
        SELECT g.subject_kind, g.subject_id, g.resource, g.level
        FROM temp.restore_grants g
        WHERE g.side = CASE WHEN g.resource GLOB 'share:*'
                THEN CASE WHEN @shares THEN 'backup' ELSE 'main' END
                ELSE CASE WHEN @users THEN 'backup' ELSE 'main' END
            END
            AND (g.subject_kind = 'app'
                OR (g.subject_kind = 'user' AND EXISTS (SELECT 1 FROM main.users u WHERE u.id = g.subject_id AND u.username = g.subject_name))
                OR (g.subject_kind = 'group' AND EXISTS (SELECT 1 FROM main.groups x WHERE x.id = g.subject_id AND x.name = g.subject_name)))
            AND (g.resource NOT GLOB 'share:*'
                OR EXISTS (SELECT 1 FROM main.shares s WHERE s.id = CAST(substr(g.resource, 7) AS INTEGER) AND s.name = g.share_name));
        """;
}
