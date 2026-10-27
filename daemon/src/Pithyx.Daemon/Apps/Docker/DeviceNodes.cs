using Pithyx.Daemon.Apps.Compose;
using Pithyx.Daemon.Apps.Manifest;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>A device node a backend gets: its path on the box, the group it needs (null when everybody may use it or the group is root's).</summary>
public sealed record DeviceNode(string Path, int? Gid);

/// <summary>How <see cref="DeviceNodes"/> sees <c>/dev</c>; <see cref="Real"/> on a box, a fake one in tests.</summary>
/// <param name="CharacterDevice">The group and openness of a character device at a path (links followed), or null.</param>
/// <param name="Entries">The full paths of the entries of a folder, or none.</param>
/// <param name="Resolve">The final target of a link, or null when the path is no link.</param>
/// <param name="Simulated">In simulation every device the manifest names counts as there, so installs and tests never depend on the machine.</param>
public sealed record DeviceFs(
    Func<string, (uint Gid, bool Open)?> CharacterDevice,
    Func<string, IEnumerable<string>> Entries,
    Func<string, string?> Resolve,
    bool Simulated = false)
{
    public static DeviceFs Real { get; } = new(
        Files.LinuxFs.CharacterDevice,
        path => Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path) : [],
        path => new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName);

    public static DeviceFs Simulation { get; } = new(_ => null, _ => [], _ => null, Simulated: true);
}

/// <summary>
/// The device nodes a device right stands for (D311, D387). A right on a device class (<c>/dev/dri</c>, <c>/dev/serial</c>,
/// <c>/dev/net/tun</c>) names every node of the class present now: the character devices of the folder whose names match
/// the class, or the targets of the links below it; any other right names its one node. Nodes outside the block list
/// only, each with the group it needs.
/// </summary>
public static class DeviceNodes
{
    /// <summary>The nodes of one device path now; empty when none is there.</summary>
    public static IReadOnlyList<DeviceNode> Present(string path, DeviceFs fs)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fs);
        var rules = ComposeValidator.Rules;
        if (fs.Simulated)
        {
            return [new DeviceNode(path, null)];
        }
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        if (rules.DeviceClasses.TryGetValue(path, out var deviceClass) && (deviceClass.Nodes is not null || deviceClass.Links is not null))
        {
            if (deviceClass.Nodes is { } nodes)
            {
                foreach (var entry in fs.Entries(path).Where(entry => nodes.IsMatch(System.IO.Path.GetFileName(entry))))
                {
                    candidates.Add(entry);
                }
            }
            if (deviceClass.Links is { } links)
            {
                foreach (var target in Links(path, fs, depth: 0).Where(target => links.IsMatch(target)))
                {
                    candidates.Add(target);
                }
            }
        }
        else
        {
            candidates.Add(fs.Resolve(path) ?? path);
        }
        var present = new List<DeviceNode>();
        foreach (var node in candidates)
        {
            if (rules.Matches("blockedDevice", node) || fs.CharacterDevice(node) is not { } device)
            {
                continue;
            }
            present.Add(new DeviceNode(node, device.Open || device.Gid == 0 ? null : (int)device.Gid));
        }
        return present;
    }

    /// <summary>The device paths of the granted rights that are missing now and not optional (the backend cannot start).</summary>
    public static IReadOnlyList<string> MissingRequired(IEnumerable<string> granted, IReadOnlySet<string> optional, DeviceFs fs) =>
        [.. Paths(granted).Where(path => !optional.Contains(path) && Present(path, fs).Count == 0)];

    /// <summary>The device paths of the granted rights that are missing now and optional (the backend starts without them).</summary>
    public static IReadOnlyList<string> MissingOptional(IEnumerable<string> granted, IReadOnlySet<string> optional, DeviceFs fs) =>
        [.. Paths(granted).Where(path => optional.Contains(path) && Present(path, fs).Count == 0)];

    /// <summary>The device paths of a set of rights, sorted.</summary>
    public static IEnumerable<string> Paths(IEnumerable<string> rights) =>
        rights.Where(right => right.StartsWith(ManifestValidator.DevicePrefix, StringComparison.Ordinal))
            .Select(right => right[ManifestValidator.DevicePrefix.Length..])
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> Links(string folder, DeviceFs fs, int depth)
    {
        if (depth > 3)
        {
            yield break;
        }
        foreach (var entry in fs.Entries(folder))
        {
            if (fs.Resolve(entry) is { } target)
            {
                yield return target;
            }
            else
            {
                foreach (var nested in Links(entry, fs, depth + 1))
                {
                    yield return nested;
                }
            }
        }
    }
}
