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
    /// caller says which pool GUIDs the key vault holds and which of those keys failed.
    /// </summary>
    public static PoolList Apply(PoolList pools, Func<string, bool> inVault, Func<string, bool>? vaultFailed = null)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(inVault);
        vaultFailed ??= _ => false;
        var roots = pools.Datasets
            .Where(dataset => dataset.Encryption != "off" && dataset.EncryptionRoot == dataset.Name && dataset.Name == Root(dataset.Pool))
            .ToDictionary(dataset => dataset.Pool, StringComparer.Ordinal);
        return pools with
        {
            Pools = [.. pools.Pools.Select(pool => roots.TryGetValue(pool.Name, out var root)
                ? pool with { Encryption = Encryption(root.KeyStatus != "available", inVault(pool.PoolGuid), vaultFailed(pool.PoolGuid)) }
                : pool with { Encryption = null })],
        };
    }

    // An unlocked pool has no failed vault key, whatever was tried before it was unlocked by hand.
    private static PoolEncryption Encryption(bool locked, bool inVault, bool failed) => new(locked, inVault, locked && inVault && failed);
}
