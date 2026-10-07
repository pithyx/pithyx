using System.Text;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// The values an app keeps per user (D61, the SDK's <c>storage.*</c>; sandboxed apps have no browser storage): keys of
/// 1 to 128 characters, values of at most 64 KB of JSON, and at most 1 MB of keys and values per app and user.
/// </summary>
public sealed class AppValueStore(SqliteConnectionFactory connections, TimeProvider time)
{
    public const int MaxKeyLength = 128;
    public const int MaxValueBytes = 64 * 1024;
    public const int MaxTotalBytes = 1024 * 1024;

    public async Task<IReadOnlyList<string>> KeysAsync(string appId, long userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<string>(
            "SELECT key FROM app_storage WHERE app_id = @appId AND user_id = @userId ORDER BY key",
            new { appId, userId })];
    }

    /// <summary>The stored value, or JSON null.</summary>
    public async Task<JsonElement> GetAsync(string appId, long userId, string? key, CancellationToken cancellationToken)
    {
        CheckKey(key);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>(
            "SELECT value FROM app_storage WHERE app_id = @appId AND user_id = @userId AND key = @key",
            new { appId, userId, key });
        using var document = JsonDocument.Parse(json ?? "null");
        return document.RootElement.Clone();
    }

    public async Task SetAsync(string appId, long userId, string? key, JsonElement value, CancellationToken cancellationToken)
    {
        CheckKey(key);
        var json = value.ValueKind == JsonValueKind.Undefined ? "null" : value.GetRawText();
        var size = Encoding.UTF8.GetByteCount(json);
        if (size > MaxValueBytes)
        {
            throw TooLarge(MaxValueBytes);
        }
        // One statement checks the total and writes, so two writes of the same app and user cannot pass it together.
        await using var connection = await connections.OpenAsync(cancellationToken);
        var written = await connection.ExecuteAsync(
            """
            INSERT INTO app_storage (app_id, user_id, key, value, updated_at)
            SELECT @appId, @userId, @key, @json, @now
            WHERE (SELECT coalesce(sum(length(CAST(key AS BLOB)) + length(CAST(value AS BLOB))), 0)
                   FROM app_storage WHERE app_id = @appId AND user_id = @userId AND key <> @key) + @total <= @max
            ON CONFLICT (app_id, user_id, key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """,
            new { appId, userId, key, json, now = SessionService.Format(time.GetUtcNow()), total = Encoding.UTF8.GetByteCount(key!) + size, max = MaxTotalBytes });
        if (written == 0)
        {
            throw TooLarge(MaxTotalBytes);
        }
    }

    public async Task RemoveAsync(string appId, long userId, string? key, CancellationToken cancellationToken)
    {
        CheckKey(key);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM app_storage WHERE app_id = @appId AND user_id = @userId AND key = @key", new { appId, userId, key });
    }

    private static void CheckKey(string? key)
    {
        if (key is not { Length: > 0 and <= MaxKeyLength })
        {
            throw new PithyxException(ErrorCodes.AppStorageKeyInvalid, new Dictionary<string, object?> { ["maxLength"] = MaxKeyLength });
        }
    }

    private static PithyxException TooLarge(int bytes) =>
        new(ErrorCodes.AppStorageTooLarge, new Dictionary<string, object?> { ["maxKb"] = bytes / 1024 });
}
