namespace Pithyx.Daemon.Storage;

/// <param name="MajorMinor">Device number of the mounted file system; ZFS and btrfs use anonymous numbers (<c>0:n</c>).</param>
/// <param name="Target">Mount point.</param>
/// <param name="FileSystem">File system type, for example <c>ext4</c> or <c>zfs</c>.</param>
/// <param name="Source">Mount source: a device path, or a dataset name for ZFS.</param>
public sealed record MountEntry(string MajorMinor, string Target, string FileSystem, string Source);

/// <summary>Parses <c>/proc/self/mountinfo</c> and <c>/proc/swaps</c>: plain files, so reading them starts no process.</summary>
public static class MountTable
{
    public static IReadOnlyList<MountEntry> ParseMountInfo(string text)
    {
        var mounts = new List<MountEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // 36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw,errors=continue
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }
            var fields = line[..separator].Split(' ');
            var tail = line[(separator + 3)..].Split(' ');
            if (fields.Length < 5 || tail.Length < 2)
            {
                continue;
            }
            mounts.Add(new MountEntry(fields[2], Unescape(fields[4]), tail[0], Unescape(tail[1])));
        }
        return mounts;
    }

    public static IReadOnlyList<string> ParseSwaps(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(line => Unescape(line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0]))];

    /// <summary>The mount that holds <paramref name="path"/>: the one with the longest matching mount point.</summary>
    public static MountEntry? Containing(IReadOnlyList<MountEntry> mounts, string path) =>
        mounts
            .Where(mount => path == mount.Target || mount.Target == "/" || path.StartsWith(mount.Target + "/", StringComparison.Ordinal))
            .OrderByDescending(mount => mount.Target.Length)
            .FirstOrDefault();

    // The kernel escapes space, tab, newline and backslash as octal: \040, \011, \012, \134.
    private static string Unescape(string value) =>
        value.Contains('\\', StringComparison.Ordinal)
            ? value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
                .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal)
            : value;
}
