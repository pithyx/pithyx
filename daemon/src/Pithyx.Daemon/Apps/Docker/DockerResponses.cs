using System.Globalization;
using System.Text.Json;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>Parsers of the Engine's answers (fixtures in <c>daemon/tests/fixtures/docker</c>, recorded by the A1 spike).</summary>
public static class DockerResponses
{
    /// <summary>The containers of <c>GET /containers/json</c>.</summary>
    public static IReadOnlyList<DockerContainer> Containers(string json)
    {
        using var document = JsonDocument.Parse(json);
        var containers = new List<DockerContainer>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var labels = Labels(item, "Labels");
            var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.TryGetProperty("NetworkSettings", out var settings) && settings.TryGetProperty("Networks", out var networks) && networks.ValueKind == JsonValueKind.Object)
            {
                foreach (var network in networks.EnumerateObject())
                {
                    if (network.Value.TryGetProperty("IPAddress", out var address) && address.GetString() is { Length: > 0 } ip)
                    {
                        addresses[network.Name] = ip;
                    }
                }
            }
            var name = item.TryGetProperty("Names", out var names) && names.GetArrayLength() > 0 ? names[0].GetString()!.TrimStart('/') : "";
            containers.Add(new DockerContainer(
                item.GetProperty("Id").GetString()!,
                name,
                labels.GetValueOrDefault("com.pithyx.app"),
                labels.GetValueOrDefault("com.pithyx.service"),
                item.GetProperty("State").GetString() ?? "",
                addresses));
        }
        return containers;
    }

    /// <summary>One line of <c>GET /events</c>, or null for anything but an app container's event.</summary>
    public static DockerEvent? Event(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (Text(root, "Type") != "container" || Text(root, "Action") is not { } action || !root.TryGetProperty("Actor", out var actor))
            {
                return null;
            }
            var attributes = Labels(actor, "Attributes");
            if (attributes.GetValueOrDefault("com.pithyx.app") is not { } app || Text(actor, "ID") is not { } id)
            {
                return null;
            }
            var colon = action.IndexOf(':', StringComparison.Ordinal);
            var time = root.TryGetProperty("timeNano", out var nanos) && nanos.TryGetInt64(out var value)
                ? DateTimeOffset.UnixEpoch.AddTicks(value / 100)
                : DateTimeOffset.UnixEpoch;
            return new DockerEvent(
                colon < 0 ? action : action[..colon],
                colon < 0 ? null : action[(colon + 1)..].Trim(),
                id,
                app,
                attributes.GetValueOrDefault("com.pithyx.service"),
                int.TryParse(attributes.GetValueOrDefault("exitCode"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exit) ? exit : null,
                time);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The image ids of <c>POST /images/load?quiet=1</c>: lines like <c>{"stream":"Loaded image ID: sha256:..."}</c>.</summary>
    public static IReadOnlyList<string> LoadedImages(string output)
    {
        const string Prefix = "Loaded image ID: ";
        var ids = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (Text(document.RootElement, "errorDetail") is not null || Text(document.RootElement, "error") is { } error)
                {
                    throw new DockerEngineException(Text(document.RootElement, "error") ?? "loading the image failed");
                }
                if (Text(document.RootElement, "stream")?.Trim() is { } stream && stream.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    ids.Add(stream[Prefix.Length..].Trim());
                }
            }
            catch (JsonException)
            {
                // Not a JSON line; the Engine writes only JSON here.
            }
        }
        return ids;
    }

    /// <summary>The error of a progress stream (pull, load), or null when it went through.</summary>
    public static string? StreamError(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (Text(document.RootElement, "error") is { } error)
                {
                    return error;
                }
            }
            catch (JsonException)
            {
                // Skip what is not JSON.
            }
        }
        return null;
    }

    /// <summary>The <c>message</c> of an Engine error answer.</summary>
    public static string? Message(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Text(document.RootElement, "message") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Labels(JsonElement element, string property)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty(property, out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in map.EnumerateObject())
            {
                if (label.Value.ValueKind == JsonValueKind.String)
                {
                    labels[label.Name] = label.Value.GetString()!;
                }
            }
        }
        return labels;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
