using System.Data;
using System.Data.Common;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Layouts;

/// <summary>
/// Desktop items per user, shared across devices: app shortcuts and sticky notes on a grid of cells. A change can name
/// the item's version it builds on (If-Match), so a change made on another device in between is refused instead of
/// overwritten.
/// </summary>
public sealed class DesktopItemService(SqliteConnectionFactory connections, TimeProvider time)
{
    public const int MaxItems = 200;
    public const int MaxNoteLength = 4000;
    public const int MaxNameLength = 100;
    public const int MaxCell = 255;
    private const int MaxAppIdLength = 64;

    // Stored as written: the default escaping would turn every non-ASCII character of a note into six bytes.
    private static readonly JsonSerializerOptions _dataOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The shortcuts every new user starts with, one below the other in the first column.</summary>
    public static readonly IReadOnlyList<string> DefaultShortcuts = ["files", "disks", "about"];

    private const string SelectColumns =
        """
        SELECT id AS Id, kind AS Kind, json_extract(data, '$.app') AS App, json_extract(data, '$.name') AS Name,
            json_extract(data, '$.text') AS Text, grid_column AS GridColumn, grid_row AS GridRow, version AS Version
        FROM desktop_items
        """;

    public async Task<IReadOnlyList<DesktopItem>> ListAsync(long userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ItemRow>($"{SelectColumns} WHERE user_id = @userId AND kind IN ('shortcut', 'note') ORDER BY grid_column, grid_row, id", new { userId });
        return [.. rows.Select(ToItem)];
    }

    public async Task<DesktopItem> CreateAsync(long userId, NewDesktopItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        CheckCell(item.Column, item.Row);
        string data;
        if (item.Kind == DesktopItemKind.Shortcut)
        {
            if (item.Text is not null || !IsAppId(item.App))
            {
                throw new PithyxException(ErrorCodes.DesktopItemInvalid);
            }
            data = ShortcutData(item.App!, CheckName(item.Name));
        }
        else
        {
            if (item.App is not null || item.Name is not null || item.Text is null)
            {
                throw new PithyxException(ErrorCodes.DesktopItemInvalid);
            }
            data = NoteData(CheckText(item.Text));
        }

        var now = SessionService.Format(time.GetUtcNow());
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var id = await connection.ExecuteScalarAsync<long?>(
            """
            INSERT INTO desktop_items (user_id, kind, data, grid_column, grid_row, created_at, updated_at)
            SELECT @userId, @kind, @data, @column, @row, @now, @now
            WHERE (SELECT COUNT(*) FROM desktop_items WHERE user_id = @userId) < @max
            RETURNING id
            """,
            new { userId, kind = KindName(item.Kind), data, column = item.Column, row = item.Row, now, max = MaxItems },
            transaction);
        if (id is null)
        {
            throw new PithyxException(ErrorCodes.DesktopItemLimitReached, new Dictionary<string, object?> { ["max"] = MaxItems });
        }
        var created = await connection.QuerySingleAsync<ItemRow>($"{SelectColumns} WHERE id = @id", new { id }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToItem(created);
    }

    /// <summary>
    /// Changes one item and returns it with its new version. Fails with <see cref="ErrorCodes.DesktopItemNotFound"/> for
    /// an item that does not exist or belongs to someone else. With <paramref name="expectedVersion"/> the change only
    /// happens if the item still has that version; otherwise it fails with <see cref="ErrorCodes.DesktopItemConflict"/>
    /// and the current version in the params.
    /// </summary>
    public async Task<DesktopItem> ChangeAsync(long userId, long id, DesktopItemChange change, long? expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var now = SessionService.Format(time.GetUtcNow());
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<ItemRow>(
            $"{SelectColumns} WHERE id = @id AND user_id = @userId AND kind IN ('shortcut', 'note')",
            new { id, userId },
            transaction)
            ?? throw new PithyxException(ErrorCodes.DesktopItemNotFound);
        if (expectedVersion is not null && current.Version != expectedVersion)
        {
            throw new PithyxException(ErrorCodes.DesktopItemConflict, new Dictionary<string, object?> { ["version"] = current.Version });
        }

        var item = ToItem(current);
        var column = change.Column ?? item.Column;
        var row = change.Row ?? item.Row;
        CheckCell(column, row);
        string data;
        if (item.Kind == DesktopItemKind.Shortcut)
        {
            if (change.Text is not null)
            {
                throw new PithyxException(ErrorCodes.DesktopItemInvalid);
            }
            data = ShortcutData(item.App!, change.Name is null ? item.Name : CheckName(change.Name));
        }
        else
        {
            if (change.Name is not null)
            {
                throw new PithyxException(ErrorCodes.DesktopItemInvalid);
            }
            data = NoteData(change.Text is null ? item.Text ?? "" : CheckText(change.Text));
        }

        var updated = await connection.QuerySingleAsync<ItemRow>(
            """
            UPDATE desktop_items SET data = @data, grid_column = @column, grid_row = @row, version = version + 1, updated_at = @now
            WHERE id = @id
            RETURNING id AS Id, kind AS Kind, json_extract(data, '$.app') AS App, json_extract(data, '$.name') AS Name,
                json_extract(data, '$.text') AS Text, grid_column AS GridColumn, grid_row AS GridRow, version AS Version
            """,
            new { id, data, column, row, now },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToItem(updated);
    }

    /// <summary>Deletes one item of the user. An item that does not exist (any more) is no error.</summary>
    public async Task DeleteAsync(long userId, long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM desktop_items WHERE user_id = @userId AND id = @id", new { userId, id });
    }

    /// <summary>Puts the default shortcuts on the desktop of a new user, inside the transaction that creates the user.</summary>
    public static async Task SeedDefaultsAsync(DbConnection connection, DbTransaction transaction, long userId, string now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        for (var row = 0; row < DefaultShortcuts.Count; row++)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO desktop_items (user_id, kind, data, grid_column, grid_row, created_at, updated_at)
                VALUES (@userId, 'shortcut', @data, 0, @row, @now, @now)
                """,
                new { userId, data = ShortcutData(DefaultShortcuts[row], null), row, now },
                transaction);
        }
    }

    private static void CheckCell(int column, int row)
    {
        if (column is < 0 or > MaxCell || row is < 0 or > MaxCell)
        {
            throw new PithyxException(ErrorCodes.DesktopItemInvalid);
        }
    }

    private static bool IsAppId(string? app) =>
        app is { Length: > 0 and <= MaxAppIdLength } && app.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');

    /// <summary>Trims the label; an empty one means the app's own name.</summary>
    private static string? CheckName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        if (trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new PithyxException(ErrorCodes.DesktopItemInvalid);
        }
        return trimmed;
    }

    private static string CheckText(string text) =>
        text.Length <= MaxNoteLength
            ? text
            : throw new PithyxException(ErrorCodes.DesktopNoteTooLong, new Dictionary<string, object?> { ["maxLength"] = MaxNoteLength });

    private static string ShortcutData(string app, string? name) =>
        JsonSerializer.Serialize(name is null ? new Dictionary<string, string> { ["app"] = app } : new Dictionary<string, string> { ["app"] = app, ["name"] = name }, _dataOptions);

    private static string NoteData(string text) => JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = text }, _dataOptions);

    private static string KindName(DesktopItemKind kind) => kind == DesktopItemKind.Shortcut ? "shortcut" : "note";

    private static DesktopItem ToItem(ItemRow row) => new(
        row.Id,
        row.Kind == "shortcut" ? DesktopItemKind.Shortcut : DesktopItemKind.Note,
        row.App,
        row.Name,
        row.Text,
        (int)row.GridColumn,
        (int)row.GridRow,
        row.Version);

    // Properties instead of a constructor: json_extract reports no column type when it yields null.
    private sealed class ItemRow
    {
        public long Id { get; init; }
        public string Kind { get; init; } = "";
        public string? App { get; init; }
        public string? Name { get; init; }
        public string? Text { get; init; }
        public long GridColumn { get; init; }
        public long GridRow { get; init; }
        public long Version { get; init; }
    }
}
