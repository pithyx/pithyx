using System.Collections.Concurrent;
using System.Globalization;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Apps.Proxy;

/// <summary>
/// The WebSockets the proxy holds open to app backends, and their end when what opened them no longer holds: at once on
/// a sign-out, a password reset, a deactivation or a deletion of the user (<see cref="TopicHub.CloseSession"/>,
/// <see cref="TopicHub.CloseUser"/>), and after every change of rights or apps (a revoked
/// grant, a disabled or removed app) once <see cref="AppTokenCheck"/> says so, and when the backend stops or starts
/// again at another address; the session's expiry is checked every minute.
/// </summary>
public sealed partial class AppProxySockets(
    AppTokenCheck check,
    TopicHub hub,
    PermissionService permissions,
    AppRegistry registry,
    AppBackends backends,
    TimeProvider time,
    ILogger<AppProxySockets> logger) : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<long, Socket> _open = new();
    private readonly SemaphoreSlim _recheck = new(0, 1);
    private long _nextId;

    /// <summary>An open socket; disposing it forgets it.</summary>
    public sealed class Socket(AppProxySockets owner, long id, AppTokenGrant grant, HttpContext context) : IDisposable
    {
        private readonly CancellationTokenSource _closing = new();

        public AppTokenGrant Grant { get; } = grant;

        /// <summary>The backend address the socket goes to, once the proxy forwards it.</summary>
        public Uri? Destination { get; set; }

        /// <summary>Cancelled when the socket must end; the forwarder stops copying.</summary>
        public CancellationToken Closing => _closing.Token;

        // Runs inside sign-out and the other endpoints that end sessions: whatever a cancellation callback throws must
        // not fail them.
        internal void Close()
        {
            try
            {
                _closing.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (AggregateException)
            {
                // A callback failed; the socket is aborted below all the same.
            }
            context.Abort();
        }

        public void Dispose()
        {
            owner._open.TryRemove(id, out _);
            _closing.Dispose();
        }
    }

    /// <summary>Open sockets, for tests and diagnostics.</summary>
    public int Count => _open.Count;

    /// <summary>Tracks the socket the proxy is about to open for a checked ticket.</summary>
    public Socket Open(AppTokenGrant grant, HttpContext context)
    {
        var id = Interlocked.Increment(ref _nextId);
        var socket = new Socket(this, id, grant, context);
        _open[id] = socket;
        return socket;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        hub.SessionClosed += CloseSession;
        hub.UserClosed += CloseUser;
        permissions.Changed += Recheck;
        registry.Changed += Recheck;
        backends.Changed += CloseStopped;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        hub.SessionClosed -= CloseSession;
        hub.UserClosed -= CloseUser;
        permissions.Changed -= Recheck;
        registry.Changed -= Recheck;
        backends.Changed -= CloseStopped;
        CloseWhere(_ => true);
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var signalled = _recheck.WaitAsync(wait.Token);
                await Task.WhenAny(signalled, Task.Delay(_checkInterval, time, wait.Token));
                await wait.CancelAsync();
                await CheckAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed check must not end the loop that closes revoked sockets.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                CheckFailed(logger, exception);
            }
        }
    }

    // One check per session, user and app, however many sockets they hold.
    private async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        foreach (var holders in _open.Values.GroupBy(socket => (socket.Grant.SessionId, socket.Grant.UserId, socket.Grant.AppId)))
        {
            if (await check.CheckAsync(holders.First().Grant, withGroups: false, cancellationToken) is null)
            {
                foreach (var socket in holders)
                {
                    socket.Close();
                }
            }
        }
    }

    private void Recheck()
    {
        try
        {
            _recheck.Release();
        }
        catch (SemaphoreFullException)
        {
            // A check is pending already and will see this change too.
        }
    }

    private void CloseSession(string sessionId) => CloseWhere(grant => grant.SessionId == sessionId);

    private void CloseUser(string userId) => CloseWhere(grant => grant.UserId.ToString(CultureInfo.InvariantCulture) == userId);

    // A stopped backend, or one that started again at another address, ends the sockets to the old one.
    private void CloseStopped(string appId)
    {
        // A socket without a destination is still waiting for its backend to start (the proxy woke it); it stays.
        foreach (var socket in _open.Values.Where(socket => socket.Grant.AppId == appId && socket.Destination is not null))
        {
            var current = socket.Grant.Endpoint is { } endpoint ? backends.Find(appId, endpoint) : null;
            if (current is null || socket.Destination != current)
            {
                socket.Close();
            }
        }
    }

    private void CloseWhere(Func<AppTokenGrant, bool> predicate)
    {
        foreach (var socket in _open.Values.Where(socket => predicate(socket.Grant)))
        {
            socket.Close();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking the open app sockets failed")]
    private static partial void CheckFailed(ILogger logger, Exception exception);
}
