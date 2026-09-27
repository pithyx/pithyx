using static Pithyx.Daemon.Storage.Zfs.ZfsJson;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>Sizes and flags of one pool from <c>zpool list</c>.</summary>
public sealed record PoolUsage(string Name, long SizeBytes, long AllocatedBytes, long FreeBytes, int CapacityPercent, int? FragmentationPercent, bool ReadOnly);

/// <summary>Parses <c>zpool list -j --json-int -o name,size,allocated,free,capacity,fragmentation,health,readonly</c>.</summary>
public static class ZpoolListParser
{
    public static IReadOnlyList<PoolUsage> Parse(string json)
    {
        using var document = ZfsJson.Parse(json);
        if (document is null)
        {
            return [];
        }
        return [.. Members(document.RootElement, "pools")
            .Select(pool => new PoolUsage(
                Text(pool, "name") ?? "",
                PropertyNumber(pool, "size"),
                PropertyNumber(pool, "allocated"),
                PropertyNumber(pool, "free"),
                (int)PropertyNumber(pool, "capacity"),
                Property(pool, "fragmentation") is { } fragmentation && AsNumber(fragmentation) is { } percent ? (int)percent : null,
                PropertyText(pool, "readonly") == "on"))
            .OrderBy(pool => pool.Name, StringComparer.Ordinal)];
    }
}
