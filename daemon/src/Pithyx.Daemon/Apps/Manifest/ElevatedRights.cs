using System.Text.Json;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>The elevated rights a manifest's backend asks for (<c>backend.elevated</c>, D311).</summary>
public static class ElevatedRights
{
    public static IReadOnlyList<string> Of(JsonElement manifest) =>
        manifest.ValueKind == JsonValueKind.Object
        && manifest.TryGetProperty("backend", out var backend)
        && backend.ValueKind == JsonValueKind.Object
        && backend.TryGetProperty("elevated", out var rights)
            ? [.. rights.EnumerateArray().Select(right => right.GetString()!)]
            : [];
}
