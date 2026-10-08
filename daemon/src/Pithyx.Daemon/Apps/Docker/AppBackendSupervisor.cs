using Pithyx.Daemon.Apps.Proxy;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>
/// Keeps the backends in line with the app pool and the proxy's view of them true (A14, D324). Once the pools are read,
/// and again whenever the app pool becomes reachable (unlocked, imported), it starts the backend of every enabled app
/// that is not running and has its required folders (A15 replaces this with the lifecycle of the manifest); when the app
/// pool goes (locked, exported, missing) it takes every backend out of the proxy and stops Docker, whose data root is
/// gone. While Docker runs it follows the Engine's events of app containers, never polling: a container's <c>die</c> or
/// <c>oom</c> takes its app out of the proxy. When the stream ends, the apps the proxy still reaches are checked against
/// the containers that run, and the stream is followed again, later each time it ends quickly.
/// </summary>
public sealed partial class AppBackendSupervisor(
    DockerOptions options,
    DockerHost host,
    DockerEngine engine,
    AppBackendRuntime runtime,
    AppRegistry registry,
    AppBackends backends,
    PoolInventory pools,
    TimeProvider time,
    ILogger<AppBackendSupervisor> logger) : BackgroundService
{
    private static readonly TimeSpan _minDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _maxDelay = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _dockerStarted = new(0, 1);
    private readonly SemaphoreSlim _poolsChanged = new(0, 1);
    private bool? _poolReachable;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        host.Started += OnDockerStarted;
        pools.Changed += OnPoolsChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        host.Started -= OnDockerStarted;
        pools.Changed -= OnPoolsChanged;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _dockerStarted.Dispose();
        _poolsChanged.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Apply)
        {
            return;
        }
        // The first reading of the pools may have come before this service listened.
        OnPoolsChanged();
        try
        {
            await Task.WhenAll(WatchPoolAsync(stoppingToken), FollowEventsAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the containers keep running until their backend is stopped.
        }
    }

    /// <summary>Acts on the app pool's reachability when it changes, the first reading after the start included.</summary>
    private async Task WatchPoolAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _poolsChanged.WaitAsync(stoppingToken);
            if (!pools.Available || registry.Pool is not { } pool)
            {
                continue;
            }
            var withBackend = registry.Apps.Values.Where(app => BackendApp.Of(app).HasBackend).Select(app => app.Id).ToList();
            if (withBackend.Count == 0)
            {
                continue;
            }
            var reachable = withBackend.Any(registry.IsAvailable);
            if (reachable == _poolReachable)
            {
                continue;
            }
            _poolReachable = reachable;
            try
            {
                if (reachable)
                {
                    await StartAllAsync(pool, stoppingToken);
                }
                else
                {
                    await StopAllAsync(stoppingToken);
                }
            }
#pragma warning disable CA1031 // The watch must go on whatever one round met.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogRoundFailed(exception);
            }
        }
    }

    private async Task StartAllAsync(string pool, CancellationToken stoppingToken)
    {
        var apps = registry.Apps.Values.Where(app => app.Enabled).Select(BackendApp.Of)
            .Where(app => app.HasBackend && !backends.IsRunning(app.AppId) && AppBackendRuntime.UnmappedRequired(app).Count == 0)
            .ToList();
        foreach (var app in apps)
        {
            if (!registry.TryBegin(app.AppId))
            {
                continue;
            }
            try
            {
                await runtime.StartAsync(pool, app, null, stoppingToken);
            }
#pragma warning disable CA1031 // One app's backend that does not start must not keep the others or the daemon down.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogStartFailed(exception, app.AppId);
            }
            finally
            {
                registry.End(app.AppId);
            }
        }
    }

    /// <summary>The app pool is gone: no backend is reachable, and Docker stops before its data root disappears for good.</summary>
    private async Task StopAllAsync(CancellationToken stoppingToken)
    {
        foreach (var appId in registry.Apps.Keys.Where(backends.IsRunning))
        {
            backends.SetStopped(appId);
        }
        if (await engine.PingAsync(stoppingToken))
        {
            LogPoolGone();
            await host.StopAsync(stoppingToken);
        }
    }

    private async Task FollowEventsAsync(CancellationToken stoppingToken)
    {
        var delay = _minDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await engine.PingAsync(stoppingToken))
            {
                // Docker is off: with restart "no" no container survived, and it runs again only through DockerHost.
                foreach (var appId in registry.Apps.Keys.Where(backends.IsRunning))
                {
                    backends.SetStopped(appId);
                }
                await _dockerStarted.WaitAsync(stoppingToken);
                delay = _minDelay;
                continue;
            }
            var started = time.GetUtcNow();
            try
            {
                await foreach (var item in engine.EventsAsync(stoppingToken))
                {
                    if (item.Action is "die" or "oom" && backends.IsRunning(item.App))
                    {
                        backends.SetStopped(item.App);
                        LogContainerEnded(item.App, item.Service, item.Action, item.ExitCode);
                    }
                }
            }
#pragma warning disable CA1031 // Docker stopped, restarted or refused; the loop asks it again.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogEventsEnded(exception);
            }
            await CheckRunningAsync(stoppingToken);
            delay = time.GetUtcNow() - started > _maxDelay ? _minDelay : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxDelay.Ticks));
            await Task.Delay(delay, time, stoppingToken);
        }
    }

    /// <summary>After the event stream broke: an app the proxy still reaches keeps that only while all its containers run.</summary>
    private async Task CheckRunningAsync(CancellationToken stoppingToken)
    {
        foreach (var appId in registry.Apps.Keys.Where(backends.IsRunning))
        {
            try
            {
                var containers = await engine.ContainersAsync(appId, stoppingToken);
                if (containers.Count == 0 || containers.Any(container => container.State != "running"))
                {
                    backends.SetStopped(appId);
                    LogContainerEnded(appId, null, "missing", null);
                }
            }
#pragma warning disable CA1031 // Docker is gone; the next round marks everything stopped.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogEventsEnded(exception);
                return;
            }
        }
    }

    private void OnDockerStarted() => Release(_dockerStarted);

    private void OnPoolsChanged() => Release(_poolsChanged);

    private static void Release(SemaphoreSlim signal)
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A signal is pending already.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A container of app {AppId} ended ({Service}, {Action}, exit code {ExitCode}); its backend is unavailable")]
    private partial void LogContainerEnded(string appId, string? service, string action, int? exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Following Docker's events ended")]
    private partial void LogEventsEnded(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backend of app {AppId} did not start")]
    private partial void LogStartFailed(Exception exception, string appId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting or stopping the app backends after a change of the app pool failed")]
    private partial void LogRoundFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The app pool is not reachable any more; stopping Docker")]
    private partial void LogPoolGone();
}
