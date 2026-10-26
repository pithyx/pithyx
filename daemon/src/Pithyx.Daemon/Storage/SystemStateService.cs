using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Storage;

/// <summary>
/// Keeps <see cref="DiskInventory"/> and <see cref="PoolInventory"/> current. Reads disks and pools once at startup,
/// the disks again after udev reports a block device event and the pools after the storage backend reports one
/// (<c>zpool events</c>) or a disk came or went; there is no polling. A monitor that stops is restarted with backoff, and
/// its state is read again so no change is missed. Disks that appear or change are probed for signatures, except pool
/// members. The pools' health is derived with <see cref="PoolHealth"/> from what ZFS reports and which disks are
/// connected, so a pulled disk degrades its pool at once, and a disk that comes back is brought back into its pool.
/// Pool reads run one at a time: a <c>zpool</c> command that hangs never piles up behind itself.
/// </summary>
public sealed partial class SystemStateService(
    ICommandRunner commands,
    DiskInventory inventory,
    PoolInventory pools,
    IStorageBackend storage,
    DiskProbe probe,
    StorageFacts facts,
    TopicHub hub,
    TimeProvider time,
    ILogger<SystemStateService> logger,
    StorageUsage? usage = null,
    Encryption.KeyVault? vault = null) : BackgroundService
{
    public static readonly CommandRequest Lsblk = new("lsblk", ["-J", "-b", "-O"]);
    public static readonly CommandRequest UdevMonitor = new("udevadm", ["monitor", "--udev", "--subsystem-match=block"]);

    /// <summary>Events arrive in bursts (a disk and its partitions); one read covers the whole burst.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a read of the used space waits after the last write: ZFS updates it when it syncs a transaction group,
    /// every 5 seconds by default. A long run of writes is read at the latest <see cref="UsageMaxWait"/> after its first.
    /// </summary>
    public static readonly TimeSpan UsageDelay = TimeSpan.FromSeconds(6);

    public static readonly TimeSpan UsageMaxWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(1);
    private readonly Channel<bool> _usageChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _poolChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _poolRead = new(1, 1);
    private readonly Lock _health = new();
    private PoolList? _lastRead;

    // Leaf GUIDs that went missing and were not brought back yet, the serials of the disks leaves were last seen on,
    // and the reconnects tried since the disks last changed (each is tried once per change, never in a loop).
    private readonly HashSet<string> _reconnecting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _serials = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // The first reads complete before the daemon accepts requests, so GET /api/disks is never empty by accident.
        // Pools come first: their members are never probed.
        await RefreshPoolsAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        if (usage is not null)
        {
            usage.Changed += OnWritten;
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var monitors = Task.WhenAll(
            MonitorAsync("udevadm monitor", token => UdevLinesAsync(token), _changes, stoppingToken),
            MonitorAsync("zpool events", storage.WatchAsync, _poolChanges, stoppingToken),
            ConsumeAsync(_poolChanges, RefreshPoolsAsync, stoppingToken),
            ConsumeUsageAsync(stoppingToken));
        await ConsumeAsync(_changes, RefreshAsync, stoppingToken);
        await monitors;
    }

    private async Task ConsumeAsync(Channel<bool> changes, Func<CancellationToken, Task> refresh, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var change in changes.Reader.ReadAllAsync(stoppingToken))
            {
                await Task.Delay(Debounce, time, stoppingToken);
                changes.Reader.TryRead(out var _);
                await refresh(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (usage is not null)
        {
            usage.Changed -= OnWritten;
        }
        return base.StopAsync(cancellationToken);
    }

    private void OnWritten() => _usageChanges.Writer.TryWrite(true);

    /// <summary>Reads the pools again after writes, once per burst (<see cref="UsageDelay"/>), so used space and fill alerts follow.</summary>
    private async Task ConsumeUsageAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var written in _usageChanges.Reader.ReadAllAsync(stoppingToken))
            {
                var first = time.GetUtcNow();
                do
                {
                    await Task.Delay(UsageDelay, time, stoppingToken);
                }
                while (_usageChanges.Reader.TryRead(out _) && time.GetUtcNow() - first < UsageMaxWait);
                var parent = Activity.Current;
                Activity.Current = null;
                using (Tracing.Source.StartActivity("storage.usage.refresh"))
                {
                    await _poolRead.WaitAsync(stoppingToken);
                    try
                    {
                        await ReadPoolsAsync(stoppingToken);
                        // Only sizes changed: the snapshot list is not read again for it.
                        PublishPools(read: false);
                    }
                    finally
                    {
                        _poolRead.Release();
                        Activity.Current = parent;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async IAsyncEnumerable<string> UdevLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in commands.StreamLinesAsync(UdevMonitor, cancellationToken))
        {
            if (UdevEvent().IsMatch(line))
            {
                yield return line;
            }
        }
    }

    private async Task MonitorAsync(string name, Func<CancellationToken, IAsyncEnumerable<string>> events, Channel<bool> changes, CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var line in events(stoppingToken))
                {
                    changes.Writer.TryWrite(true);
                    backoff = TimeSpan.FromSeconds(1);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogMonitorFailed(name, exception);
            }
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            LogMonitorRestart(name, backoff.TotalSeconds);
            try
            {
                await Task.Delay(backoff, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = backoff * 2 < _maxBackoff ? backoff * 2 : _maxBackoff;
            // Events may have been missed while the monitor was down.
            changes.Writer.TryWrite(true);
        }
    }

    /// <summary>Raised for every block device event udev reports, before the disks are read again (the system mirror listens).</summary>
    public event Action? BlockDevicesChanged;

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        BlockDevicesChanged?.Invoke();
        // Each read is its own trace, so the resulting event and the lsblk call share one request id.
        var parent = Activity.Current;
        Activity.Current = null;
        using var activity = Tracing.Source.StartActivity("storage.disks.refresh");
        try
        {
            var result = await commands.RunAsync(Lsblk, cancellationToken);
            if (result.ExitCode != 0)
            {
                LogLsblkFailed(result.ExitCode, result.StandardError);
                return;
            }
            var parsed = LsblkParser.ParseWithDevices(result.StandardOutput);
            var changes = inventory.Replace(
                [.. parsed.Select(entry => entry.Disk)],
                parsed.ToDictionary(entry => entry.Disk.Id, entry => entry.Device, StringComparer.Ordinal));
            await ProbeAsync(changes.Upserts, cancellationToken);
            if (changes.Upserts.Count > 0 || changes.Removals.Count > 0)
            {
                LogDisksChanged(changes.Upserts.Count, changes.Removals.Count);
                hub.Publish(DiskInventory.Topic, changes);
                lock (_health)
                {
                    _attempted.Clear();
                }
                // The pools' health follows at once; ZFS may not notice a pulled disk before its next write.
                PublishPools(read: false);
                _poolChanges.Writer.TryWrite(true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRefreshFailed(exception);
        }
        finally
        {
            Activity.Current = parent;
        }
    }

    /// <summary>
    /// Reads the disks now and probes the given ones even if lsblk shows no change, for a plan that is about to be shown
    /// or to run. Members of an imported pool are still not probed.
    /// </summary>
    public async Task RefreshDisksAsync(IReadOnlyCollection<string> probeDiskIds, CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await ProbeAsync([.. inventory.Current.Disks.Where(disk => probeDiskIds.Contains(disk.Id))], cancellationToken);
    }

    /// <summary>Probes disks that appeared or changed, except members of an imported pool (they may be asleep).</summary>
    private async Task ProbeAsync(IReadOnlyList<Disk> disks, CancellationToken cancellationToken)
    {
        var poolDisks = facts.PoolDiskIds();
        foreach (var disk in disks)
        {
            if (poolDisks.Contains(disk.Id))
            {
                inventory.SetSignatures(disk.Id, null);
                continue;
            }
            try
            {
                inventory.SetSignatures(disk.Id, await probe.ProbeAsync(disk.Path, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                inventory.SetSignatures(disk.Id, null);
                LogProbeFailed(disk.Path, exception);
            }
        }
    }

    /// <summary>
    /// Reads pools and datasets now, for example right after a plan imported a pool or changed a dataset. Waits for a
    /// read that is running already. Brings back disks that returned (<see cref="PoolHealth.Reconnects"/>).
    /// </summary>
    public async Task RefreshPoolsAsync(CancellationToken cancellationToken)
    {
        var parent = Activity.Current;
        Activity.Current = null;
        using var activity = Tracing.Source.StartActivity("storage.pools.refresh");
        try
        {
            await _poolRead.WaitAsync(cancellationToken);
            try
            {
                if (await ReadPoolsAsync(cancellationToken) && await ReconnectAsync(cancellationToken))
                {
                    await ReadPoolsAsync(cancellationToken);
                }
                // Also after a failed read: a suspended pool or a pulled disk shows even while zpool hangs.
                PublishPools(read: true);
            }
            finally
            {
                _poolRead.Release();
            }
        }
        finally
        {
            Activity.Current = parent;
        }
    }

    /// <summary>
    /// Applies the connected disks and the kernel's pool states to the last read again and refuses a pool that a command on
    /// all its disks would hang on (<see cref="PoolHealth.Unreachable"/>), right before such a command runs.
    /// </summary>
    public void RequireReachable(string poolName)
    {
        PublishPools(read: false);
        if (pools.Current.Pools.FirstOrDefault(pool => pool.Name == poolName) is { } pool && PoolHealth.Unreachable(pool) is { } code)
        {
            throw new PithyxException(code, new Dictionary<string, object?> { ["name"] = poolName });
        }
    }

    /// <summary>Publishes the pools again after the key vault changed, without reading them (<see cref="Pool.Encryption"/>).</summary>
    public void PublishVault() => PublishPools(read: false);

    private async Task<bool> ReadPoolsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var read = await storage.ReadAsync(cancellationToken);
            lock (_health)
            {
                _lastRead = read;
            }
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPoolRefreshFailed(exception);
            return false;
        }
    }

    /// <summary>Runs the reconnects for disks that came back, each once per disk change; true when one ran.</summary>
    private async Task<bool> ReconnectAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CommandRequest> reconnects;
        lock (_health)
        {
            if (_lastRead is not { } read || !inventory.Loaded)
            {
                return false;
            }
            reconnects = [.. PoolHealth.Reconnects(read, facts.DevicePresence(), facts.SuspendedPools(read.Pools.Select(pool => pool.Name)), _reconnecting)
                .Where(command => _attempted.Add(string.Join(' ', command.Arguments)))];
        }
        foreach (var command in reconnects)
        {
            CommandResult result;
            try
            {
                result = await commands.RunAsync(command, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Stuck in the kernel; the pool reads must go on, and the pool shows what ZFS reports.
                LogReconnectFailed(string.Join(' ', command.Arguments), -1, "timed out");
                continue;
            }
            var pool = command.Arguments[1];
            if (result.ExitCode != 0)
            {
                LogReconnectFailed(string.Join(' ', command.Arguments), result.ExitCode, result.StandardError);
                continue;
            }
            LogReconnected(string.Join(' ', command.Arguments), pool);
            if (command.Arguments[0] == "online")
            {
                lock (_health)
                {
                    _reconnecting.Remove(command.Arguments[2]);
                }
            }
        }
        return reconnects.Count > 0;
    }

    private void PublishPools(bool read)
    {
        PoolList derived;
        lock (_health)
        {
            if (_lastRead is not { } last)
            {
                return;
            }
            var connected = inventory.Loaded ? facts.DevicePresence() : _ => true;
            var guids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var leaf in last.Pools.SelectMany(pool => pool.Vdevs.SelectMany(vdev => vdev.Leaves())))
            {
                guids.Add(leaf.VdevGuid);
                if (leaf.Type != "disk" || leaf.Path is not { } path)
                {
                    continue;
                }
                if (!connected(path))
                {
                    _reconnecting.Add(leaf.VdevGuid);
                }
                else if (facts.SerialOf(path) is { } serial)
                {
                    _serials[leaf.VdevGuid] = serial;
                }
            }
            // A leaf replaced or removed from its pool is forgotten.
            _reconnecting.IntersectWith(guids);
            foreach (var gone in _serials.Keys.Where(guid => !guids.Contains(guid)).ToList())
            {
                _serials.Remove(gone);
            }
            derived = Zfs.EncryptionCommands.Apply(
                PoolHealth.Apply(last, connected, facts.SuspendedPools(last.Pools.Select(pool => pool.Name)), _reconnecting, _serials),
                guid => vault?.Has(guid) == true,
                guid => vault?.Failed(guid) == true);
        }
        if (pools.Replace(derived, read))
        {
            LogPoolsChanged(pools.Current.Pools.Count);
            hub.Publish(PoolInventory.Topic, pools.Current);
        }
    }

    // UDEV  [5093.123456] remove   /devices/pci0000:00/.../block/sdg (block)
    [GeneratedRegex(@"^UDEV\s+\[")]
    private static partial Regex UdevEvent();

    [LoggerMessage(Level = LogLevel.Information, Message = "Disks changed: {Upserts} added or updated, {Removals} removed")]
    private partial void LogDisksChanged(int upserts, int removals);

    [LoggerMessage(Level = LogLevel.Warning, Message = "lsblk failed with {ExitCode}: {StandardError}")]
    private partial void LogLsblkFailed(int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reading the disks failed")]
    private partial void LogRefreshFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Monitor} failed")]
    private partial void LogMonitorFailed(string monitor, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Monitor} stopped; restarting in {Seconds} s")]
    private partial void LogMonitorRestart(string monitor, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pools changed: {Count} pools")]
    private partial void LogPoolsChanged(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the pools failed")]
    private partial void LogPoolRefreshFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A disk of pool {Pool} came back: ran zpool {Arguments}")]
    private partial void LogReconnected(string arguments, string pool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bringing back a disk failed: zpool {Arguments} exited with {ExitCode}: {StandardError}")]
    private partial void LogReconnectFailed(string arguments, int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Probing {Path} for signatures failed")]
    private partial void LogProbeFailed(string path, Exception exception);
}
