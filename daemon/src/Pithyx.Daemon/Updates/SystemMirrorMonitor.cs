using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Updates;

/// <summary>
/// Watches the system mirror (D295): the arrays <c>pithyx-system</c> and <c>pithyx-swap</c> in <c>/proc/mdstat</c>, read
/// at start and after every block device event udev reports, never polled. A mirror that lost a disk (missing or failed)
/// notifies the administrators once; a single-disk system (one member of one) never does. The box keeps running on the
/// remaining disk.
/// </summary>
public sealed partial class SystemMirrorMonitor(SystemStateService state, NotificationService notifications, ILogger<SystemMirrorMonitor> logger) : BackgroundService
{
    public const string DegradedCode = "system.mirror.degraded";

    private static readonly IReadOnlyList<string> _arrays = ["pithyx-system", "pithyx-swap"];

    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checking = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnEvent() => _ = CheckAsync(stoppingToken);
        state.BlockDevicesChanged += OnEvent;
        try
        {
            await CheckAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            state.BlockDevicesChanged -= OnEvent;
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!await _checking.WaitAsync(0, cancellationToken))
        {
            return;
        }
        try
        {
            var arrays = SystemParsers.ParseMdstat(await File.ReadAllTextAsync("/proc/mdstat", cancellationToken));
            foreach (var name in _arrays)
            {
                var link = new FileInfo($"/dev/md/{name}");
                var device = link.Exists ? Path.GetFileName(link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? "") : null;
                if (arrays.FirstOrDefault(array => array.Device == device) is not { } array)
                {
                    continue;
                }
                if (!array.Degraded)
                {
                    _announced.Remove(name);
                    continue;
                }
                if (_announced.Add(name))
                {
                    LogDegraded(name, array.Working, array.Expected);
                    await notifications.CreateForAdministratorsAsync(new NotificationDraft(DegradedCode, new Dictionary<string, object?>
                    {
                        ["array"] = name == "pithyx-system" ? "system" : "swap",
                        ["working"] = array.Working,
                        ["expected"] = array.Expected,
                    }, NotificationTone.Critical), cancellationToken);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogReadFailed(exception);
        }
        finally
        {
            _checking.Release();
        }
    }

    public override void Dispose()
    {
        _checking.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The system mirror {Array} runs on {Working} of {Expected} disks")]
    private partial void LogDegraded(string array, int working, int expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read /proc/mdstat")]
    private partial void LogReadFailed(Exception exception);
}
