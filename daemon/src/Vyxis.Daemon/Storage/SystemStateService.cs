using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Vyxis.Daemon.Commands;
using Vyxis.Daemon.Hosting;
using Vyxis.Daemon.Realtime;

namespace Vyxis.Daemon.Storage;

/// <summary>
/// Keeps <see cref="DiskInventory"/> current. Reads the disks once at startup and again after udev reports a block
/// device event; there is no polling. If <c>udevadm monitor</c> stops, it is restarted with backoff and the disks are
/// read again so no change is missed.
/// </summary>
public sealed partial class SystemStateService(
    ICommandRunner commands,
    DiskInventory inventory,
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

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // The first read completes before the daemon accepts requests, so GET /api/disks is never empty by accident.
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var monitor = MonitorAsync(stoppingToken);
        try
        {
            await foreach (var change in _changes.Reader.ReadAllAsync(stoppingToken))
            {
                await Task.Delay(Debounce, time, stoppingToken);
                _changes.Reader.TryRead(out var _);
                await RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        await monitor;
    }

    private async Task MonitorAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var line in commands.StreamLinesAsync(UdevMonitor, stoppingToken))
                {
                    if (UdevEvent().IsMatch(line))
                    {
                        _changes.Writer.TryWrite(true);
                        backoff = TimeSpan.FromSeconds(1);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogMonitorFailed(exception);
            }
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            LogMonitorRestart(backoff.TotalSeconds);
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
            _changes.Writer.TryWrite(true);
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
            var changes = inventory.Replace(LsblkParser.Parse(result.StandardOutput));
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

    // UDEV  [5093.123456] remove   /devices/pci0000:00/.../block/sdg (block)
    [GeneratedRegex(@"^UDEV\s+\[")]
    private static partial Regex UdevEvent();

    [LoggerMessage(Level = LogLevel.Information, Message = "Disks changed: {Upserts} added or updated, {Removals} removed")]
    private partial void LogDisksChanged(int upserts, int removals);

    [LoggerMessage(Level = LogLevel.Warning, Message = "lsblk failed with {ExitCode}: {StandardError}")]
    private partial void LogLsblkFailed(int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reading the disks failed")]
    private partial void LogRefreshFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "udevadm monitor failed")]
    private partial void LogMonitorFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "udevadm monitor stopped; restarting in {Seconds} s")]
    private partial void LogMonitorRestart(double seconds);
}
