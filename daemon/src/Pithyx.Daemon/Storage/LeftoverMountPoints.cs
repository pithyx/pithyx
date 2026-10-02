namespace Pithyx.Daemon.Storage;

/// <summary>
/// The empty folders a destroyed or exported pool leaves on the system disk. Its root is not mounted (<c>canmount=off</c>),
/// so ZFS creates the mount points of the datasets below it as plain folders there; a new pool with the same name would
/// then fail with "mountpoint exists and is not empty". Only the pool's mount points and the folders between them are
/// considered, and only empty ones are removed, never anything with content.
/// </summary>
public static class LeftoverMountPoints
{
    /// <summary>The mount points and the folders between them, deepest first.</summary>
    public static IReadOnlyList<string> Of(IEnumerable<string> mountPoints)
    {
        ArgumentNullException.ThrowIfNull(mountPoints);
        var roots = mountPoints
            .Where(path => path.StartsWith('/') && path != "/")
            .Select(path => path.TrimEnd('/'))
            .ToHashSet(StringComparer.Ordinal);
        var folders = new HashSet<string>(roots, StringComparer.Ordinal);
        foreach (var mountPoint in roots)
        {
            // A folder between two mount points, like /tank/.pithyx between /tank and /tank/.pithyx/cache.
            for (var parent = Path.GetDirectoryName(mountPoint); parent is not null && parent != "/"; parent = Path.GetDirectoryName(parent))
            {
                if (roots.Any(root => parent.StartsWith(root + "/", StringComparison.Ordinal)))
                {
                    folders.Add(parent);
                }
            }
        }
        return [.. folders.OrderByDescending(folder => folder.Count(character => character == '/')).ThenBy(folder => folder, StringComparer.Ordinal)];
    }

    /// <summary>Removes the folders that exist and are empty; a folder with content or still mounted stays.</summary>
    public static void Remove(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder, recursive: false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Busy (something is mounted there) or not ours to remove; the folder stays.
            }
        }
    }
}
