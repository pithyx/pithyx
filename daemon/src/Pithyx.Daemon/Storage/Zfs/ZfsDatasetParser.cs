using System.Text.Json;
using static Pithyx.Daemon.Storage.Zfs.ZfsJson;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>
/// Parses <c>zfs list -j --json-int</c> and <c>zfs get -j --json-int</c>, which share their shape: datasets with a
/// <c>properties</c> object.
/// </summary>
public static class ZfsDatasetParser
{
    public static IReadOnlyList<Dataset> ParseList(string json)
    {
        using var document = ZfsJson.Parse(json);
        if (document is null)
        {
            return [];
        }
        return [.. Members(document.RootElement, "datasets")
            .Select(dataset =>
            {
                var mountPoint = PropertyText(dataset, "mountpoint");
                var encryption = PropertyText(dataset, "encryption") ?? "off";
                return new Dataset(
                    Text(dataset, "name") ?? "",
                    Text(dataset, "pool") ?? "",
                    PropertyText(dataset, "type") ?? (Text(dataset, "type")?.ToLowerInvariant() ?? "filesystem"),
                    PropertyNumber(dataset, "used"),
                    PropertyNumber(dataset, "available"),
                    PropertyNumber(dataset, "referenced"),
                    mountPoint is "none" or "legacy" ? null : mountPoint,
                    PropertyText(dataset, "mounted") == "yes",
                    encryption,
                    encryption == "off" ? null : PropertyText(dataset, "keystatus"),
                    encryption == "off" ? null : PropertyText(dataset, "encryptionroot"),
                    Limit(dataset, "quota"),
                    Limit(dataset, "reservation"),
                    PropertyText(dataset, "compression"),
                    Size(dataset, "recordsize"),
                    Size(dataset, "volsize"),
                    Size(dataset, "volblocksize"),
                    PropertyNumber(dataset, "usedbysnapshots"));
            })
            .OrderBy(dataset => dataset.Name, StringComparer.Ordinal)];
    }

    // ZFS prints 0 for "no quota" and "no reservation".
    private static long? Limit(JsonElement dataset, string name) => Size(dataset, name) is long bytes and > 0 ? bytes : null;

    private static long? Size(JsonElement dataset, string name) => Property(dataset, name) is { } value ? AsNumber(value) : null;

    /// <summary>Property values per dataset from <c>zfs get</c>; values ZFS prints as <c>-</c> are left out.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseProperties(string json)
    {
        using var document = ZfsJson.Parse(json);
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (document is null)
        {
            return result;
        }
        foreach (var dataset in Members(document.RootElement, "datasets"))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (dataset.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (property.Value.TryGetProperty("value", out var value) && AsText(value) is { } text)
                    {
                        values[property.Name] = text;
                    }
                }
            }
            result[Text(dataset, "name") ?? ""] = values;
        }
        return result;
    }
}
