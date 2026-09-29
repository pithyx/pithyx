using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pithyx.Daemon.Files.Thumbnails;

/// <summary>The two thumbnail sizes: rows of the list, tiles of the grid (both twice their CSS size for sharp screens).</summary>
public enum ThumbnailSize
{
    Small,
    Large,
}

/// <summary>
/// Thumbnails on the system disk (<c>/var/cache/pithyx/thumbnails</c>), one WebP file per key, never on a pool: a
/// cache hit never wakes a data disk. The key hashes the share's folder, the file's path, inode, size and change
/// time and the thumbnail size, so a changed file simply has another key; the old entry is never served again and
/// goes when the cache is over its limit, least recently used first. Access times live in memory only, to spare the
/// system disk writes; after a restart the files' write times stand in for them.
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>Changing how thumbnails look changes every key.</summary>
    public const int FormatVersion = 1;

    public const long DefaultLimitBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _directory;
    private readonly long _limitBytes;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private Dictionary<string, (long Bytes, DateTimeOffset Used)>? _index;
    private long _totalBytes;

    public ThumbnailCache(string directory, TimeProvider time, long limitBytes = DefaultLimitBytes)
    {
        _directory = directory;
        _limitBytes = limitBytes;
        _time = time;
    }

    public static int EdgeOf(ThumbnailSize size) => size == ThumbnailSize.Small ? 64 : 256;

    /// <summary>The key of a file as it is now; <paramref name="stat"/> must be the file's current stat.</summary>
    public static string KeyOf(string sharePath, string path, FsStat stat, ThumbnailSize size)
    {
        ArgumentNullException.ThrowIfNull(stat);
        var text = string.Join(
            '\n',
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            sharePath,
            path,
            stat.Id,
            stat.Size.ToString(CultureInfo.InvariantCulture),
            stat.Modified.UtcTicks.ToString(CultureInfo.InvariantCulture),
            stat.Changed.UtcTicks.ToString(CultureInfo.InvariantCulture),
            size.ToString());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                EnsureIndex();
                return _totalBytes;
            }
        }
    }

    /// <summary>The file of a cached thumbnail, or null; a hit counts as a use.</summary>
    public string? Find(string key)
    {
        lock (_lock)
        {
            var index = EnsureIndex();
            if (!index.TryGetValue(key, out var entry))
            {
                return null;
            }
            var path = PathOf(key);
            if (!File.Exists(path))
            {
                // Someone cleaned the cache folder: it is only a cache.
                index.Remove(key);
                _totalBytes -= entry.Bytes;
                return null;
            }
            index[key] = entry with { Used = _time.GetUtcNow() };
            return path;
        }
    }

    public bool Contains(string key)
    {
        lock (_lock)
        {
            return EnsureIndex().ContainsKey(key);
        }
    }

    /// <summary>Stores a thumbnail (written next to its place and renamed, so a reader never sees half a file), then evicts.</summary>
    public void Store(string key, byte[] webp)
    {
        ArgumentNullException.ThrowIfNull(webp);
        lock (_lock)
        {
            var index = EnsureIndex();
            Directory.CreateDirectory(_directory);
            var path = PathOf(key);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, webp);
            File.Move(temporary, path, overwrite: true);
            if (index.TryGetValue(key, out var old))
            {
                _totalBytes -= old.Bytes;
            }
            index[key] = (webp.Length, _time.GetUtcNow());
            _totalBytes += webp.Length;
            Evict(index);
        }
    }

    private void Evict(Dictionary<string, (long Bytes, DateTimeOffset Used)> index)
    {
        if (_totalBytes <= _limitBytes)
        {
            return;
        }
        // Down to 90 percent, so a full cache is not trimmed again with every new thumbnail.
        foreach (var (key, entry) in index.OrderBy(pair => pair.Value.Used).ToList())
        {
            if (_totalBytes <= _limitBytes * 9 / 10)
            {
                break;
            }
            try
            {
                File.Delete(PathOf(key));
            }
            catch (IOException)
            {
                continue;
            }
            index.Remove(key);
            _totalBytes -= entry.Bytes;
        }
    }

    private Dictionary<string, (long Bytes, DateTimeOffset Used)> EnsureIndex()
    {
        if (_index is not null)
        {
            return _index;
        }
        _index = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
        _totalBytes = 0;
        if (Directory.Exists(_directory))
        {
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
            {
                if (file.Extension == ".tmp")
                {
                    // Left by a daemon that stopped while writing.
                    file.Delete();
                    continue;
                }
                if (file.Extension != ".webp")
                {
                    continue;
                }
                _index[Path.GetFileNameWithoutExtension(file.Name)] = (file.Length, file.LastWriteTimeUtc);
                _totalBytes += file.Length;
            }
        }
        return _index;
    }

    private string PathOf(string key) => Path.Combine(_directory, key + ".webp");
}
