using Pithyx.Daemon.Apps.Proxy;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>
/// Keeps the proxy's view of the backends true (A14, D324). At the daemon's start, once the pools are read, it starts
/// the backend of every enabled app whose pool is reachable (A15 replaces this with the lifecycle of the manifest);
/// while Docker runs it follows the Engine's events of app containers, never polling, and takes an app out of the
/// proxy when one of its containers dies or runs out of memory, with a warning in the app's log source.
/// </summary>
public sealed partial class AppBackendSupervisor(
    DockerOptions options,
    DockerHost host,
    DockerEngine engine,
    AppBackendRuntime runtime,
    AppRegistry registry,
    AppBackends backends,
    PoolInventory pools,
    ILogger<AppBackendSupervisor> logger) : BackgroundService
{
    private readonly SemaphoreSlim _started = new(0, 1);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        host.Started += Signal;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        host.Started -= Signal;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _started.Dispose();
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
            await WhenPoolsReadAsync(stoppingToken);
            await StartInstalledAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await engine.PingAsync(stoppingToken))
                {
                    // Docker is off until a start of DockerHost says otherwise.
                    await _started.WaitAsync(stoppingToken);
                    continue;
                }
                await FollowAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the containers keep running until their backend is stopped.
        }
    }

    private async Task FollowAsync(CancellationToken stoppingToken)
    {
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
        catch (Exception exception) when (exception is HttpRequestException or IOException or DockerEngineException)
        {
            // Docker stopped or restarted; the loop asks it again.
            LogEventsEnded(exception);
        }
    }

    private async Task StartInstalledAsync(CancellationToken stoppingToken)
    {
        if (registry.Pool is not { } pool)
        {
            return;
        }
        foreach (var app in registry.Apps.Values.Where(app => app.Enabled).Select(BackendApp.Of).Where(app => app.HasBackend))
        {
            if (!registry.IsAvailable(app.AppId) || !registry.TryBegin(app.AppId))
            {
                continue;
            }
            try
            {
                await runtime.StartAsync(pool, app, null, stoppingToken);
            }
            catch (PithyxException exception)
            {
                LogStartFailed(app.AppId, exception.Code);
            }
            finally
            {
                registry.End(app.AppId);
            }
        }
    }

    private async Task WhenPoolsReadAsync(CancellationToken stoppingToken)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (pools.Available)
            {
                read.TrySetResult();
            }
        }
        pools.Changed += OnChanged;
        try
        {
            OnChanged();
            await read.Task.WaitAsync(stoppingToken);
        }
        finally
        {
            pools.Changed -= OnChanged;
        }
    }

    private void Signal()
    {
        try
        {
            _started.Release();
        }
        catch (SemaphoreFullException)
        {
            // A start is pending already.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A container of app {AppId} ended ({Service}, {Action}, exit code {ExitCode}); its backend is unavailable")]
    private partial void LogContainerEnded(string appId, string? service, string action, int? exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Following Docker's events ended")]
    private partial void LogEventsEnded(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backend of app {AppId} did not start with the daemon: {Code}")]
    private partial void LogStartFailed(string appId, string code);
}
