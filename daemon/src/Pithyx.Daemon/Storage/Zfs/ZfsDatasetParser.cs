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
                    encryption == "off" ? null : PropertyText(dataset, "encryptionroot"));
            })
            .OrderBy(dataset => dataset.Name, StringComparer.Ordinal)];
    }

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
