using System.Globalization;
using System.Text.Json;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>Reading helpers for the JSON output of OpenZFS 2.3 (<c>-j --json-int</c>).</summary>
internal static class ZfsJson
{
    /// <summary>The members of an object property, or none when it is missing or not an object.</summary>
    public static IEnumerable<JsonElement> Members(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().Select(member => member.Value)
            : [];

    public static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? AsText(value) : null;

    public static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? AsNumber(value) ?? 0 : 0;

    /// <summary>A numeric value; GUIDs exceed <see cref="long"/> and are read as text instead.</summary>
    public static long? AsNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    /// <summary>Text of a value; ZFS prints <c>-</c> for "no value".</summary>
    public static string? AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() is { Length: > 0 } text && text != "-" => text,
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    /// <summary>The <c>value</c> of a property in a <c>properties</c> object.</summary>
    public static JsonElement? Property(JsonElement item, string name) =>
        item.TryGetProperty("properties", out var properties)
        && properties.TryGetProperty(name, out var property)
        && property.TryGetProperty("value", out var value)
            ? value
            : null;

    public static long PropertyNumber(JsonElement item, string name) => Property(item, name) is { } value ? AsNumber(value) ?? 0 : 0;

    public static string? PropertyText(JsonElement item, string name) => Property(item, name) is { } value ? AsText(value) : null;

    /// <summary>Parses the output, or returns null for the empty output ZFS prints when there is nothing to list.</summary>
    public static JsonDocument? Parse(string json) => string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);
}
