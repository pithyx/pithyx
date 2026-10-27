using System.Collections.Concurrent;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Notifications;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>
/// Follows the devices the installed apps were granted (D387): <c>udevadm monitor</c> for graphics, serial and misc
/// devices runs while some app holds a device right, so nothing polls. Every event raises <see cref="Changed"/>, which
/// makes the supervisor decide again (a backend whose required device went stops; one whose device came back starts)
/// and the app contexts show <c>unavailable</c> with the reason <c>device</c>. When a required device goes, the app's log
/// <c>app/&lt;id&gt;</c> gets an error and administrators a notification; an optional one only gets a warning in the log.
/// </summary>
public sealed partial class AppDevices(
    DockerOptions options,
    ICommandRunner commands,
    AppRegistry registry,
    DeviceFs fs,
    LogStore logs,
    TimeProvider time,
    ILogger<AppDevices> logger) : BackgroundService
{
    /// <summary>The udev subsystems of the device classes: graphics (<c>drm</c>), serial adapters (<c>tty</c>) and <c>misc</c> (tun).</summary>
    public static readonly CommandRequest Monitor = new("udevadm", ["monitor", "--udev", "--subsystem-match=drm", "--subsystem-match=tty", "--subsystem-match=misc"]);

    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _missing = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wanted = new(0, 1);

    /// <summary>Raised after a device of an app came or went.</summary>
    public event Action? Changed;

    /// <summary>Raised when a required device of an app went (the app and the device path), for the administrators' notification.</summary>
    public event Func<InstalledApp, string, CancellationToken, Task>? RequiredMissing;

    /// <summary>The granted required devices of an app that are not there now; its backend cannot start without them.</summary>
    public IReadOnlyList<string> MissingRequired(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return DeviceNodes.MissingRequired(app.GrantedElevated, Manifest.ElevatedRights.OptionalDevices(app.Manifest), fs);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Changed += OnRegistryChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        registry.Changed -= OnRegistryChanged;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _wanted.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Apply)
        {
            return;
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);
                if (!AnyDevice())
                {
                    await _wanted.WaitAsync(stoppingToken);
                    continue;
                }
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                void Stop()
                {
                    if (!AnyDevice())
                    {
                        watch.Cancel();
                    }
                }
                registry.Changed += Stop;
                try
                {
                    await foreach (var line in commands.StreamLinesAsync(Monitor, watch.Token))
                    {
                        if (line.StartsWith("UDEV", StringComparison.Ordinal))
                        {
                            await CheckAsync(stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // No app holds a device any more.
                }
                finally
                {
                    registry.Changed -= Stop;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Compares every app's missing devices with the last look; a change is logged, notified and raised.</summary>
    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var app in registry.Apps.Values)
        {
            var optional = Manifest.ElevatedRights.OptionalDevices(app.Manifest);
            var missing = DeviceNodes.Paths(app.GrantedElevated).Where(path => DeviceNodes.Present(path, fs).Count == 0).ToHashSet(StringComparer.Ordinal);
            var before = _missing.GetValueOrDefault(app.Id) ?? new HashSet<string>(StringComparer.Ordinal);
            if (missing.SetEquals(before))
            {
                continue;
            }
            _missing[app.Id] = missing;
            changed = true;
            foreach (var path in missing.Except(before).Order(StringComparer.Ordinal))
            {
                var required = !optional.Contains(path);
                AppLog(app.Id, required ? LogLevelName.Error : LogLevelName.Warn, required
                    ? $"The device {path} the app needs is not there; its background service does not run until it is back"
                    : $"The optional device {path} is not there; the background service runs without it");
                if (required && RequiredMissing is { } notify)
                {
                    await notify(app, path, cancellationToken);
                }
            }
            foreach (var path in before.Except(missing).Order(StringComparer.Ordinal))
            {
                AppLog(app.Id, LogLevelName.Info, $"The device {path} is back");
            }
        }
        foreach (var gone in _missing.Keys.Where(id => !registry.Apps.ContainsKey(id)))
        {
            _missing.TryRemove(gone, out _);
        }
        if (changed)
        {
            LogDevicesChanged();
            Changed?.Invoke();
        }
    }

    private bool AnyDevice() => registry.Apps.Values.Any(app => DeviceNodes.Paths(app.GrantedElevated).Any());

    private void AppLog(string appId, LogLevelName level, string message) =>
        logs.Enqueue(new PendingLogEntry(time.GetUtcNow(), level, "app/" + appId, message));

    private void OnRegistryChanged()
    {
        try
        {
            _wanted.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is pending already.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A device of an installed app came or went")]
    private partial void LogDevicesChanged();
}

/// <summary>Tells the administrators when a required device of an app went (D387); kept apart so the notifications need no app context.</summary>
public sealed class AppDeviceAlerts(AppDevices devices, NotificationService notifications) : IHostedService
{
    public const string MissingCode = "apps.device.missing";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        devices.RequiredMissing += NotifyAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        devices.RequiredMissing -= NotifyAsync;
        return Task.CompletedTask;
    }

    private async Task NotifyAsync(InstalledApp app, string path, CancellationToken cancellationToken) =>
        await notifications.CreateForAdministratorsAsync(
            new NotificationDraft(MissingCode, new Dictionary<string, object?> { ["app"] = NameOf(app), ["path"] = path }, NotificationTone.Warning),
            cancellationToken);

    private static string NameOf(InstalledApp app) =>
        app.Manifest.TryGetProperty("name", out var name) && name.TryGetProperty("en", out var english) ? english.GetString() ?? app.Id : app.Id;
}
