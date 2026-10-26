using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage.Zfs;

/// <summary>
/// The ZFS commands that take a pool's key (D384). The key always goes to stdin, which is never logged, never into an
/// argument; <c>keylocation</c> stays <c>prompt</c>, so the pool itself never names where a key is kept.
/// </summary>
public static class EncryptionCommands
{
    /// <summary>Options of <c>&lt;pool&gt;/pithyx</c>, the encryption root of an encrypted pool; the key follows on stdin.</summary>
    public static readonly IReadOnlyList<string> RootOptions = ["-o", "encryption=aes-256-gcm", "-o", "keyformat=hex", "-o", "keylocation=prompt"];

    /// <summary>The encryption root of a pool Pithyx set up.</summary>
    public static string Root(string pool) => $"{pool}/pithyx";

    public static string Input(string hexKey) => hexKey + "\n";

    /// <summary>Loads the key, after which the datasets can be mounted.</summary>
    public static CommandRequest LoadKey(string root, string hexKey) => new("zfs", ["load-key", root], StandardInput: Input(hexKey));

    /// <summary>
    /// Checks a key without loading it (<c>zfs load-key -n</c>); unlike a load it also answers while the key is loaded, so
    /// a key can be checked on an unlocked pool before it goes into the vault or onto a stick.
    /// </summary>
    public static CommandRequest CheckKey(string root, string hexKey) => new("zfs", ["load-key", "-n", root], StandardInput: Input(hexKey));

    /// <summary>Wraps the pool's data key with a new key; the old key stops working. Needs the key loaded.</summary>
    public static CommandRequest ChangeKey(string root, string hexKey) =>
        new("zfs", ["change-key", "-o", "keyformat=hex", "-o", "keylocation=prompt", root], StandardInput: Input(hexKey));

    public static CommandRequest Mount(string dataset) => new("zfs", ["mount", dataset]);

    /// <summary>True when ZFS refused the key itself, not the command.</summary>
    public static bool IsWrongKey(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ExitCode != 0 && result.StandardError.Contains("Incorrect key provided", StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds <see cref="Pool.Encryption"/> to every pool whose <c>&lt;pool&gt;/pithyx</c> is an encryption root. Pure: the
    /// caller says what the box knows about each pool's keys, by pool GUID.
    /// </summary>
    public static PoolList Apply(PoolList pools, Func<string, KeySources> sources)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(sources);
        var roots = pools.Datasets
            .Where(dataset => dataset.Encryption != "off" && dataset.EncryptionRoot == dataset.Name && dataset.Name == Root(dataset.Pool))
            .ToDictionary(dataset => dataset.Pool, StringComparer.Ordinal);
        return pools with
        {
            Pools = [.. pools.Pools.Select(pool => roots.TryGetValue(pool.Name, out var root)
                ? pool with { Encryption = Encryption(root.KeyStatus != "available", sources(pool.PoolGuid)) }
                : pool with { Encryption = null })],
        };
    }

    // A failure is only one while the pool is locked, and an unlock from a stick only while it is unlocked.
    private static PoolEncryption Encryption(bool locked, KeySources sources) => new(
        locked,
        sources.InVault,
        locked && sources.InVault && sources.VaultFailed,
        sources.Usb,
        !locked && sources.UnlockedFromUsb,
        locked && sources.UsbFailed);
}

/// <summary>What the box knows about one pool's keys (D384, D385).</summary>
/// <param name="InVault">The key vault holds its key.</param>
/// <param name="VaultFailed">The vault's key was tried and failed.</param>
/// <param name="Usb">The stick its key was last written to.</param>
/// <param name="UnlockedFromUsb">A stick unlocked it since the start.</param>
/// <param name="UsbFailed">A key file on a stick was tried and failed.</param>
public readonly record struct KeySources(bool InVault, bool VaultFailed, PoolUsbKey? Usb = null, bool UnlockedFromUsb = false, bool UsbFailed = false);
