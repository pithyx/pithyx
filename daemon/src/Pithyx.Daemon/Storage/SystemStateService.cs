using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Storage;

/// <summary>
/// Keeps <see cref="DiskInventory"/> and <see cref="PoolInventory"/> current. Reads disks and pools once at startup,
/// the disks again after udev reports a block device event and the pools after the storage backend reports one
/// (<c>zpool events</c>); there is no polling. A monitor that stops is restarted with backoff, and its state is read
/// again so no change is missed. Disks that appear or change are probed for signatures, except pool members.
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
    ILogger<SystemStateService> logger) : BackgroundService
{
    public static readonly CommandRequest Lsblk = new("lsblk", ["-J", "-b", "-O"]);
    public static readonly CommandRequest UdevMonitor = new("udevadm", ["monitor", "--udev", "--subsystem-match=block"]);

    /// <summary>Events arrive in bursts (a disk and its partitions); one read covers the whole burst.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(1);
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _poolChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // The first reads complete before the daemon accepts requests, so GET /api/disks is never empty by accident.
        // Pools come first: their members are never probed.
        await RefreshPoolsAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var monitors = Task.WhenAll(
            MonitorAsync("udevadm monitor", token => UdevLinesAsync(token), _changes, stoppingToken),
            MonitorAsync("zpool events", storage.WatchAsync, _poolChanges, stoppingToken),
            ConsumeAsync(_poolChanges, RefreshPoolsAsync, stoppingToken));
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

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
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

    private async Task RefreshPoolsAsync(CancellationToken cancellationToken)
    {
        var parent = Activity.Current;
        Activity.Current = null;
        using var activity = Tracing.Source.StartActivity("storage.pools.refresh");
        try
        {
            if (pools.Replace(await storage.ReadAsync(cancellationToken)))
            {
                LogPoolsChanged(pools.Current.Pools.Count);
                hub.Publish(PoolInventory.Topic, pools.Current);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPoolRefreshFailed(exception);
        }
        finally
        {
            Activity.Current = parent;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Probing {Path} for signatures failed")]
    private partial void LogProbeFailed(string path, Exception exception);
}
