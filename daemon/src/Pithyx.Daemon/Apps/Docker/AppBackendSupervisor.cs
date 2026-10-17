using System.Collections.Concurrent;
using System.Diagnostics;
using Pithyx.Daemon.Apps.Proxy;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>
/// Runs every app backend's lifecycle (A15, D327) and keeps the proxy's view of the backends true (A14, D324). It decides
/// with <see cref="BackendLifecycle"/> whenever something it depends on changes: a window lease (<see cref="AppLeases"/>),
/// the registry, the pools, a backend's state, a request that wakes a backend (<see cref="WakeAsync"/>), a schedule, or
/// the next deadline it set itself; nothing polls. Backends of apps that are busy with a job are left to the job.
/// <list type="bullet">
/// <item>After a daemon restart it takes over containers that still run instead of starting them again.</item>
/// <item>A schedule of the manifest wakes the backend at its time and calls <c>POST path</c> on its endpoint with
/// <c>X-Pithyx-Schedule</c>, then lets it idle as after a closed window.</item>
/// <item>When the app pool goes (locked, exported, missing) every backend leaves the proxy and Docker stops, its data root
/// gone; when it comes back, what is wanted starts again.</item>
/// <item>While Docker runs it follows the Engine's events of app containers: <c>die</c> and <c>oom</c> take the app out of
/// the proxy (and count as a failure, so a crashing backend is not restarted in a loop), <c>health_status</c> turns the
/// status to <c>unavailable</c> or back to <c>running</c>.</item>
/// </list>
/// </summary>
public sealed partial class AppBackendSupervisor(
    DockerOptions options,
    DockerHost host,
    DockerEngine engine,
    AppBackendRuntime runtime,
    AppRegistry registry,
    AppBackends backends,
    AppLeases leases,
    PoolInventory pools,
    TimeProvider time,
    ILogger<AppBackendSupervisor> logger) : BackgroundService
{
    /// <summary>How long the proxy holds a request while the backend starts.</summary>
    public static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a schedule's call may run; the backend stays up for it.</summary>
    public static readonly TimeSpan ScheduleTimeout = TimeSpan.FromMinutes(10);

    public const string ScheduleHeader = "X-Pithyx-Schedule";

    private static readonly TimeSpan _minDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _maxDelay = TimeSpan.FromMinutes(1);
    private static readonly HttpClient _scheduleClient = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = ScheduleTimeout,
    };

    private readonly ConcurrentDictionary<string, AppState> _apps = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _evaluate = new(0, 1);
    private readonly SemaphoreSlim _dockerStarted = new(0, 1);
    private readonly Lock _timerLock = new();
    private ITimer? _timer;
    private bool? _poolReachable;

    /// <summary>What the supervisor remembers about one app between decisions.</summary>
    private sealed class AppState
    {
        public DateTimeOffset? IdleSince;
        public DateTimeOffset? FailedAt;
        public bool WasInUse;
        public bool WasRunning;
        /// <summary>Who holds the backend up (a waiting request, a running schedule) and until when.</summary>
        public readonly Dictionary<Guid, DateTimeOffset> Holds = [];
        public readonly List<TaskCompletionSource<bool>> Waiters = [];
        public string? ScheduleVersion;
        public readonly Dictionary<string, DateTimeOffset> NextRuns = new(StringComparer.Ordinal);
    }

    private static readonly ConcurrentDictionary<string, CronSchedule> _crons = new(StringComparer.Ordinal);

    /// <summary>
    /// Wakes an app's backend for a request: it starts when it may run, and the call returns once it runs (true), when
    /// it cannot start, or after <see cref="WakeTimeout"/> (false). During the pause after a failed start, and in
    /// simulation, it returns false at once.
    /// </summary>
    public async Task<bool> WakeAsync(string appId, CancellationToken cancellationToken)
    {
        if (backends.IsRunning(appId))
        {
            return true;
        }
        if (!options.Apply || registry.Find(appId) is not { } app || !BackendApp.Of(app).HasBackend || !IsRunnable(app))
        {
            return false;
        }
        var state = StateOf(appId);
        var hold = Guid.NewGuid();
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (state)
        {
            if (state.FailedAt is { } failed && failed + BackendLifecycle.RetryAfter > time.GetUtcNow())
            {
                return false;
            }
            state.Holds[hold] = time.GetUtcNow() + WakeTimeout;
            state.Waiters.Add(waiter);
        }
        Signal();
        try
        {
            return await waiter.Task.WaitAsync(WakeTimeout, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            return backends.IsRunning(appId);
        }
        finally
        {
            lock (state)
            {
                state.Holds.Remove(hold);
                state.Waiters.Remove(waiter);
            }
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        host.Started += OnDockerStarted;
        pools.Changed += Signal;
        registry.Changed += Signal;
        leases.Changed += Signal;
        backends.Changed += OnBackendChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        host.Started -= OnDockerStarted;
        pools.Changed -= Signal;
        registry.Changed -= Signal;
        leases.Changed -= Signal;
        backends.Changed -= OnBackendChanged;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
        }
        _evaluate.Dispose();
        _dockerStarted.Dispose();
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
            await AdoptAsync(stoppingToken);
            await Task.WhenAll(LifecycleAsync(stoppingToken), FollowEventsAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the containers keep running and are taken over at the next start.
        }
    }

    private async Task LifecycleAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync(stoppingToken);
            }
#pragma warning disable CA1031 // The lifecycle must go on whatever one round met.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogRoundFailed(exception);
            }
            await _evaluate.WaitAsync(stoppingToken);
        }
    }

    /// <summary>One round: the app pool first, then every backend app's decision; the earliest deadline sets the timer.</summary>
    private async Task EvaluateAsync(CancellationToken stoppingToken)
    {
        var withBackend = registry.Apps.Values.Where(app => BackendApp.Of(app).HasBackend).ToList();
        if (registry.Pool is null || withBackend.Count == 0)
        {
            return;
        }
        var reachable = withBackend.Any(app => registry.IsAvailable(app.Id));
        if (reachable != _poolReachable)
        {
            _poolReachable = reachable;
            if (!reachable)
            {
                await StopAllAsync(stoppingToken);
            }
        }
        var now = time.GetUtcNow();
        DateTimeOffset? recheck = null;
        foreach (var app in withBackend)
        {
            if (registry.IsBusy(app.Id))
            {
                continue;
            }
            var next = Decide(app, now, stoppingToken);
            recheck = Min(recheck, next);
        }
        SetTimer(recheck, now);
    }

    private DateTimeOffset? Decide(InstalledApp installed, DateTimeOffset now, CancellationToken stoppingToken)
    {
        var app = BackendApp.Of(installed);
        var state = StateOf(app.AppId);
        var inUse = leases.InUse(app.AppId);
        var running = backends.IsRunning(app.AppId);
        if (!running && backends.StatusOf(app.AppId) is AppBackends.Starting or AppBackends.Updating)
        {
            // Left over by a job that ended without a start: the backend is simply stopped.
            backends.SetStopped(app.AppId);
        }
        var dueSchedules = DueSchedules(installed, state, now, out var nextSchedule);
        bool held;
        DateTimeOffset? holdEnds;
        lock (state)
        {
            if (inUse && !state.WasInUse)
            {
                // A window opened: whatever failed before is worth another try now.
                state.FailedAt = null;
            }
            foreach (var expired in state.Holds.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
            {
                state.IdleSince = Max(state.IdleSince, state.Holds[expired]);
                state.Holds.Remove(expired);
            }
            held = state.Holds.Count > 0;
            holdEnds = held ? state.Holds.Values.Min() : null;
            // The idle countdown starts anew whenever the backend was used, held, or (re)started.
            if (inUse || held || (state.WasInUse && !inUse) || (running && !state.WasRunning))
            {
                state.IdleSince = now;
            }
            if (!running)
            {
                state.IdleSince = null;
            }
            state.WasInUse = inUse;
            state.WasRunning = running;
        }
        foreach (var schedule in dueSchedules)
        {
            _ = RunScheduleAsync(installed, schedule, stoppingToken);
        }
        var input = new LifecycleInput(
            Always: installed.Manifest.GetProperty("backend").GetProperty("lifecycle").GetString() == "always",
            IdleTimeout: IdleTimeoutOf(installed),
            Runnable: IsRunnable(installed),
            Running: running,
            InUse: inUse,
            Held: held,
            IdleSince: state.IdleSince,
            FailedAt: state.FailedAt,
            Now: now);
        var decision = BackendLifecycle.Decide(input);
        // A start or stop runs beside the decisions, holding the app, so a slow start does not delay the other apps.
        if (decision.Action != LifecycleAction.None && registry.TryBegin(app.AppId))
        {
            _ = decision.Action == LifecycleAction.Start ? StartAsync(app, state, stoppingToken) : StopAsync(app.AppId, stoppingToken);
        }
        else if (decision.Action == LifecycleAction.None && (!input.Runnable || (input.FailedAt is not null && !running)))
        {
            Complete(state, false);
        }
        return Min(Min(decision.RecheckAt, holdEnds), nextSchedule);
    }

    /// <summary>Whether the app may run now: enabled, its pool reachable, its required folders mapped.</summary>
    private bool IsRunnable(InstalledApp app) =>
        app.Enabled && registry.IsAvailable(app.Id) && AppBackendRuntime.UnmappedRequired(BackendApp.Of(app)).Count == 0;

    /// <summary>Starts a backend the caller holds with <see cref="AppRegistry.TryBegin"/>.</summary>
    private async Task StartAsync(BackendApp app, AppState state, CancellationToken stoppingToken)
    {
        try
        {
            if (registry.Pool is not { } pool)
            {
                Complete(state, false);
                return;
            }
            await runtime.StartAsync(pool, app, null, stoppingToken);
            lock (state)
            {
                state.FailedAt = null;
            }
            LogStarted(app.AppId);
            Complete(state, true);
        }
#pragma warning disable CA1031 // A backend that does not start must not keep the others or the daemon down.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogStartFailed(exception, app.AppId);
            await StopQuietlyAsync(app.AppId, stoppingToken);
            backends.SetStatus(app.AppId, AppBackends.Unavailable);
            lock (state)
            {
                state.FailedAt = time.GetUtcNow();
            }
            Complete(state, false);
        }
        finally
        {
            registry.End(app.AppId);
        }
    }

    /// <summary>Stops a backend the caller holds with <see cref="AppRegistry.TryBegin"/>.</summary>
    private async Task StopAsync(string appId, CancellationToken stoppingToken)
    {
        try
        {
            LogIdleStop(appId);
            await StopQuietlyAsync(appId, stoppingToken);
        }
        finally
        {
            registry.End(appId);
        }
    }

    private async Task StopQuietlyAsync(string appId, CancellationToken stoppingToken)
    {
        try
        {
            await runtime.StopAsync(appId, registry.Pool, stoppingToken);
        }
#pragma warning disable CA1031 // The lifecycle goes on; the next round tries again.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogStopFailed(exception, appId);
        }
    }

    /// <summary>The schedules due now; the next due time of the others goes to <paramref name="next"/>.</summary>
    private static List<(string Id, string Endpoint, string Path)> DueSchedules(InstalledApp app, AppState state, DateTimeOffset now, out DateTimeOffset? next)
    {
        next = null;
        var due = new List<(string, string, string)>();
        if (!app.Enabled || !app.Manifest.GetProperty("backend").TryGetProperty("schedules", out var schedules))
        {
            return due;
        }
        lock (state)
        {
            // An update may change or drop schedules: their times are computed again from the new manifest.
            if (state.ScheduleVersion != app.Version)
            {
                state.NextRuns.Clear();
                state.ScheduleVersion = app.Version;
            }
            foreach (var schedule in schedules.EnumerateArray())
            {
                var id = schedule.GetProperty("id").GetString()!;
                var cron = _crons.GetOrAdd(schedule.GetProperty("cron").GetString()!, CronSchedule.Parse);
                if (!state.NextRuns.TryGetValue(id, out var at))
                {
                    at = cron.Next(now, TimeZoneInfo.Local) ?? DateTimeOffset.MaxValue;
                    state.NextRuns[id] = at;
                }
                if (at <= now)
                {
                    due.Add((id, schedule.GetProperty("endpoint").GetString()!, schedule.GetProperty("path").GetString()!));
                    at = cron.Next(now, TimeZoneInfo.Local) ?? DateTimeOffset.MaxValue;
                    state.NextRuns[id] = at;
                }
                next = Min(next, at == DateTimeOffset.MaxValue ? null : at);
            }
        }
        return due;
    }

    /// <summary>Wakes the backend for a schedule and calls it; the backend then idles as after a closed window.</summary>
    /// <summary>
    /// The call of a schedule: <c>POST</c> to its path with <c>X-Pithyx-Schedule</c> and the <c>traceparent</c> of the
    /// current activity (a new one when there is none), like every request the proxy forwards.
    /// </summary>
    internal static HttpRequestMessage ScheduleRequest(Uri address, string path, string scheduleId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, path.TrimStart('/')));
        request.Headers.TryAddWithoutValidation(ScheduleHeader, scheduleId);
        request.Headers.TryAddWithoutValidation("traceparent", Activity.Current is { IdFormat: ActivityIdFormat.W3C } current
            ? current.Id
            : $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01");
        return request;
    }

    private async Task RunScheduleAsync(InstalledApp app, (string Id, string Endpoint, string Path) schedule, CancellationToken stoppingToken)
    {
        // A schedule has no request of its own; its run gets the request id it carries to the backend.
        using var activity = Tracing.Source.StartActivity("apps.schedule");
        activity?.SetTag("app", app.Id);
        activity?.SetTag("schedule", schedule.Id);
        var state = StateOf(app.Id);
        var hold = Guid.NewGuid();
        lock (state)
        {
            state.Holds[hold] = time.GetUtcNow() + ScheduleTimeout;
        }
        try
        {
            if (!await WakeAsync(app.Id, stoppingToken) || backends.Find(app.Id, schedule.Endpoint) is not { } address)
            {
                LogScheduleSkipped(app.Id, schedule.Id);
                return;
            }
            using var request = ScheduleRequest(address, schedule.Path, schedule.Id);
            using var response = await _scheduleClient.SendAsync(request, stoppingToken);
            LogScheduleRan(app.Id, schedule.Id, (int)response.StatusCode);
        }
#pragma warning disable CA1031 // A schedule that fails is logged; the next one runs at its time.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogScheduleFailed(exception, app.Id, schedule.Id);
        }
        finally
        {
            lock (state)
            {
                state.Holds.Remove(hold);
                state.IdleSince = time.GetUtcNow();
            }
            Signal();
        }
    }

    /// <summary>After a daemon restart: containers that still run with all of the app's services are the app's backend.</summary>
    private async Task AdoptAsync(CancellationToken stoppingToken)
    {
        if (!await engine.PingAsync(stoppingToken))
        {
            return;
        }
        foreach (var app in registry.Apps.Values.Where(app => app.Enabled).Select(BackendApp.Of).Where(app => app.HasBackend))
        {
            try
            {
                if (await runtime.AdoptAsync(app, stoppingToken))
                {
                    LogAdopted(app.AppId);
                }
            }
#pragma warning disable CA1031 // A backend that cannot be taken over is started by the lifecycle when it is wanted.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogStartFailed(exception, app.AppId);
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
                    OnEngineEvent(item);
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

    private void OnEngineEvent(DockerEvent item)
    {
        if (item.Action is "die" or "oom" && backends.IsRunning(item.App) && !registry.IsBusy(item.App))
        {
            backends.SetStopped(item.App);
            var state = StateOf(item.App);
            lock (state)
            {
                state.FailedAt = time.GetUtcNow();
            }
            LogContainerEnded(item.App, item.Service, item.Action, item.ExitCode);
        }
        else if (item.Action == "health_status" && backends.IsRunning(item.App))
        {
            backends.SetStatus(item.App, item.Detail == "healthy" ? AppBackends.Running : AppBackends.Unavailable);
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

    private static TimeSpan IdleTimeoutOf(InstalledApp app) =>
        app.Manifest.GetProperty("backend").TryGetProperty("idleTimeout", out var seconds)
            ? TimeSpan.FromSeconds(seconds.GetInt32())
            : BackendLifecycle.DefaultIdleTimeout;

    private AppState StateOf(string appId) => _apps.GetOrAdd(appId, _ => new AppState());

    private static void Complete(AppState state, bool running)
    {
        List<TaskCompletionSource<bool>> waiters;
        lock (state)
        {
            waiters = [.. state.Waiters];
            state.Waiters.Clear();
        }
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(running);
        }
    }

    private void SetTimer(DateTimeOffset? at, DateTimeOffset now)
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = at is { } due ? time.CreateTimer(_ => Signal(), null, due > now ? due - now : TimeSpan.Zero, Timeout.InfiniteTimeSpan) : null;
        }
    }

    private void OnBackendChanged(string appId) => Signal();

    private void OnDockerStarted() => Release(_dockerStarted);

    private void Signal() => Release(_evaluate);

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
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value > b ? value : b;

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the backend of app {AppId} for its lifecycle")]
    private partial void LogStarted(string appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping the backend of app {AppId}: idle, disabled or its pool is gone")]
    private partial void LogIdleStop(string appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Took over the running backend of app {AppId}")]
    private partial void LogAdopted(string appId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A container of app {AppId} ended ({Service}, {Action}, exit code {ExitCode}); its backend is unavailable")]
    private partial void LogContainerEnded(string appId, string? service, string action, int? exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Following Docker's events ended")]
    private partial void LogEventsEnded(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backend of app {AppId} did not start")]
    private partial void LogStartFailed(Exception exception, string appId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping the backend of app {AppId} failed")]
    private partial void LogStopFailed(Exception exception, string appId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting or stopping the app backends failed")]
    private partial void LogRoundFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The app pool is not reachable any more; stopping Docker")]
    private partial void LogPoolGone();

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} of app {AppId} answered {Status}")]
    private partial void LogScheduleRan(string appId, string scheduleId, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {ScheduleId} of app {AppId} did not run: the backend is not reachable")]
    private partial void LogScheduleSkipped(string appId, string scheduleId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {ScheduleId} of app {AppId} failed")]
    private partial void LogScheduleFailed(Exception exception, string appId, string scheduleId);
}
