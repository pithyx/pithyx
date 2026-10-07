using System.Collections.Concurrent;
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
/// The per-user topic <c>apps/&lt;id&gt;/context</c> (A11): what the daemon decides about an app for each subscriber,
/// the access, permissions, log level, backend and storage. A dependency-free state holder like the registry; the
/// publisher sends it after every change of the registry, the rights or the pools. Anyone signed in may subscribe,
/// so a revoke reaches the desktop as <c>access: false</c> instead of silence; without access nothing else is sent.
/// </summary>
public sealed class AppContexts(AppRegistry registry, PermissionService permissions, PoolInventory pools, SqliteConnectionFactory connections)
{
    public const string Pattern = "apps/+/context";

    /// <summary>The shared part of a context; each subscriber gets it shaped by its own rights.</summary>
    /// <param name="AppId">The app of the topic.</param>
    /// <param name="App">The installed app, or null when it is not installed.</param>
    /// <param name="ShareUnavailable">Per shared folder of the app's mappings: why it cannot be reached, or null.</param>
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
        (user, topic) => TopicDefinition.AnyUser(user, topic) && (TopicPattern.HasWildcards(topic) || AppOf(topic) is not null),
        CurrentTopics: () => registry.Apps.Keys.Select(TopicOf),
        ForUser: (user, data) => data is Source source ? For(user, source) : data);

    /// <summary>The shared part of an app's context now.</summary>
    public Source SourceOf(string appId)
    {
        var app = registry.Find(appId);
        var shares = app?.Mappings is { Count: > 0 } mappings ? Datasets([.. mappings.Select(mapping => mapping.ShareId)]) : [];
        var unavailable = shares.ToDictionary(
            share => share.Key,
            share => share.Value.Found ? ShareAvailability.Reason(share.Value.Dataset, pools.Current, pools.Available) : ShareAvailability.PoolMissing);
        return new Source(appId, app, unavailable);
    }

    /// <summary>The context as one user sees it.</summary>
    public AppContextState For(ClaimsPrincipal user, Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rights = permissions.ForUser(user);
        if (source.App is not { } app || !registry.Allows(rights, app.Id))
        {
            return new AppContextState(false, null, [], AppLogLevel.Info, null, []);
        }
        var endpoints = app.Endpoints;
        var backend = app.Manifest.TryGetProperty("backend", out _) ? new AppBackendInfo("stopped", endpoints) : null;
        return new AppContextState(true, app.Version, app.Granted, app.LogLevel, backend, [.. app.StorageRequests.Select(request => Storage(app, request, rights, source))]);
    }

    private static AppStorageInfo Storage(InstalledApp app, (string Id, string Access) request, EffectivePermissions rights, Source source)
    {
        var mapping = app.Mappings?.FirstOrDefault(candidate => candidate.StorageId == request.Id);
        if (mapping is null)
        {
            return new AppStorageInfo(request.Id, AppStorageState.Unmapped, null);
        }
        var userAccess = rights.Share(mapping.ShareId);
        if (source.ShareUnavailable.GetValueOrDefault(mapping.ShareId, ShareAvailability.PoolMissing) is not null || userAccess == ShareAccess.None)
        {
            return new AppStorageInfo(request.Id, AppStorageState.Unavailable, null);
        }
        var access = request.Access == "readWrite" && mapping.Access == "readWrite" && userAccess == ShareAccess.ReadWrite ? ShareAccess.ReadWrite : ShareAccess.Read;
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
/// The window lease (A11 for A15): an app is in use by a user while a desktop of that user is subscribed to the app's
/// context topic, and for 2 minutes after such a desktop lost its connection (a reload, a short network loss). An
/// unsubscribe (the last window of the app closed) ends the lease at once. The desktop subscribes once per app and
/// tab, so the count per user is the number of the user's desktops with a window of the app.
/// </summary>
public sealed class AppLeases : IHostedService, IDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    private readonly TopicHub _hub;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(string AppId, Guid Connection), (long UserId, DateTimeOffset Until)> _grace = new();
    private ITimer? _timer;

    public AppLeases(TopicHub hub, TimeProvider time)
    {
        _hub = hub;
        _time = time;
    }

    /// <summary>Raised when a lease may have begun or ended.</summary>
    public event Action? Changed;

    /// <summary>Per user holding a lease on the app: the number of its desktops with a window of the app.</summary>
    public IReadOnlyDictionary<long, int> Holders(string appId)
    {
        var now = _time.GetUtcNow();
        var holders = new Dictionary<long, int>();
        foreach (var connection in _hub.Subscribers(AppContexts.TopicOf(appId)))
        {
            Add(holders, CurrentUser.Id(connection.User));
        }
        foreach (var ((app, _), (userId, until)) in _grace)
        {
            if (app == appId && until > now)
            {
                Add(holders, userId);
            }
        }
        return holders;
    }

    /// <summary>Whether any user holds a lease on the app.</summary>
    public bool InUse(string appId) => Holders(appId).Count > 0;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _hub.Disconnected += OnDisconnected;
        _hub.SubscribersAdded += Raise;
        _hub.SubscribersRemoved += Raise;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _hub.Disconnected -= OnDisconnected;
        _hub.SubscribersAdded -= Raise;
        _hub.SubscribersRemoved -= Raise;
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private static void Add(Dictionary<long, int> holders, long userId) => holders[userId] = holders.GetValueOrDefault(userId) + 1;

    private void OnDisconnected(TopicHub.Connection connection)
    {
        var until = _time.GetUtcNow() + Grace;
        foreach (var pattern in connection.Patterns())
        {
            if (AppContexts.AppOf(pattern) is { } appId)
            {
                _grace[(appId, connection.Id)] = (CurrentUser.Id(connection.User), until);
            }
        }
        lock (_grace)
        {
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Expire(), null, Grace, Timeout.InfiniteTimeSpan);
        }
    }

    private void Expire()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, value) in _grace)
        {
            if (value.Until <= now)
            {
                _grace.TryRemove(KeyValuePair.Create(key, value));
            }
        }
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
