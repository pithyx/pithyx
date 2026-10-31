using System.Collections.Frozen;
using System.Text.Json;

namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>How much an elevated right can do on the box, and so how much granting it asks (D388).</summary>
public enum RiskTier
{
    /// <summary>A device that reaches little beyond the app: a hint and a checkbox.</summary>
    Elevated,

    /// <summary>The host's network or processes, a capability or the filtered Docker socket: a risk text, a checkbox and the step-up.</summary>
    High,

    /// <summary>Root on the box: a step of its own where the administrator types the app's name, plus the step-up; never updated by itself.</summary>
    Critical,
}

/// <summary>
/// The permissions and elevated rights of a manifest's <c>permissions</c> map (manifest v2, D388): the plain
/// permissions reach the app's token, the elevated rights only its backend (D311).
/// </summary>
public static class ElevatedRights
{
    /// <summary>The permissions of the manifest schema's <c>Permission</c>; every other name is an elevated right.</summary>
    public static readonly FrozenSet<string> Plain = FrozenSet.ToFrozenSet(
        ["files.read", "files.write", "files.shares", "notifications", "notifications.external", "directory.read", "network.internet", "network.lan"],
        StringComparer.Ordinal);

    public static bool IsElevated(string name) => !Plain.Contains(name);

    /// <summary>The elevated rights the manifest asks for, in its order.</summary>
    public static IReadOnlyList<string> Of(JsonElement manifest) => [.. Names(manifest).Where(IsElevated)];

    /// <summary>The plain permissions the manifest asks for, in its order.</summary>
    public static IReadOnlyList<string> PermissionsOf(JsonElement manifest) => [.. Names(manifest).Where(name => !IsElevated(name))];

    /// <summary>Whether the manifest's <c>permissions</c> name this permission or right.</summary>
    public static bool Declares(JsonElement manifest, string name) => Grant(manifest, name) is not null;

    /// <summary>The paths of the device rights marked <c>optional</c> (D387), for example <c>/dev/dri</c>.</summary>
    public static IReadOnlySet<string> OptionalDevices(JsonElement manifest) =>
        Names(manifest)
            .Where(name => name.StartsWith(ManifestValidator.DevicePrefix, StringComparison.Ordinal)
                && Grant(manifest, name) is { } grant
                && grant.TryGetProperty("optional", out var optional)
                && optional.ValueKind == JsonValueKind.True)
            .Select(name => name[ManifestValidator.DevicePrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The tier of an elevated right, from <c>elevated.tiers</c> of the compose rules.</summary>
    public static RiskTier TierOf(string right)
    {
        ArgumentNullException.ThrowIfNull(right);
        var rules = Compose.ComposeValidator.Rules;
        // A device below a listed folder has its tier: device:/dev/dri/renderD128 is one node of device:/dev/dri.
        for (var name = right; ; name = name[..name.LastIndexOf('/')])
        {
            if (rules.Tiers.TryGetValue(name, out var tier))
            {
                return tier;
            }
            if (!name.StartsWith(ManifestValidator.DevicePrefix, StringComparison.Ordinal) || name.LastIndexOf('/') <= ManifestValidator.DevicePrefix.Length)
            {
                return rules.DefaultTier;
            }
        }
    }

    /// <summary>Whether the manifest asks for a critical right, which keeps the app from updating by itself.</summary>
    public static bool IsCritical(JsonElement manifest) => Of(manifest).Any(right => TierOf(right) == RiskTier.Critical);

    private static JsonElement? Grant(JsonElement manifest, string name) =>
        Map(manifest) is { } map && map.TryGetProperty(name, out var grant) ? grant : null;

    private static IEnumerable<string> Names(JsonElement manifest) =>
        Map(manifest) is { } map ? map.EnumerateObject().Select(entry => entry.Name) : [];

    private static JsonElement? Map(JsonElement manifest) =>
        manifest.ValueKind == JsonValueKind.Object
        && manifest.TryGetProperty("permissions", out var permissions)
        && permissions.ValueKind == JsonValueKind.Object
            ? permissions
            : null;
}
