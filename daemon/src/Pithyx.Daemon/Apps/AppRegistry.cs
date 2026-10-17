using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using PackageLayout = Pithyx.Daemon.Apps.Manifest.PackageLayout;

namespace Pithyx.Daemon.Apps;

/// <summary>
/// The version an app ran before its last update, kept with the update's snapshot until the next update so the app can
/// go back to it with its data (D329); stored as JSON in <c>apps.previous</c>.
/// </summary>
/// <param name="Version">The version.</param>
/// <param name="Manifest">Its manifest as the registry held it.</param>
/// <param name="Icon">Its icon in base64.</param>
/// <param name="Sha256">The SHA-256 of its package.</param>
/// <param name="Source">sideload or catalog.</param>
/// <param name="CatalogId">The catalog it came from, if any.</param>
/// <param name="Snapshot">The snapshot of the app's dataset taken before the update; null in simulation.</param>
/// <param name="UpdatedAt">When the update replaced it.</param>
public sealed record AppPrevious(string Version, JsonElement Manifest, string Icon, string Sha256, string Source, string? CatalogId, string? Snapshot, DateTimeOffset UpdatedAt);

/// <summary>An installed third-party app, as the registry keeps it in memory.</summary>
public sealed record InstalledApp(
    string Id,
    string Version,
    string Source,
    bool Enabled,
    JsonElement Manifest,
    IReadOnlyList<AppPermissionState> Permissions,
    long Uid,
    string Sha256,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    AppLogLevel LogLevel,
    IReadOnlyList<AppStorageMapping> Storage,
    string? CatalogId = null,
    Catalog.UpdatePolicy? UpdatePolicy = null,
    AppPrevious? Previous = null)
{
    /// <summary>The path of the entry page below <c>/apps/&lt;id&gt;/</c> (the manifest's entry without <c>frontend/</c>).</summary>
    public string EntryPath => Manifest.GetProperty("entry").GetString()![(PackageLayout.FrontendFolder.Length + 1)..];

    /// <summary>Whether the app may reach the internet (<c>connect-src https:</c> for its frontend).</summary>
    public bool HasInternet => Permissions.Any(permission => permission.Granted && permission.Permission == "network.internet");

    /// <summary>Whether the app's backend may reach the local network (D358).</summary>
    public bool HasLan => Permissions.Any(permission => permission.Granted && permission.Permission == "network.lan");

    public IReadOnlyList<string> Granted => [.. Permissions.Where(permission => permission.Granted).Select(permission => permission.Permission)];

    /// <summary>Whether the app holds a permission of its manifest that no administrator revoked.</summary>
    public bool Holds(string permission) => Permissions.Any(state => state.Granted && state.Permission == permission);

    /// <summary>The endpoint names of the manifest's backend; empty for an app without one.</summary>
    public IReadOnlyList<string> Endpoints =>
        Manifest.TryGetProperty("backend", out var backend) && backend.TryGetProperty("endpoints", out var endpoints)
            ? [.. endpoints.EnumerateObject().Select(endpoint => endpoint.Name).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Whether the manifest's backend declares the endpoint.</summary>
    public bool HasEndpoint(string name) =>
        Manifest.TryGetProperty("backend", out var backend) && backend.TryGetProperty("endpoints", out var endpoints) && endpoints.TryGetProperty(name, out _);

    /// <summary>The storage requests of the manifest: id and the access asked for.</summary>
    public IReadOnlyList<(string Id, string Access)> StorageRequests =>
        Manifest.TryGetProperty("storage", out var storage)
            ? [.. storage.EnumerateArray().Select(request => (request.GetProperty("id").GetString()!, request.GetProperty("access").GetString()!))]
            : [];
}

/// <summary>
/// The installed third-party apps (A10), read from <c>pithyx.db</c> and kept in memory, plus the setting
/// <c>apps.pool</c> and which apps have an install, update or removal running. A dependency-free state holder: the
/// topics <c>apps</c> (the apps each user may use) and <c>apps/admin</c> read it, <see cref="AppChangePublisher"/>
/// publishes.
/// </summary>
public sealed class AppRegistry(SqliteConnectionFactory connections, PermissionService permissions, PoolInventory pools, AppStorageOptions options)
{
    public const string Topic = "apps";
    public const string AdminTopic = "apps/admin";
    public const string PoolSetting = "apps.pool";

    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, bool> _busy = new(StringComparer.Ordinal);
    private (IReadOnlyDictionary<string, InstalledApp> Apps, string? Pool)? _state;

    /// <summary>Raised after the registry changed or an app became busy or idle.</summary>
    public event Action? Changed;

    public IReadOnlyDictionary<string, InstalledApp> Apps => State().Apps;

    /// <summary>The app pool, or null before the first install.</summary>
    public string? Pool => State().Pool;

    public InstalledApp? Find(string appId) => Apps.GetValueOrDefault(appId);

    public bool IsBusy(string appId) => _busy.ContainsKey(appId);

    /// <summary>Marks an app busy for a job; false when it is busy already.</summary>
    public bool TryBegin(string appId)
    {
        var begun = _busy.TryAdd(appId, true);
        if (begun)
        {
            Changed?.Invoke();
        }
        return begun;
    }

    public void End(string appId)
    {
        _busy.TryRemove(appId, out _);
        Changed?.Invoke();
    }

    /// <summary>Whether the app's data is reachable now.</summary>
    public bool IsAvailable(string appId) =>
        Pool is { } pool && AppStorage.RootOf(pools.Current, options, pool, appId) is not null;

    /// <summary>The folder of an app's installed frontend, or null while the app is unknown, disabled or unreachable.</summary>
    public string? FrontendOf(string appId)
    {
        if (Find(appId) is not { Enabled: true } app || Pool is not { } pool || AppStorage.RootOf(pools.Current, options, pool, appId) is not { } root)
        {
            return null;
        }
        return Path.Combine(root, AppStorage.VersionsFolder, app.Version, PackageLayout.FrontendFolder);
    }

    /// <summary>Reads the registry again after a change and tells the listeners.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var loaded = await LoadAsync(connection);
        lock (_lock)
        {
            _state = loaded;
        }
        Changed?.Invoke();
    }

    /// <summary>Whether the rights allow the app: a grant on <c>app:&lt;id&gt;</c>, or the administrator right.</summary>
    public bool Allows(EffectivePermissions rights, string appId)
    {
        ArgumentNullException.ThrowIfNull(rights);
        return Find(appId) is { Enabled: true } && rights.Has(Resources.App(appId));
    }

    public AppList List() => new([.. Apps.Values.Where(app => app.Enabled).Select(Summary)]);

    public AdminAppList AdminList() => new(Pool, [.. Apps.Values.Select(Details)]);

    public AppDetails Details(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new(app.Id, app.Version, app.Source, app.Enabled, app.Manifest, app.Permissions, app.Uid, app.Sha256, app.InstalledAt, app.UpdatedAt, IsBusy(app.Id), IsAvailable(app.Id), app.LogLevel, app.Storage, app.CatalogId, app.UpdatePolicy, app.Previous?.Version);
    }

    public static AppSummary Summary(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var manifest = app.Manifest;
        return new AppSummary(
            app.Id,
            app.Version,
            Texts(manifest.GetProperty("name")),
            manifest.TryGetProperty("description", out var description) ? Texts(description) : null,
            $"/apps/{app.Id}/{app.EntryPath}",
            $"/api/apps/{app.Id}/icon?v={app.Sha256[..12]}",
            manifest.GetProperty("window").Clone(),
            app.Granted,
            app.LogLevel,
            FileHandlers(manifest));
    }

    private static List<AppFileHandler> FileHandlers(JsonElement manifest) =>
        manifest.TryGetProperty("fileHandlers", out var handlers)
            ? [.. handlers.EnumerateArray().Select(handler => new AppFileHandler(
                [.. handler.GetProperty("extensions").EnumerateArray().Select(extension => extension.GetString()!)],
                handler.TryGetProperty("isDefault", out var isDefault) && isDefault.GetBoolean()))]
            : [];

    public TopicDefinition Definition() => new(
        Topic,
        typeof(AppList),
        typeof(AppList),
        (_, _) => ValueTask.FromResult<object>(List()),
        TopicDefinition.AnyUser,
        ForUser: (user, data) =>
        {
            if (data is not AppList list)
            {
                return data;
            }
            var rights = permissions.ForUser(user);
            return new AppList([.. list.Apps.Where(app => rights.Has(Resources.App(app.Id)))]);
        });

    public TopicDefinition AdminDefinition() => new(
        AdminTopic,
        typeof(AdminAppList),
        typeof(AdminAppList),
        (_, _) => ValueTask.FromResult<object>(AdminList()),
        (user, _) => user.IsInRole(SessionAuthenticationHandler.AdminRole));

    private (IReadOnlyDictionary<string, InstalledApp> Apps, string? Pool) State()
    {
        lock (_lock)
        {
            if (_state is { } state)
            {
                return state;
            }
        }
        using var connection = connections.Open();
        var loaded = LoadAsync(connection).GetAwaiter().GetResult();
        lock (_lock)
        {
            _state ??= loaded;
            return _state.Value;
        }
    }

    private static async Task<(IReadOnlyDictionary<string, InstalledApp>, string?)> LoadAsync(System.Data.Common.DbConnection connection)
    {
        var rows = await connection.QueryAsync<AppRow>(
            """
            SELECT id AS Id, version AS Version, source AS Source, enabled AS Enabled, manifest AS Manifest, uid AS Uid, package_sha256 AS Sha256,
                installed_at AS InstalledAt, updated_at AS UpdatedAt, log_level AS LogLevel, catalog_id AS CatalogId, update_policy AS UpdatePolicy,
                previous AS Previous
            FROM apps ORDER BY id
            """);
        var granted = (await connection.QueryAsync<(string AppId, string Permission, long Granted)>("SELECT app_id, permission, granted FROM app_permissions ORDER BY permission"))
            .ToLookup(row => row.AppId, StringComparer.Ordinal);
        var mappings = (await connection.QueryAsync<(string AppId, string StorageId, long ShareId, string Path, string Access)>(
                "SELECT app_id, storage_id, share_id, path, access FROM storage_mappings ORDER BY storage_id"))
            .ToLookup(row => row.AppId, StringComparer.Ordinal);
        var pool = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = PoolSetting });
        var apps = new SortedDictionary<string, InstalledApp>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            using var manifest = JsonDocument.Parse(row.Manifest);
            apps[row.Id] = new InstalledApp(
                row.Id,
                row.Version,
                row.Source,
                row.Enabled == 1,
                manifest.RootElement.Clone(),
                [.. granted[row.Id].Select(permission => new AppPermissionState(permission.Permission, permission.Granted == 1))],
                row.Uid,
                row.Sha256,
                DateTimeOffset.Parse(row.InstalledAt, CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture),
                row.LogLevel == "debug" ? AppLogLevel.Debug : AppLogLevel.Info,
                [.. mappings[row.Id].Select(mapping => new AppStorageMapping(
                    mapping.StorageId, mapping.ShareId, mapping.Path, mapping.Access == "read" ? AppStorageAccess.Read : AppStorageAccess.ReadWrite))],
                row.CatalogId,
                row.UpdatePolicy is { } policy ? Enum.Parse<Catalog.UpdatePolicy>(policy, ignoreCase: true) : null,
                row.Previous is { } previous ? JsonSerializer.Deserialize<AppPrevious>(previous, PreviousJson) : null);
        }
        return (apps, pool);
    }

    /// <summary>How <c>apps.previous</c> is written and read.</summary>
    public static readonly JsonSerializerOptions PreviousJson = new(JsonSerializerDefaults.Web);

    // Properties instead of a constructor: Dapper cannot map a constructor when a nullable column is NULL in every row.
    private sealed class AppRow
    {
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
        public string Source { get; init; } = "";
        public long Enabled { get; init; }
        public string Manifest { get; init; } = "";
        public long Uid { get; init; }
        public string Sha256 { get; init; } = "";
        public string InstalledAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
        public string LogLevel { get; init; } = "";
        public string? CatalogId { get; init; }
        public string? UpdatePolicy { get; init; }
        public string? Previous { get; init; }
    }

    private static Dictionary<string, string> Texts(JsonElement texts) =>
        texts.EnumerateObject().ToDictionary(text => text.Name, text => text.Value.GetString()!, StringComparer.Ordinal);
}

/// <summary>
/// Publishes <c>apps</c>, <c>apps/admin</c> and every app's <c>apps/&lt;id&gt;/context</c> when the registry, the rights
/// or the pools change.
/// </summary>
public sealed class AppChangePublisher(TopicHub hub, AppRegistry registry, AppContexts contexts, PermissionService permissions, PoolInventory pools, Proxy.AppBackends backends) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Changed += Publish;
        permissions.Changed += Publish;
        pools.Changed += Publish;
        backends.Changed += PublishContext;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.Changed -= Publish;
        permissions.Changed -= Publish;
        pools.Changed -= Publish;
        backends.Changed -= PublishContext;
        return Task.CompletedTask;
    }

    // A backend that starts or stops changes only its app's context.
    private void PublishContext(string appId) => hub.Publish(AppContexts.TopicOf(appId), contexts.SourceOf(appId));

    private void Publish()
    {
        hub.Publish(AppRegistry.Topic, registry.List());
        hub.Publish(AppRegistry.AdminTopic, registry.AdminList());
        foreach (var appId in registry.Apps.Keys)
        {
            hub.Publish(AppContexts.TopicOf(appId), contexts.SourceOf(appId));
        }
    }
}
