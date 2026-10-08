using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// The per-user topic <c>apps/&lt;id&gt;/context</c> (A11, D319): what the daemon decides about an app for each
/// subscriber, its permissions, log level, backend and storage. A dependency-free state holder like the registry; the
/// publisher sends it after every change of the registry, the rights or the pools. Only a user who may use the app may
/// subscribe, and only to the concrete topic (a wildcard never takes it), so nobody learns which apps are installed.
/// When a user loses an app, its events stop and the app leaves the user's topic <c>apps</c>, which closes its windows.
/// </summary>
public sealed class AppContexts(AppRegistry registry, PermissionService permissions, PoolInventory pools, SqliteConnectionFactory connections, Proxy.AppBackends backends)
{
    public const string Pattern = "apps/+/context";

    /// <summary>The reason a shared folder of a mapping cannot be reached when its dataset is encrypted and locked.</summary>
    public const string Locked = "locked";

    /// <summary>The shared part of a context; each subscriber gets it shaped by its own rights.</summary>
    /// <param name="AppId">The app of the topic.</param>
    /// <param name="App">The installed app, or null when it is not installed.</param>
    /// <param name="ShareUnavailable">Per shared folder of the app's mappings: why it cannot be reached (<see cref="Locked"/> among them), or null.</param>
    public sealed record Source(string AppId, InstalledApp? App, IReadOnlyDictionary<long, string?> ShareUnavailable);

    public static string TopicOf(string appId) => $"apps/{appId}/context";

    /// <summary>The app id of a context topic, or null for any other topic.</summary>
    public static string? AppOf(string topic)
    {
        var parts = topic.Split('/');
        return parts is ["apps", var appId, "context"] && AppId.IsValid(appId) ? appId : null;
    }

    public TopicDefinition Definition() => new(
        Pattern,
        typeof(AppContextState),
        typeof(AppContextState),
        (topic, _) => ValueTask.FromResult<object>(SourceOf(AppOf(topic) ?? "")),
        MayUse,
        CurrentTopics: () => registry.Apps.Keys.Select(TopicOf),
        ForUser: (user, data) => data is Source source ? For(user, source) : data,
        ExactOnly: true);

    /// <summary>Whether the user may follow the topic: it names an app the user may use now.</summary>
    public bool MayUse(ClaimsPrincipal user, string topic) =>
        TopicDefinition.AnyUser(user, topic) && AppOf(topic) is { } appId && registry.Allows(permissions.ForUser(user), appId);

    /// <summary>The shared part of an app's context now.</summary>
    public Source SourceOf(string appId)
    {
        var app = registry.Find(appId);
        var shares = app?.Storage is { Count: > 0 } mappings ? Datasets([.. mappings.Select(mapping => mapping.ShareId)]) : [];
        var unavailable = shares.ToDictionary(
            share => share.Key,
            share => share.Value.Found ? ReasonOf(share.Value.Dataset, pools.Current, pools.Available) : ShareAvailability.PoolMissing);
        return new Source(appId, app, unavailable);
    }

    /// <summary>Why a shared folder's dataset cannot be reached: <see cref="Locked"/> for a locked encrypted dataset, otherwise as for shares.</summary>
    public static string? ReasonOf(string? dataset, PoolList pools, bool poolsRead)
    {
        ArgumentNullException.ThrowIfNull(pools);
        if (dataset is not null && poolsRead && pools.Datasets.FirstOrDefault(candidate => candidate.Name == dataset) is { KeyStatus: "unavailable" })
        {
            return Locked;
        }
        return ShareAvailability.Reason(dataset, pools, poolsRead);
    }

    /// <summary>The context as one user sees it, or null for a user who may not use the app (nothing is sent).</summary>
    public AppContextState? For(ClaimsPrincipal user, Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rights = permissions.ForUser(user);
        if (source.App is not { } app || !registry.Allows(rights, app.Id))
        {
            return null;
        }
        var backend = app.Manifest.TryGetProperty("backend", out _) ? new AppBackendInfo(backends.IsRunning(app.Id) ? "running" : "stopped", app.Endpoints) : null;
        return new AppContextState(app.Version, app.Granted, app.LogLevel, backend, StorageStates(app, rights, source.ShareUnavailable));
    }

    /// <summary>Each storage request of the manifest with its state and access for a user with these rights.</summary>
    public static IReadOnlyList<AppStorageInfo> StorageStates(InstalledApp app, EffectivePermissions rights, IReadOnlyDictionary<long, string?> shareUnavailable)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(rights);
        ArgumentNullException.ThrowIfNull(shareUnavailable);
        return [.. app.StorageRequests.Select(request => Storage(app, request, rights, shareUnavailable))];
    }

    private static AppStorageInfo Storage(InstalledApp app, (string Id, string Access) request, EffectivePermissions rights, IReadOnlyDictionary<long, string?> shareUnavailable)
    {
        var mapping = app.Storage.FirstOrDefault(candidate => candidate.StorageId == request.Id);
        if (mapping is null)
        {
            return new AppStorageInfo(request.Id, AppStorageState.Unmapped, null);
        }
        var reason = shareUnavailable.GetValueOrDefault(mapping.ShareId, ShareAvailability.PoolMissing);
        if (reason == Locked)
        {
            return new AppStorageInfo(request.Id, AppStorageState.Locked, null);
        }
        var userAccess = rights.Share(mapping.ShareId);
        if (reason is not null || userAccess == ShareAccess.None)
        {
            return new AppStorageInfo(request.Id, AppStorageState.Unavailable, null);
        }
        var access = request.Access == "readWrite" && mapping.Access == AppStorageAccess.ReadWrite && userAccess == ShareAccess.ReadWrite ? ShareAccess.ReadWrite : ShareAccess.Read;
        return new AppStorageInfo(request.Id, AppStorageState.Ready, access);
    }

    private Dictionary<long, (bool Found, string? Dataset)> Datasets(IReadOnlyList<long> shareIds)
    {
        using var connection = connections.Open();
        var rows = connection.Query<(long Id, string? Dataset)>("SELECT id, dataset FROM shares WHERE id IN @shareIds", new { shareIds })
            .ToDictionary(row => row.Id, row => row.Dataset);
        return shareIds.Distinct().ToDictionary(id => id, id => (rows.ContainsKey(id), rows.GetValueOrDefault(id)));
    }
}

/// <summary>
/// The window lease (A11 for A15, D319): an app is in use by a user while a desktop of that user that may use the app
/// is subscribed to the app's context topic, and for 2 minutes after such a desktop lost its connection (a reload, a
/// short network loss) until the user is back. An unsubscribe (the last window of the app closed) ends the lease at
/// once, and so does the end of the session (sign-out, revoke, deactivation). The desktop subscribes once per app and
/// tab, so the count per user is the number of the user's desktops with a window of the app.
/// </summary>
public sealed class AppLeases(TopicHub hub, AppRegistry registry, PermissionService permissions, TimeProvider time) : IHostedService, IDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    private readonly Lock _lock = new();
    private readonly Dictionary<(string AppId, Guid Connection), (long UserId, string SessionId, DateTimeOffset Until)> _grace = [];
    private ITimer? _timer;
    private DateTimeOffset? _due;

    /// <summary>Raised when a lease may have begun or ended.</summary>
    public event Action? Changed;

    /// <summary>Per user holding a lease on the app: the number of its desktops with a window of the app.</summary>
    public IReadOnlyDictionary<long, int> Holders(string appId)
    {
        var holders = new Dictionary<long, int>();
        foreach (var connection in hub.Subscribers(AppContexts.TopicOf(appId)))
        {
            Add(holders, CurrentUser.Id(connection.User));
        }
        var now = time.GetUtcNow();
        var live = holders.Keys.ToHashSet();
        List<long> waiting;
        lock (_lock)
        {
            waiting = [.. _grace.Where(entry => entry.Key.AppId == appId && entry.Value.Until > now && !live.Contains(entry.Value.UserId)).Select(entry => entry.Value.UserId)];
        }
        foreach (var userId in waiting.Where(userId => registry.Allows(permissions.ForUser(userId), appId)))
        {
            Add(holders, userId);
        }
        return holders;
    }

    /// <summary>Whether any user holds a lease on the app.</summary>
    public bool InUse(string appId) => Holders(appId).Count > 0;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        hub.Disconnected += OnDisconnected;
        hub.SubscribersAdded += OnSubscribed;
        hub.SubscribersRemoved += Raise;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        hub.Disconnected -= OnDisconnected;
        hub.SubscribersAdded -= OnSubscribed;
        hub.SubscribersRemoved -= Raise;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
        }
    }

    private static void Add(Dictionary<long, int> holders, long userId) => holders[userId] = holders.GetValueOrDefault(userId) + 1;

    private void OnDisconnected(TopicHub.Connection connection)
    {
        var userId = CurrentUser.Id(connection.User);
        lock (_lock)
        {
            if (connection.CloseCode == WebSocketCloseCode.SessionEnded)
            {
                // Signed out, revoked or deactivated: the session's leases end now.
                foreach (var key in _grace.Where(entry => entry.Value.SessionId == connection.SessionId).Select(entry => entry.Key).ToList())
                {
                    _grace.Remove(key);
                }
            }
            else
            {
                var until = time.GetUtcNow() + Grace;
                foreach (var appId in connection.Patterns().Select(AppContexts.AppOf).OfType<string>())
                {
                    _grace[(appId, connection.Id)] = (userId, connection.SessionId, until);
                }
            }
            Schedule();
        }
        Raise();
    }

    private void OnSubscribed()
    {
        lock (_lock)
        {
            // A user who is back (a reload) holds the lease through the new connection; its grace ends.
            foreach (var key in _grace.Keys.ToList())
            {
                var (appId, _) = key;
                var userId = _grace[key].UserId;
                if (hub.Subscribers(AppContexts.TopicOf(appId)).Any(connection => CurrentUser.Id(connection.User) == userId))
                {
                    _grace.Remove(key);
                }
            }
            Schedule();
        }
        Raise();
    }

    /// <summary>Points the one timer at the earliest deadline. Callers hold the lock.</summary>
    private void Schedule()
    {
        DateTimeOffset? next = _grace.Count == 0 ? null : _grace.Values.Min(entry => entry.Until);
        if (next == _due)
        {
            return;
        }
        _timer?.Dispose();
        _timer = null;
        _due = next;
        if (next is { } due)
        {
            var wait = due - time.GetUtcNow();
            _timer = time.CreateTimer(_ => Expire(), null, wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void Expire()
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            foreach (var key in _grace.Where(entry => entry.Value.Until <= now).Select(entry => entry.Key).ToList())
            {
                _grace.Remove(key);
            }
            _due = null;
            Schedule();
        }
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
