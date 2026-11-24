namespace Pithyx.Daemon.Backups;

/// <summary>A shared folder or home now: its id, the name the backup app shows and its folder on the host.</summary>
public sealed record BackupShareRoot(long Id, string Name, string Path);

/// <summary>A folder a version holds: as the app shows it and as restic stored it.</summary>
public sealed record BackupVersionSource(string Name, string Path);

/// <summary>
/// Paths of versions (#395), pure. restic stores the live paths on the host; the app shows them as the shared
/// folder's or home's name and the path below it, from the shares as they are now.
/// </summary>
public static class BackupRestorePaths
{
    /// <summary>A stored path as the app shows it; unchanged when no share holds it now.</summary>
    public static string Name(string path, IReadOnlyList<BackupShareRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Place(path, roots) is { } place ? (place.Root.Name + "/" + place.Relative).TrimEnd('/') : path;
    }

    /// <summary>The share that holds a stored path now and the path below its folder (empty for the folder itself); null when none does.</summary>
    public static (BackupShareRoot Root, string Relative)? Place(string path, IReadOnlyList<BackupShareRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(roots);
        var root = roots
            .Where(root => Below(path, root.Path.TrimEnd('/')))
            .MaxBy(root => root.Path.TrimEnd('/').Length);
        return root is null ? null : (root, path[root.Path.TrimEnd('/').Length..].TrimStart('/'));
    }

    /// <summary>
    /// The stored path of a path as the app shows it, through the version's folders; null when no folder of the
    /// version holds it or it has an empty, <c>.</c> or <c>..</c> part.
    /// </summary>
    public static string? Stored(string name, IReadOnlyList<BackupVersionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(sources);
        var source = sources.Where(source => Below(name, source.Name)).MaxBy(source => source.Name.Length);
        if (source is null)
        {
            return null;
        }
        var rest = name[source.Name.Length..];
        if (rest.Length > 0 && rest[1..].Split('/').Any(part => part is "" or "." or ".." || part.Contains('\0', StringComparison.Ordinal)))
        {
            return null;
        }
        return source.Path + rest;
    }

    /// <summary>The path as the app shows it without slashes at either end.</summary>
    public static string Clean(string? name) => (name ?? "").Trim('/');

    /// <summary>The last part of a path.</summary>
    public static string Last(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    private static bool Below(string path, string folder) =>
        path == folder || (path.StartsWith(folder, StringComparison.Ordinal) && path.Length > folder.Length && (folder.EndsWith('/') || path[folder.Length] == '/'));
}
