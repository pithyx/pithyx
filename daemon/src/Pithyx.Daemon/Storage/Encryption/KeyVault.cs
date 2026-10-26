using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Storage.Encryption;

/// <summary>
/// The key vault on the system disk (D384): one file per pool, named by the pool's GUID so a renamed or re-imported pool
/// keeps its key, holding the key as ZFS takes it. The folder is root's alone (0700), each file 0600. It protects against
/// stolen disks, not against a stolen box. It is never part of the configuration backup and never written to a pool.
/// Beside the keys it notes which USB stick a pool's key was written to (<c>&lt;pool GUID&gt;.usb</c>, D385), and it keeps
/// in memory what happened with the keys since the start.
/// </summary>
/// <param name="directory"><c>/var/lib/pithyx/pool-keys</c> on a box.</param>
public sealed partial class KeyVault(string directory)
{
    // Pools whose key from the vault did not unlock them since the key was last stored; in memory, a restart tries again.
    private readonly ConcurrentDictionary<string, bool> _failed = new(StringComparer.Ordinal);

    // Pools whose key file on a USB stick did not unlock them, and pools a stick unlocked; in memory, since the start.
    private readonly ConcurrentDictionary<string, bool> _usbFailed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _unlockedFromUsb = new(StringComparer.Ordinal);

    public string Directory { get; } = directory;

    public bool Has(string poolGuid) => File.Exists(PathOf(poolGuid));

    /// <summary>True when the vault's key of the pool was tried and did not unlock it (wrong, unreadable or damaged).</summary>
    public bool Failed(string poolGuid) => _failed.ContainsKey(poolGuid ?? "");

    /// <summary>The GUIDs of the pools whose vault key failed.</summary>
    public IReadOnlyList<string> FailedPools => [.. _failed.Keys];

    public void MarkFailed(string poolGuid) => _failed[PoolGuidOf(poolGuid)] = true;

    public void ClearFailed(string poolGuid) => _failed.TryRemove(poolGuid ?? "", out _);

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
        CreateDirectory();
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
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
        ClearFailed(poolGuid);
    }

    /// <summary>Deletes the pool's key; nothing happens when the vault has none.</summary>
    public void Remove(string poolGuid)
    {
        var path = PathOf(poolGuid);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        ClearFailed(poolGuid);
    }

    /// <summary>Forgets everything about a destroyed pool: its key, the note of its USB stick and what was tried.</summary>
    public void RemovePool(string poolGuid)
    {
        Remove(poolGuid);
        RemoveUsb(poolGuid);
        _usbFailed.TryRemove(poolGuid, out _);
        _unlockedFromUsb.TryRemove(poolGuid, out _);
    }

    /// <summary>The USB stick the pool's key was last written to, or null; a note that cannot be read counts as none.</summary>
    public PoolUsbKey? Usb(string poolGuid)
    {
        var path = UsbPathOf(poolGuid);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PoolUsbKey>(File.ReadAllText(path), _json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Notes the stick the pool's key was written to, replacing an earlier note.</summary>
    public void SaveUsb(string poolGuid, PoolUsbKey usb)
    {
        var path = UsbPathOf(poolGuid);
        CreateDirectory();
        var temporary = $"{path}.new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(usb, _json));
        File.Move(temporary, path, overwrite: true);
        _usbFailed.TryRemove(poolGuid, out _);
    }

    /// <summary>Drops the note of the pool's stick, for example after a key change left the stick with the old key.</summary>
    public void RemoveUsb(string poolGuid)
    {
        var path = UsbPathOf(poolGuid);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public bool UsbFailed(string poolGuid) => _usbFailed.ContainsKey(poolGuid ?? "");

    public void MarkUsbFailed(string poolGuid) => _usbFailed[PoolGuidOf(poolGuid)] = true;

    public bool UnlockedFromUsb(string poolGuid) => _unlockedFromUsb.ContainsKey(poolGuid ?? "");

    public void MarkUnlockedFromUsb(string poolGuid)
    {
        _unlockedFromUsb[PoolGuidOf(poolGuid)] = true;
        _usbFailed.TryRemove(poolGuid, out _);
    }

    /// <summary>
    /// Forgets what happened with the USB keys of pools that are gone or no longer in the given state: a stick's failure
    /// once the pool is unlocked, and the unlock from a stick once the pool is locked again.
    /// </summary>
    public void SettleUsb(IReadOnlySet<string> locked, IReadOnlySet<string> unlocked)
    {
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentNullException.ThrowIfNull(unlocked);
        foreach (var guid in _usbFailed.Keys.Where(guid => !locked.Contains(guid)))
        {
            _usbFailed.TryRemove(guid, out _);
        }
        foreach (var guid in _unlockedFromUsb.Keys.Where(guid => !unlocked.Contains(guid)))
        {
            _unlockedFromUsb.TryRemove(guid, out _);
        }
    }

    /// <summary>What the box knows about the pool's keys, for <see cref="Zfs.EncryptionCommands.Apply"/>.</summary>
    public Zfs.KeySources Sources(string poolGuid) =>
        new(Has(poolGuid), Failed(poolGuid), Usb(poolGuid), UnlockedFromUsb(poolGuid), UsbFailed(poolGuid));

    private void CreateDirectory()
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private string PathOf(string poolGuid) => Path.Combine(Directory, $"{PoolGuidOf(poolGuid)}.key");

    private string UsbPathOf(string poolGuid) => Path.Combine(Directory, $"{PoolGuidOf(poolGuid)}.usb");

    private static string PoolGuidOf(string poolGuid) =>
        PoolGuid().IsMatch(poolGuid ?? "") ? poolGuid! : throw new ArgumentException($"Not a pool GUID: {poolGuid}.", nameof(poolGuid));

    [GeneratedRegex("^[0-9]{1,20}$")]
    private static partial Regex PoolGuid();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexKey();
}
