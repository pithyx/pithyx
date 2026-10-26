using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Storage.Encryption;

/// <summary>
/// The key vault on the system disk (D384): one file per pool, named by the pool's GUID so a renamed or re-imported pool
/// keeps its key, holding the key as ZFS takes it. The folder is root's alone (0700), each file 0600. It protects against
/// stolen disks, not against a stolen box. It is never part of the configuration backup and never written to a pool.
/// </summary>
/// <param name="directory"><c>/var/lib/pithyx/pool-keys</c> on a box.</param>
public sealed partial class KeyVault(string directory)
{
    public string Directory { get; } = directory;

    public bool Has(string poolGuid) => File.Exists(PathOf(poolGuid));

    /// <summary>The pool's key in hex, or null when the vault has none.</summary>
    public string? Read(string poolGuid)
    {
        var path = PathOf(poolGuid);
        if (!File.Exists(path))
        {
            return null;
        }
        var key = File.ReadAllText(path).Trim();
        return HexKey().IsMatch(key) ? key : null;
    }

    /// <summary>Stores the key, replacing an older one in one step: the file is written beside it and then renamed.</summary>
    public void Save(string poolGuid, string hexKey)
    {
        if (!HexKey().IsMatch(hexKey))
        {
            throw new ArgumentException("Not a hex pool key.", nameof(hexKey));
        }
        var path = PathOf(poolGuid);
        System.IO.Directory.CreateDirectory(Directory);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        var temporary = $"{path}.new";
        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(hexKey);
            writer.Write('\n');
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Deletes the pool's key; nothing happens when the vault has none.</summary>
    public void Remove(string poolGuid) => File.Delete(PathOf(poolGuid));

    private string PathOf(string poolGuid) =>
        PoolGuid().IsMatch(poolGuid ?? "")
            ? Path.Combine(Directory, $"{poolGuid}.key")
            : throw new ArgumentException($"Not a pool GUID: {poolGuid}.", nameof(poolGuid));

    [GeneratedRegex("^[0-9]{1,20}$")]
    private static partial Regex PoolGuid();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexKey();
}
