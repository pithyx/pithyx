using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pithyx.Daemon.SupportBundles;

/// <summary>
/// What of <c>pithyx.db</c> goes into a support bundle (D412). Every table and every column is listed here, so a new one
/// stays out until somebody decides about it (a test fails until then): secrets never go in, nor what users keep for
/// themselves (desktops, notes, recent files, app data). JSON values also lose every property named like a secret.
/// </summary>
public static partial class SupportDatabase
{
    /// <summary>Tables that go in with every column but the ones listed.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Included = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["app_elevated"] = None(),
        ["app_permissions"] = None(),
        ["apps"] = Set("icon"),
        ["audit_log"] = None(),
        ["backup_jobs"] = Set("repository_key"),
        ["backup_runs"] = None(),
        ["backup_versions"] = None(),
        ["backup_targets"] = Set("ssh_private_key"),
        ["catalogs"] = Set("ssh_private_key", "registry_token"),
        ["file_services"] = None(),
        ["grants"] = None(),
        ["group_members"] = None(),
        ["groups"] = None(),
        ["id_allocations"] = None(),
        ["jobs"] = None(),
        ["network_config"] = None(),
        ["network_pending"] = Set("previous_files"),
        ["notification_channels"] = Set("config"),
        ["notification_mutes"] = None(),
        ["notification_rules"] = None(),
        ["proxy_acme"] = None(),
        ["proxy_hosts"] = Set("basic_auth", "request_headers", "response_headers"),
        ["proxy_redirects"] = None(),
        ["proxy_streams"] = None(),
        ["settings"] = None(),
        ["share_user_quotas"] = None(),
        ["shares"] = None(),
        ["snapshot_schedules"] = None(),
        ["storage_alert_mutes"] = None(),
        ["storage_alert_notices"] = None(),
        ["storage_mappings"] = None(),
        ["storage_schedule_runs"] = None(),
        ["storage_schedules"] = None(),
        ["system_region"] = None(),
        ["system_updates"] = None(),
        ["tls_issued"] = Set("certificate", "chain"),
        ["users"] = Set("password_hash", "totp_secret", "totp_last_used_step"),
    };

    /// <summary>Tables that stay out: secrets, sessions, and what users keep for themselves.</summary>
    public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(StringComparer.Ordinal)
    {
        "app_storage",
        "desktop_items",
        "desktop_layouts",
        "notifications",
        "passkeys",
        "proxy_sessions",
        "recent_files",
        "recovery_codes",
        "sessions",
        "trash_items",
        "user_appearance",
        "user_file_defaults",
        "user_languages",
        "user_startup_apps",
        "window_preferences",
    };

    /// <summary>
    /// Rows of <c>settings</c> that go in, by key or key prefix (ending in a dot or colon); every other key is listed
    /// without its value. <c>notifications.smtp</c> and <c>ups.settings</c> lose their passwords like every JSON value.
    /// </summary>
    public static readonly IReadOnlyList<string> IncludedSettings =
    [
        "apps.pool",
        "homes.pool",
        "homes.quota",
        "logs.settings",
        "notifications.smtp",
        "pool.capacity:",
        "proxy.acme",
        "proxy.signInAddress",
        "proxy.unknownHosts",
        "store.channel",
        "store.updates.",
        "tls.extraNames",
        "updates.channel",
        "updates.check",
        "updates.sequence.",
        "updates.status",
        "updates.time",
        "ups.settings",
    ];

    public const string Omitted = "(omitted)";

    /// <summary>Whether <paramref name="key"/> of <c>settings</c> goes in with its value.</summary>
    public static bool IncludesSetting(string key) =>
        IncludedSettings.Any(entry => entry.EndsWith('.') || entry.EndsWith(':') ? key.StartsWith(entry, StringComparison.Ordinal) : key == entry);

    /// <summary>Whether a JSON property of this name is a secret: a password, secret, token or private key.</summary>
    public static bool IsSecretName(string name) =>
        SecretName().IsMatch(name);

    /// <summary>
    /// A value as it goes into the bundle: JSON objects and arrays without their secret properties, and no password in a
    /// URL (<c>https://user:password@host</c>); anything else unchanged.
    /// </summary>
    public static object? Scrub(object? value)
    {
        if (value is not string raw)
        {
            return value;
        }
        var text = WithoutUrlPasswords(raw);
        if (text.Length < 2 || (text[0] != '{' && text[0] != '['))
        {
            return text;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return text;
        }
        Remove(node);
        return node?.ToJsonString();
    }

    /// <summary>The password of every URL in <paramref name="text"/> blanked.</summary>
    public static string WithoutUrlPasswords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UrlPassword().Replace(text, Omitted + "@");
    }

    /// <summary>One file per included table: <c>{ "table", "omittedColumns", "rows" }</c>.</summary>
    public static async Task<IReadOnlyList<(string Table, byte[] Json)>> ExportAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var files = new List<(string, byte[])>();
        foreach (var table in await TablesAsync(connection, transaction, cancellationToken))
        {
            if (!Included.TryGetValue(table, out var omitted))
            {
                continue;
            }
            var columns = (await ColumnsAsync(connection, table, transaction, cancellationToken)).Where(column => !omitted.Contains(column)).ToList();
            var rows = new JsonArray();
            var select = $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)}";
            await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(select, transaction: transaction, cancellationToken: cancellationToken));
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new JsonObject();
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = await reader.IsDBNullAsync(index, cancellationToken) ? null : reader.GetValue(index);
                    row[reader.GetName(index)] = Value(table, row, reader.GetName(index), value);
                }
                rows.Add(row);
            }
            var file = new JsonObject
            {
                ["table"] = table,
                ["omittedColumns"] = new JsonArray([.. omitted.Order(StringComparer.Ordinal).Select(column => (JsonNode?)column)]),
                ["rows"] = rows,
            };
            files.Add((table, JsonSerializer.SerializeToUtf8Bytes(file, _indented)));
        }
        return files;
    }

    /// <summary>The tables of the database but SQLite's own and <c>schema_version</c>.</summary>
    public static async Task<IReadOnlyList<string>> TablesAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> 'schema_version' ORDER BY name",
            transaction: transaction,
            cancellationToken: cancellationToken))];

    public static async Task<IReadOnlyList<string>> ColumnsAsync(SqliteConnection connection, string table, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<string>(new CommandDefinition($"SELECT name FROM pragma_table_info({Literal(table)})", transaction: transaction, cancellationToken: cancellationToken))];

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    private static JsonNode? Value(string table, JsonObject row, string column, object? value)
    {
        if (table == "settings" && column == "value" && row["key"]?.GetValue<string>() is { } key && !IncludesSetting(key))
        {
            return Omitted;
        }
        return Scrub(value) switch
        {
            null => null,
            string text => text,
            long number => number,
            double number => number,
            byte[] bytes => $"({bytes.Length} bytes)",
            var other => other.ToString(),
        };
    }

    private static void Remove(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var name in value.Select(property => property.Key).Where(IsSecretName).ToList())
                {
                    value.Remove(name);
                }
                foreach (var (_, child) in value)
                {
                    Remove(child);
                }
                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    Remove(item);
                }
                break;
        }
    }

    private static HashSet<string> None() => new(StringComparer.Ordinal);

    private static HashSet<string> Set(params string[] columns) => new(columns, StringComparer.Ordinal);

    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Literal(string text) => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    [GeneratedRegex("password|passwd|secret|token|private_?key|passphrase|credential|psk", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"(?<=[a-z][a-z0-9+.-]*://)[^/@\s:]+:[^/@\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPassword();
}
