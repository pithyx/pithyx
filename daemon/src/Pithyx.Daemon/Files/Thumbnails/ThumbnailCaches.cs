using System.Collections.Concurrent;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Files.Thumbnails;

/// <summary>
/// One thumbnail cache per pool, in the pool's own dataset <c>&lt;pool&gt;/pithyx/cache</c> (mounted at
/// <c>/&lt;pool&gt;/.pithyx/cache</c>, never shown in the UI), replacing the cache on the system disk (D255). Thumbnails
/// leave with their pool when it is destroyed or exported, never fill the system disk, and a pool that is gone takes
/// its thumbnails along. A share's pool is the one whose mounted dataset holds the share's folder. Folder shares outside
/// a pool exist only on development machines; they use the development cache folder, elsewhere they get none.
/// </summary>
public sealed class ThumbnailCaches : IDisposable
{
    public const string DatasetName = "cache";
    public const string Folder = "thumbnails";

    private readonly PoolInventory _pools;
    private readonly TimeProvider _time;
    private readonly string? _fallbackDirectory;
    private readonly ConcurrentDictionary<string, ThumbnailCache> _caches = new(StringComparer.Ordinal);

    public ThumbnailCaches(PoolInventory pools, TimeProvider time, string? fallbackDirectory)
    {
        ArgumentNullException.ThrowIfNull(pools);
        _pools = pools;
        _time = time;
        _fallbackDirectory = fallbackDirectory;
        _pools.Changed += ForgetGonePools;
    }

    public static string DatasetOf(string pool) => $"{pool}/pithyx/{DatasetName}";

    /// <summary>The cache for files of the share at <paramref name="sharePath"/>, or null when its pool has none (yet).</summary>
    public ThumbnailCache? For(string sharePath)
    {
        var directory = DirectoryFor(_pools.Current, sharePath, _fallbackDirectory);
        return directory is null ? null : _caches.GetOrAdd(directory, path => new ThumbnailCache(path, _time));
    }

    /// <summary>
    /// Where the thumbnails of a share's files go: the cache dataset of the pool that holds the share, null when that
    /// dataset is missing or not mounted, and <paramref name="fallback"/> when no pool holds the share.
    /// </summary>
    public static string? DirectoryFor(PoolList pools, string sharePath, string? fallback)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(sharePath);
        var holder = pools.Datasets
            .Where(dataset => dataset.Type == "filesystem" && dataset.Mounted && dataset.MountPoint is { } mount && IsAtOrBelow(sharePath, mount))
            .MaxBy(dataset => dataset.MountPoint!.Length);
        if (holder is null)
        {
            return fallback;
        }
        var cache = pools.Datasets.FirstOrDefault(dataset => dataset.Name == DatasetOf(holder.Pool));
        return cache is { Mounted: true, MountPoint: { } cacheMount } ? Path.Combine(cacheMount, Folder) : null;
    }

    private static bool IsAtOrBelow(string path, string mount) =>
        mount == "/" || path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal);

    /// <summary>A pool that was exported or destroyed must not leave a stale index behind when it comes back.</summary>
    private void ForgetGonePools()
    {
        var current = _pools.Current.Datasets
            .Where(dataset => dataset.Name.EndsWith("/pithyx/" + DatasetName, StringComparison.Ordinal) && dataset is { Mounted: true, MountPoint: not null })
            .Select(dataset => Path.Combine(dataset.MountPoint!, Folder))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var directory in _caches.Keys)
        {
            if (directory != _fallbackDirectory && !current.Contains(directory))
            {
                _caches.TryRemove(directory, out _);
            }
        }
    }

    public void Dispose() => _pools.Changed -= ForgetGonePools;
}
