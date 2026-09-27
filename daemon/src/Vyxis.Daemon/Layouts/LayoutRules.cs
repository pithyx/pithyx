using System.Text;
using System.Text.Json;
using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Layouts;

/// <summary>
/// Checks a saved desktop layout. The daemon does not interpret app ids or geometry (the desktop validates those live
/// on restore); it enforces the schema version and the size limits.
/// </summary>
public static class LayoutRules
{
    public const int SchemaVersion = 1;
    public const int MaxWindows = 50;
    /// <summary>"App data in a layout is capped at 16 KB": the arguments of all windows together, in UTF-8 bytes.</summary>
    public const int MaxAppDataBytes = 16 * 1024;
    public const int MaxLayoutBytes = 48 * 1024;

    public sealed record Checked(string Json, int WindowCount);

    /// <summary>Returns the layout as compact JSON with its window count, or throws with a layout error code.</summary>
    public static Checked Check(JsonElement layout)
    {
        if (layout.ValueKind != JsonValueKind.Object
            || !layout.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number)
            || number != SchemaVersion
            || !layout.TryGetProperty("windows", out var windows)
            || windows.ValueKind != JsonValueKind.Array)
        {
            throw new VyxisException(ErrorCodes.DesktopLayoutInvalid);
        }

        var count = windows.GetArrayLength();
        var appData = 0;
        foreach (var window in windows.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !window.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.String)
            {
                throw new VyxisException(ErrorCodes.DesktopLayoutInvalid);
            }
            if (window.TryGetProperty("args", out var args) && args.ValueKind != JsonValueKind.Null)
            {
                appData += Encoding.UTF8.GetByteCount(args.GetRawText());
            }
        }

        var json = JsonSerializer.Serialize(layout);
        if (count > MaxWindows || appData > MaxAppDataBytes || Encoding.UTF8.GetByteCount(json) > MaxLayoutBytes)
        {
            throw new VyxisException(ErrorCodes.DesktopLayoutTooLarge, new Dictionary<string, object?>
            {
                ["maxWindows"] = MaxWindows,
                ["maxAppDataKb"] = MaxAppDataBytes / 1024,
            });
        }
        return new Checked(json, count);
    }
}
