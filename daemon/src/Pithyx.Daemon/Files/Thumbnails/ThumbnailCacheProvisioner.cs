using System.Threading.Channels;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Files.Thumbnails;

/// <summary>
/// Gives pools that were set up before the cache dataset existed their <c>&lt;pool&gt;/pithyx/cache</c> (D255), once per
/// pool and daemon run, and removes the old thumbnail folder on the system disk. It is the only dataset the daemon
/// creates without a plan the user saw: it holds nothing but rebuildable thumbnails, and only pools with the Pithyx
/// layout that are writable and unlocked get it. New pools get it with their layout (<see cref="ZfsPlans.Datasets"/>).
/// </summary>
public sealed partial class ThumbnailCacheProvisioner(
    PoolInventory pools,
    SystemStateService state,
    ICommandRunner commands,
    DaemonPaths paths,
    DeploymentEnvironment environment,
    ILogger<ThumbnailCacheProvisioner> logger) : BackgroundService
{
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

    /// <summary>Pools with the Pithyx layout that lack the cache dataset and can take it now.</summary>
    public static IEnumerable<Pool> Missing(PoolList current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var datasets = current.Datasets.ToDictionary(dataset => dataset.Name, StringComparer.Ordinal);
        return current.Pools.Where(pool =>
            pool is { ReadOnly: false, State: "ONLINE" or "DEGRADED" }
            && datasets.TryGetValue($"{pool.Name}/pithyx", out var layout)
            && layout.KeyStatus is null or "available"
            && !datasets.ContainsKey(ThumbnailCaches.DatasetOf(pool.Name)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RemoveSystemDiskCache();
        void Signal() => _changes.Writer.TryWrite(true);
        pools.Changed += Signal;
        try
        {
            Signal();
            await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
            {
                var created = false;
                foreach (var pool in Missing(pools.Current).Where(pool => _tried.Add(pool.PoolGuid)).ToList())
                {
                    var result = await commands.RunAsync(ZfsPlans.CreateCacheDataset(pool.Name), stoppingToken);
                    if (result.ExitCode == 0)
                    {
                        LogCreated(pool.Name);
                        created = true;
                    }
                    else
                    {
                        LogFailed(pool.Name, result.StandardError);
                    }
                }
                if (created)
                {
                    await state.RefreshPoolsAsync(stoppingToken);
                }
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

    /// <summary>Thumbnails lived in /var/cache/pithyx/thumbnails before D255; development machines keep it for folder shares.</summary>
    private void RemoveSystemDiskCache()
    {
        var old = Path.Combine(paths.CacheDirectory, ThumbnailCaches.Folder);
        if (environment.Kind == DeploymentKind.Dev || !Directory.Exists(old))
        {
            return;
        }
        try
        {
            Directory.Delete(old, recursive: true);
            LogRemovedOldCache(old);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogRemoveFailed(old, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the cache dataset of pool {Pool}")]
    private partial void LogCreated(string pool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create the cache dataset of pool {Pool}: {Error}")]
    private partial void LogFailed(string pool, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the old thumbnail cache {Directory} from the system disk")]
    private partial void LogRemovedOldCache(string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove the old thumbnail cache {Directory}")]
    private partial void LogRemoveFailed(string directory, Exception exception);
}
