using System.Text.Json;
using System.Text.RegularExpressions;
using static Pithyx.Daemon.Storage.Zfs.ZfsJson;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>What <c>zpool status -j --json-int</c> says about one pool; sizes come from <c>zpool list</c>.</summary>
public sealed record PoolStatus(string Name, string PoolGuid, string State, long DataErrors, PoolScan? Scan, IReadOnlyList<Vdev> Vdevs, PoolExpansion? Expansion = null);

/// <summary>Parses <c>zpool status -j --json-int</c> (OpenZFS 2.3).</summary>
public static partial class ZpoolStatusParser
{
    public static IReadOnlyList<PoolStatus> Parse(string json)
    {
        using var document = ZfsJson.Parse(json);
        if (document is null)
        {
            return [];
        }
        return [.. Members(document.RootElement, "pools").Select(ParsePool).OrderBy(pool => pool.Name, StringComparer.Ordinal)];
    }

    private static PoolStatus ParsePool(JsonElement pool)
    {
        var name = Text(pool, "name") ?? "";
        var vdevs = new List<Vdev>();
        // The root vdev carries the pool's name; its children are the top-level vdevs with their class.
        foreach (var root in Members(pool, "vdevs"))
        {
            vdevs.AddRange(Members(root, "vdevs").Select(vdev => ParseVdev(vdev, Role(Text(vdev, "class")))));
        }
        vdevs.AddRange(Members(pool, "logs").Select(vdev => ParseVdev(vdev, "log")));
        vdevs.AddRange(Members(pool, "l2cache").Select(vdev => ParseVdev(vdev, "cache")));
        vdevs.AddRange(Members(pool, "spares").Select(vdev => ParseVdev(vdev, "spare")));
        return new PoolStatus(
            name,
            pool.TryGetProperty("pool_guid", out var guid) ? guid.GetRawText().Trim('"') : "",
            Text(pool, "state") ?? "UNKNOWN",
            Number(pool, "error_count"),
            pool.TryGetProperty("scan_stats", out var scan) ? ParseScan(scan) : null,
            vdevs,
            pool.TryGetProperty("raidz_expand_stats", out var expansion) ? ParseExpansion(expansion, vdevs) : null);
    }

    private static Vdev ParseVdev(JsonElement vdev, string role)
    {
        var name = Text(vdev, "name") ?? "";
        var type = Text(vdev, "vdev_type") ?? "";
        return new Vdev(
            name,
            vdev.TryGetProperty("guid", out var guid) ? guid.GetRawText().Trim('"') : "",
            type,
            role,
            Text(vdev, "state") ?? "UNKNOWN",
            Text(vdev, "path"),
            type == "raidz" && RaidzName().Match(name) is { Success: true } match ? match.Groups[1].Value[0] - '0' : null,
            Number(vdev, "read_errors"),
            Number(vdev, "write_errors"),
            Number(vdev, "checksum_errors"),
            [.. Members(vdev, "vdevs").Select(child => ParseVdev(child, role))]);
    }

    private static string Role(string? vdevClass) => vdevClass switch
    {
        "log" or "logs" => "log",
        "special" => "special",
        "dedup" => "dedup",
        "spare" or "spares" => "spare",
        "l2cache" or "cache" => "cache",
        _ => "data",
    };

    private static PoolScan? ParseScan(JsonElement scan)
    {
        var kind = Text(scan, "function")?.ToLowerInvariant();
        if (kind is not ("scrub" or "resilver"))
        {
            return null;
        }
        return new PoolScan(
            kind,
            Text(scan, "state")?.ToLowerInvariant() ?? "finished",
            Number(scan, "scrub_pause") != 0,
            Time(Number(scan, "start_time")),
            Time(Number(scan, "end_time")),
            Number(scan, "to_examine"),
            Number(scan, "examined"),
            Number(scan, "errors"));
    }

    /// <summary>
    /// <c>raidz_expand_stats</c> names the growing vdev by its top-level index (<c>expanding_vdev</c>), which is also the
    /// number in its name: index 0 is <c>raidz1-0</c>.
    /// </summary>
    private static PoolExpansion ParseExpansion(JsonElement expansion, List<Vdev> vdevs)
    {
        var index = Number(expansion, "expanding_vdev");
        var vdev = vdevs.FirstOrDefault(candidate => candidate.Type == "raidz" && candidate.Name.EndsWith($"-{index}", StringComparison.Ordinal));
        return new PoolExpansion(
            Text(expansion, "state")?.ToLowerInvariant() ?? "finished",
            vdev?.VdevGuid,
            Time(Number(expansion, "start_time")),
            Time(Number(expansion, "end_time")),
            Number(expansion, "to_reflow"),
            Number(expansion, "reflowed"),
            Number(expansion, "waiting_for_resilver") != 0);
    }

    private static DateTimeOffset? Time(long seconds) => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    [GeneratedRegex(@"^raidz([1-3])")]
    private static partial Regex RaidzName();
}
