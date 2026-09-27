using System.Text.Json;
using Dapper;
using Vyxis.Daemon.Auth;
using Vyxis.Daemon.Database;
using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Layouts;

/// <summary>
/// Desktop layouts per user and device, and window preferences per user and app. A layout expires 90 days after it
/// was last saved; <see cref="LayoutCleanupService"/> deletes expired ones.
/// </summary>
public sealed class LayoutService(SqliteConnectionFactory connections, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// Saves the layout of a device and returns its new version. With <paramref name="expectedVersion"/> the save only
    /// happens if the stored layout still has that version (0 for none or an expired one); otherwise it throws
    /// <see cref="ErrorCodes.DesktopLayoutConflict"/> with the current version.
    /// </summary>
    public async Task<long> SaveAsync(long userId, string deviceId, string? userAgent, JsonElement layout, long? expectedVersion, CancellationToken cancellationToken)
    {
        var checkedLayout = LayoutRules.Check(layout);
        var device = DeviceNamesParser.Parse(userAgent);
        var now = SessionService.Format(time.GetUtcNow());
        await using var connection = await connections.OpenAsync(cancellationToken);
        // An expired layout counts as none: the client read it as version 0.
        var version = await connection.QuerySingleOrDefaultAsync<long?>(
            """
            INSERT INTO desktop_layouts (user_id, device_id, device_browser, device_os, schema_version, layout, window_count, updated_at, expires_at, version)
            VALUES (@UserId, @DeviceId, @Browser, @Os, @SchemaVersion, @Layout, @WindowCount, @Now, @ExpiresAt, 1)
            ON CONFLICT (user_id, device_id) DO UPDATE SET
                device_browser = excluded.device_browser,
                device_os = excluded.device_os,
                schema_version = excluded.schema_version,
                layout = excluded.layout,
                window_count = excluded.window_count,
                updated_at = excluded.updated_at,
                expires_at = excluded.expires_at,
                version = CASE WHEN desktop_layouts.expires_at <= @Now THEN 1 ELSE desktop_layouts.version + 1 END
            WHERE @Expected IS NULL
                OR (CASE WHEN desktop_layouts.expires_at <= @Now THEN 0 ELSE desktop_layouts.version END) = @Expected
            RETURNING version
            """,
            new
            {
                UserId = userId,
                DeviceId = deviceId,
                device.Browser,
                device.Os,
                SchemaVersion = LayoutRules.SchemaVersion,
                Layout = checkedLayout.Json,
                checkedLayout.WindowCount,
                Now = now,
                ExpiresAt = SessionService.Format(time.GetUtcNow() + Lifetime),
                Expected = expectedVersion,
            });
        if (version is null)
        {
            var current = await connection.QuerySingleAsync<long>(
                "SELECT CASE WHEN expires_at <= @now THEN 0 ELSE version END FROM desktop_layouts WHERE user_id = @userId AND device_id = @deviceId",
                new { userId, deviceId, now });
            throw new VyxisException(ErrorCodes.DesktopLayoutConflict, new Dictionary<string, object?> { ["version"] = current });
        }
        return version.Value;
    }

    /// <summary>The saved layout of a device and every window preference of the user.</summary>
    public async Task<DesktopState> GetStateAsync(long userId, string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var layout = await connection.QuerySingleOrDefaultAsync<LayoutRow>(
            "SELECT layout AS Json, version AS Version FROM desktop_layouts WHERE user_id = @userId AND device_id = @deviceId AND expires_at > @now",
            new { userId, deviceId, now = SessionService.Format(time.GetUtcNow()) });
        var preferences = await connection.QueryAsync<PreferenceRow>(
            "SELECT app_id AS AppId, width AS Width, height AS Height FROM window_preferences WHERE user_id = @userId ORDER BY app_id",
            new { userId });
        return new DesktopState(
            layout is null ? null : Parse(layout.Json),
            layout?.Version ?? 0,
            [.. preferences.Select(row => new WindowPreference(row.AppId, (int)row.Width, (int)row.Height))]);
    }

    /// <summary>The user's other devices with a saved layout, most recently used first.</summary>
    public async Task<IReadOnlyList<DeviceLayout>> ListDevicesAsync(long userId, string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DeviceRow>(
            """
            SELECT device_id AS DeviceId, device_browser AS Browser, device_os AS Os, window_count AS WindowCount, updated_at AS UpdatedAt,
                CASE WHEN json_type(layout, '$.viewport.width') = 'integer' THEN json_extract(layout, '$.viewport.width') END AS ScreenWidth,
                CASE WHEN json_type(layout, '$.viewport.height') = 'integer' THEN json_extract(layout, '$.viewport.height') END AS ScreenHeight
            FROM desktop_layouts
            WHERE user_id = @userId AND device_id <> @deviceId AND expires_at > @now
            ORDER BY updated_at DESC
            """,
            new { userId, deviceId, now = SessionService.Format(time.GetUtcNow()) });
        return [.. rows.Select(row => new DeviceLayout(row.DeviceId, row.Browser, row.Os, row.WindowCount, SessionService.Parse(row.UpdatedAt), row.ScreenWidth, row.ScreenHeight))];
    }

    /// <summary>"Get desktop from ...": copies another device's layout to this device and returns it with its new version.</summary>
    public async Task<SavedLayout> CopyAsync(long userId, string fromDeviceId, string toDeviceId, string? userAgent, CancellationToken cancellationToken)
    {
        string? layout;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            layout = await connection.QuerySingleOrDefaultAsync<string?>(
                "SELECT layout FROM desktop_layouts WHERE user_id = @userId AND device_id = @fromDeviceId AND expires_at > @now",
                new { userId, fromDeviceId, now = SessionService.Format(time.GetUtcNow()) });
        }
        if (layout is null)
        {
            throw new VyxisException(ErrorCodes.DesktopDeviceNotFound);
        }
        var element = Parse(layout);
        var version = await SaveAsync(userId, toDeviceId, userAgent, element, expectedVersion: null, cancellationToken);
        return new SavedLayout(element, version);
    }

    public async Task SetPreferenceAsync(long userId, string appId, int width, int height, CancellationToken cancellationToken)
    {
        if (appId.Length is 0 or > 64 || width <= 0 || height <= 0)
        {
            throw new VyxisException(ErrorCodes.RequestBodyInvalid);
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO window_preferences (user_id, app_id, width, height, updated_at)
            VALUES (@userId, @appId, @width, @height, @now)
            ON CONFLICT (user_id, app_id) DO UPDATE SET width = excluded.width, height = excluded.height, updated_at = excluded.updated_at
            """,
            new { userId, appId, width, height, now = SessionService.Format(time.GetUtcNow()) });
    }

    /// <summary>Deletes layouts past their expiry. Returns how many were deleted.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(
            "DELETE FROM desktop_layouts WHERE expires_at <= @now",
            new { now = SessionService.Format(time.GetUtcNow()) });
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record LayoutRow(string Json, long Version);

    private sealed record PreferenceRow(string AppId, long Width, long Height);

    // Properties instead of a constructor: SQLite reports no column type for the screen size expressions when no row
    // matches, which Dapper cannot map to a constructor.
    private sealed class DeviceRow
    {
        public string DeviceId { get; init; } = "";
        public string? Browser { get; init; }
        public string? Os { get; init; }
        public long WindowCount { get; init; }
        public string UpdatedAt { get; init; } = "";
        public long? ScreenWidth { get; init; }
        public long? ScreenHeight { get; init; }
    }
}
