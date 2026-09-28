using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Storage.Zfs;

/// <param name="Name">Device or vdev name as <c>zpool import</c> prints it: <c>raidz1-0</c>, a by-id name, a path or, for a missing device, its GUID.</param>
/// <param name="State">
/// ZFS state, for example <c>ONLINE</c> or <c>UNAVAIL</c>; null for the <c>logs</c>, <c>cache</c>, <c>spares</c>,
/// <c>special</c> and <c>dedup</c> group headings, and for cache and spare devices, which an import lists without one.
/// </param>
/// <param name="Detail">What ZFS adds after the state, for example <c>cannot open</c> or <c>insufficient replicas</c>.</param>
/// <param name="DiskId">The disk this device is on, if it is one the daemon knows.</param>
/// <param name="Children">Child vdevs.</param>
public sealed record ImportVdev(string Name, string? State, string? Detail, string? DiskId, IReadOnlyList<ImportVdev> Children)
{
    /// <summary>The devices below this vdev, or the vdev itself when it is a device.</summary>
    public IEnumerable<ImportVdev> Leaves() => Children.Count == 0 ? [this] : Children.SelectMany(child => child.Leaves());
}

/// <summary>One pool that <c>zpool import</c> found and that is not imported.</summary>
/// <param name="Name">The pool's name as stored on its devices.</param>
/// <param name="PoolGuid">The numeric identifier, which names the pool in the import command.</param>
/// <param name="State">ZFS state of the pool as it would be imported: <c>ONLINE</c>, <c>DEGRADED</c>, <c>UNAVAIL</c>, <c>FAULTED</c>.</param>
/// <param name="Status">What ZFS says is wrong, if anything, in English.</param>
/// <param name="Action">What ZFS says can be done, in English.</param>
/// <param name="MessageId">The ZFS message id from the <c>see:</c> link, for example <c>ZFS-8000-2Q</c>.</param>
/// <param name="CanImport">True when ZFS says the pool can be imported (possibly only with force).</param>
/// <param name="NeedsForce">True when the pool was last accessed by another system and not exported there.</param>
/// <param name="Vdevs">The top-level vdevs and group headings with their devices; the pool's own root line is left out.</param>
public sealed record ImportCandidate(
    string Name,
    string PoolGuid,
    string State,
    string? Status,
    string? Action,
    string? MessageId,
    bool CanImport,
    bool NeedsForce,
    IReadOnlyList<ImportVdev> Vdevs);

/// <summary>
/// Parses the text of <c>zpool import</c> without a pool argument (OpenZFS 2.3 prints no JSON for it). Destroyed pools
/// are not listed because the daemon never passes <c>-D</c>.
/// </summary>
public static partial class ZpoolImportParser
{
    /// <summary>The message ZFS prints for a pool last accessed by another system (ZFS-8000-EY).</summary>
    public const string HostMismatchMessage = "ZFS-8000-EY";

    public static IReadOnlyList<ImportCandidate> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var candidates = new List<ImportCandidate>();
        foreach (var block in Blocks(text.Replace("\r", "", StringComparison.Ordinal).Split('\n')))
        {
            candidates.Add(ParseBlock(block));
        }
        return candidates;
    }

    /// <summary>Splits the output at every <c>pool:</c> line.</summary>
    private static IEnumerable<List<string>> Blocks(string[] lines)
    {
        List<string>? block = null;
        foreach (var line in lines)
        {
            if (Field().Match(line) is { Success: true } match && match.Groups[1].Value == "pool")
            {
                if (block is not null)
                {
                    yield return block;
                }
                block = [];
            }
            block?.Add(line);
        }
        if (block is not null)
        {
            yield return block;
        }
    }

    private static ImportCandidate ParseBlock(List<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var config = new List<string>();
        foreach (var line in lines)
        {
            if (current == "config" && (line.StartsWith('\t') || line.Trim().Length == 0))
            {
                config.Add(line.TrimStart('\t'));
                continue;
            }
            if (Field().Match(line) is { Success: true } match)
            {
                current = match.Groups[1].Value;
                fields[current] = match.Groups[2].Value.Trim();
            }
            else if (current is not null && current != "config" && line.StartsWith('\t'))
            {
                // A long status or action continues on lines that start with a tab.
                fields[current] = $"{fields[current]} {line.Trim()}";
            }
        }
        var action = fields.GetValueOrDefault("action");
        var status = fields.GetValueOrDefault("status");
        var messageId = fields.GetValueOrDefault("see") is { } see && MessageId().Match(see) is { Success: true } id ? id.Value : null;
        var vdevs = ParseTree(config);
        return new ImportCandidate(
            fields.GetValueOrDefault("pool") ?? "",
            fields.GetValueOrDefault("id") ?? "",
            fields.GetValueOrDefault("state") ?? "UNKNOWN",
            status,
            action,
            messageId,
            action?.StartsWith("The pool can be imported", StringComparison.Ordinal) == true,
            messageId == HostMismatchMessage
                || status?.Contains("last accessed by another system", StringComparison.Ordinal) == true,
            vdevs);
    }

    /// <summary>
    /// The config tree: two spaces of indentation per level below the pool's root line, then the name, the state and
    /// an optional detail.
    /// </summary>
    private static IReadOnlyList<ImportVdev> ParseTree(List<string> lines)
    {
        var root = new Node("", null, null, -1);
        var stack = new Stack<Node>();
        stack.Push(root);
        foreach (var line in lines.Where(line => line.Trim().Length > 0))
        {
            var depth = (line.Length - line.TrimStart(' ').Length) / 2;
            var parts = Columns().Split(line.Trim(), 3);
            var node = new Node(parts[0], parts.Length > 1 ? parts[1] : null, parts.Length > 2 ? parts[2].Trim() : null, depth);
            while (stack.Peek().Depth >= depth)
            {
                stack.Pop();
            }
            stack.Peek().Children.Add(node);
            stack.Push(node);
        }
        // The first line at depth 0 is the pool itself; group headings (logs, cache, spares) follow at depth 0 without a state.
        return root.Children.Count == 0 ? [] : [.. root.Children[0].Children.Concat(root.Children.Skip(1)).Select(node => node.ToVdev())];
    }

    private sealed record Node(string Name, string? State, string? Detail, int Depth)
    {
        public List<Node> Children { get; } = [];

        public ImportVdev ToVdev() => new(Name, State, Detail, null, [.. Children.Select(child => child.ToVdev())]);
    }

    //   pool: tank      id: 123   state: ONLINE   status: ...   action: ...   see: https://...   config:
    [GeneratedRegex(@"^\s*(pool|id|state|status|action|see|config|comment|errata):(?:\s(.*))?$")]
    private static partial Regex Field();

    [GeneratedRegex(@"ZFS-\d{4}-[0-9A-Z]+")]
    private static partial Regex MessageId();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Columns();
}
