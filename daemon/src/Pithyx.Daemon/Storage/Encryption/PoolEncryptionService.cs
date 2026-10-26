using System.Threading.Channels;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Storage.Encryption;

/// <summary>
/// Unlocks encrypted pools, changes their keys and keeps the key vault (D384). At start, and whenever a pool appears
/// locked (an import), it unlocks every pool whose key is in the vault, once per appearance. Unlocking loads the key and
/// mounts the datasets of <c>&lt;pool&gt;/pithyx</c> that are safe to mount, by the rules of an import; the pools are
/// then read again, and shares, homes and apps follow on their own. One key action runs at a time.
/// </summary>
public sealed partial class PoolEncryptionService(
    ICommandRunner commands,
    PoolInventory pools,
    SystemStateService state,
    StorageFacts facts,
    KeyVault vault,
    ILogger<PoolEncryptionService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // Pools the vault was tried on since they appeared locked; a pool that is gone or unlocked is tried again next time.
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

    /// <summary>Loads the pool's key and mounts its datasets.</summary>
    /// <param name="poolName">The pool.</param>
    /// <param name="typedKey">The key as typed, or null to take it from the vault.</param>
    /// <param name="keepInVault">With a typed key: keep it in the vault once it opened the pool.</param>
    /// <param name="cancellationToken">Stops waiting for another key action.</param>
    public async Task UnlockAsync(string poolName, string? typedKey, bool keepInVault, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (pool, encryption) = Encrypted(poolName);
            if (!encryption.Locked)
            {
                // ZFS cannot check a key against a loaded one, so an unchecked key never goes into the vault.
                if (typedKey is not null || keepInVault)
                {
                    throw new PithyxException(ErrorCodes.StoragePoolNotLocked, Name(pool));
                }
            }
            else
            {
                var hexKey = typedKey is null
                    ? vault.Read(pool.PoolGuid) ?? throw new PithyxException(ErrorCodes.StorageEncryptionKeyRequired, Name(pool))
                    : PoolKey.ToHex(typedKey);
                var result = await commands.RunAsync(EncryptionCommands.LoadKey(EncryptionCommands.Root(pool.Name), hexKey), CancellationToken.None);
                if (EncryptionCommands.IsWrongKey(result))
                {
                    throw new PithyxException(ErrorCodes.StorageEncryptionKeyWrong, Name(pool));
                }
                Require(result, "zfs");
                if (typedKey is not null && keepInVault)
                {
                    vault.Save(pool.PoolGuid, hexKey);
                }
                LogUnlocked(pool.Name, typedKey is null ? "vault" : "typed");
            }
            await MountAsync(pool.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the pool's key with a new one the user stored and confirmed; the old key stops working. The vault then
    /// holds the new key, or none.
    /// </summary>
    public async Task ChangeKeyAsync(string poolName, string typedKey, bool keepInVault, CancellationToken cancellationToken)
    {
        var hexKey = PoolKey.ToHex(typedKey);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (pool, encryption) = Encrypted(poolName);
            if (encryption.Locked)
            {
                throw new PithyxException(ErrorCodes.StoragePoolLocked, Name(pool));
            }
            Require(await commands.RunAsync(EncryptionCommands.ChangeKey(EncryptionCommands.Root(pool.Name), hexKey), CancellationToken.None), "zfs");
            try
            {
                if (keepInVault)
                {
                    vault.Save(pool.PoolGuid, hexKey);
                }
                else
                {
                    vault.Remove(pool.PoolGuid);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The old key in the vault no longer opens the pool; without a key the pool waits for the typed one at start.
                vault.Remove(pool.PoolGuid);
                throw;
            }
            LogKeyChanged(pool.Name, keepInVault);
            state.PublishVault();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes the pool's key out of the vault: from the next start on, the pool waits for its key to be typed.</summary>
    public async Task ForgetKeyAsync(string poolName, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (pool, _) = Encrypted(poolName);
            vault.Remove(pool.PoolGuid);
            LogKeyForgotten(pool.Name);
            state.PublishVault();
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Signal() => _changes.Writer.TryWrite(true);
        pools.Changed += Signal;
        try
        {
            Signal();
            await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
            {
                await UnlockFromVaultAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            pools.Changed -= Signal;
        }
    }

    /// <summary>Unlocks every locked pool whose key is in the vault and that was not tried since it appeared locked.</summary>
    internal async Task UnlockFromVaultAsync(CancellationToken cancellationToken)
    {
        var locked = pools.Current.Pools.Where(pool => pool.Encryption is { Locked: true } && !pool.ReadOnly).ToList();
        _tried.IntersectWith(locked.Select(pool => pool.PoolGuid));
        foreach (var pool in locked.Where(pool => pool.Encryption is { KeyInVault: true } && _tried.Add(pool.PoolGuid)))
        {
            try
            {
                await UnlockAsync(pool.Name, null, false, cancellationToken);
            }
            catch (PithyxException exception)
            {
                LogVaultUnlockFailed(pool.Name, exception.Code);
            }
        }
    }

    /// <summary>
    /// Mounts what an import would mount below the encryption root and is not mounted yet: never a dataset that would hide
    /// part of the system or something mounted already (<see cref="ImportPlans.DecideMounts"/>).
    /// </summary>
    private async Task MountAsync(string poolName)
    {
        var root = EncryptionCommands.Root(poolName);
        var properties = await commands.RunAsync(ImportPlans.MountProperties(poolName), CancellationToken.None);
        Require(properties, "zfs");
        var mounted = pools.Current.Datasets.Where(dataset => dataset.Mounted).Select(dataset => dataset.Name).ToHashSet(StringComparer.Ordinal);
        var below = ZfsDatasetParser.ParseProperties(properties.StandardOutput)
            .Where(entry => (entry.Key == root || entry.Key.StartsWith(root + "/", StringComparison.Ordinal)) && !mounted.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var mounts = ImportPlans.DecideMounts(below, facts.Current().Mounts);
        var failed = new List<string>();
        var exitCode = 0;
        foreach (var dataset in mounts.Mount)
        {
            var result = await commands.RunAsync(EncryptionCommands.Mount(dataset), CancellationToken.None);
            if (result.ExitCode != 0)
            {
                LogMountFailed(dataset, result.ExitCode, result.StandardError);
                failed.Add(dataset);
                exitCode = result.ExitCode;
            }
        }
        foreach (var dataset in mounts.Unmounted)
        {
            LogLeftUnmounted(dataset.Name, dataset.Reason);
        }
        await state.RefreshPoolsAsync(CancellationToken.None);
        if (failed.Count > 0)
        {
            throw new PithyxException(ErrorCodes.StorageCommandFailed, new Dictionary<string, object?> { ["command"] = "zfs", ["exitCode"] = exitCode, ["stderr"] = $"mount failed: {string.Join(", ", failed)}" });
        }
    }

    private (Pool Pool, PoolEncryption Encryption) Encrypted(string poolName)
    {
        var pool = pools.Current.Pools.FirstOrDefault(candidate => candidate.Name == poolName)
            ?? throw new PithyxException(ErrorCodes.StoragePoolNotFound, new Dictionary<string, object?> { ["name"] = poolName });
        if (pool.Encryption is not { } encryption)
        {
            throw new PithyxException(ErrorCodes.StoragePoolNotEncrypted, Name(pool));
        }
        if (pool.ReadOnly)
        {
            throw new PithyxException(ErrorCodes.StoragePoolReadOnly, Name(pool));
        }
        if (PoolHealth.Unreachable(pool) is { } code)
        {
            throw new PithyxException(code, Name(pool));
        }
        return (pool, encryption);
    }

    private static Dictionary<string, object?> Name(Pool pool) => new() { ["name"] = pool.Name };

    private static void Require(CommandResult result, string command)
    {
        if (result.ExitCode != 0)
        {
            throw new PithyxException(
                ErrorCodes.StorageCommandFailed,
                new Dictionary<string, object?> { ["command"] = command, ["exitCode"] = result.ExitCode, ["stderr"] = result.StandardError.Trim() });
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pool {Pool} unlocked with the key from the {Source}")]
    private partial void LogUnlocked(string pool, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pool {Pool} has a new key; kept in the key vault: {Kept}")]
    private partial void LogKeyChanged(string pool, bool kept);

    [LoggerMessage(Level = LogLevel.Information, Message = "The key of pool {Pool} left the key vault")]
    private partial void LogKeyForgotten(string pool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pool {Pool} stays locked: its key from the key vault failed with {Code}")]
    private partial void LogVaultUnlockFailed(string pool, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mounting {Dataset} after the unlock failed with {ExitCode}: {StandardError}")]
    private partial void LogMountFailed(string dataset, int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Dataset} stays unmounted after the unlock: {Reason}")]
    private partial void LogLeftUnmounted(string dataset, string reason);
}
