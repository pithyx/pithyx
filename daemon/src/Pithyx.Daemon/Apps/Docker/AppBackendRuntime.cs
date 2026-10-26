using System.Globalization;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Apps.Compose;
using Pithyx.Daemon.Apps.Proxy;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using PackageLayout = Pithyx.Daemon.Apps.Manifest.PackageLayout;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>An app version whose backend is to run: what the manifest and the registry say about it.</summary>
/// <param name="AppId">The app.</param>
/// <param name="Version">The installed version, whose files are in <c>versions/&lt;version&gt;</c>.</param>
/// <param name="Manifest">That version's manifest.</param>
/// <param name="Uid">The app's uid (and gid) from 40000 to 49999.</param>
/// <param name="Internet">Whether the app holds <c>network.internet</c>: its outside network reaches the internet.</param>
/// <param name="Lan">Whether the app holds <c>network.lan</c>: its outside network reaches the local network (D358).</param>
/// <param name="Storage">The folders the administrator mapped to its storage requests.</param>
/// <param name="Elevated">The elevated rights an administrator granted (D311); the Compose keys of the others are left out.</param>
public sealed record BackendApp(
    string AppId,
    string Version,
    JsonElement Manifest,
    long Uid,
    bool Internet,
    bool Lan,
    IReadOnlyList<AppStorageMapping> Storage,
    IReadOnlySet<string>? Elevated = null)
{
    public static BackendApp Of(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new(app.Id, app.Version, app.Manifest, app.Uid, app.HasInternet, app.HasLan, app.Storage, app.GrantedElevated);
    }

    public IReadOnlySet<string> GrantedElevated => Elevated ?? new HashSet<string>(StringComparer.Ordinal);

    public bool HasBackend => Manifest.TryGetProperty("backend", out _);

    /// <summary>Whether the services get a network out of the box at all: one with a way to the internet, the LAN or both.</summary>
    public bool Outside => Internet || Lan;
}

/// <summary>
/// Runs app backends on Docker (A14, D309, D310, D324). A start checks that the app pool and every required storage
/// request are reachable, makes Docker run (<see cref="DockerHost"/>), brings every image of the lock in by its digest
/// (loaded from the package's <c>backend/images/&lt;arch&gt;/</c> or pulled from the registry) and refuses one whose id
/// differs, writes the rewritten Compose file to <c>run/compose.json</c> of the app's dataset, creates the folders of its
/// volumes, and runs <c>docker compose up --wait</c>; then it gives the proxy the containers' addresses
/// (<see cref="AppBackends"/>). A stop takes the app out of the proxy and runs <c>docker compose down</c>. In simulation
/// nothing runs, and a backend never becomes reachable.
/// </summary>
public sealed partial class AppBackendRuntime(
    DockerHost host,
    DockerEngine engine,
    DockerOptions options,
    ICommandRunner commands,
    AppStorage storage,
    AppBackends backends,
    SqliteConnectionFactory connections,
    PoolInventory pools,
    ILogger<AppBackendRuntime> logger)
{
    /// <summary>The rewritten Compose file in the app's <c>run/</c> folder.</summary>
    public const string ComposeFile = "compose.json";

    /// <summary>How long Compose may take to start every service and see it healthy.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long the manifest's health check may take after Compose reported the services healthy.</summary>
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The pause between two tries of the manifest's health check while a backend starts. Without a Compose healthcheck the
    /// containers count as started before the server listens, so the first tries often fail; a short pause keeps the start
    /// close to the moment the server answers (N16: a small Node server about 0.4 seconds after its container).
    /// </summary>
    public static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly HttpClient _healthClient = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>How long a service gets to stop before it is killed.</summary>
    public const int StopSeconds = 30;

    /// <summary>
    /// The group each granted device node needs, by the path the manifest names (D311): the node's group unless everybody
    /// may read and write it, nothing for root's group. Throws <c>app.device.unavailable</c> for a node that is missing, is
    /// no character device or leads to a blocked one through a link, so the backend does not start without it.
    /// </summary>
    public static IReadOnlyDictionary<string, int> DeviceGroups(IReadOnlySet<string> granted)
    {
        ArgumentNullException.ThrowIfNull(granted);
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var right in granted.Where(right => right.StartsWith(Manifest.ManifestValidator.DevicePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var path = right[Manifest.ManifestValidator.DevicePrefix.Length..];
            var resolved = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
            if (!Manifest.ManifestValidator.IsSupportedElevatedRight(Manifest.ManifestValidator.DevicePrefix + resolved)
                || Files.LinuxFs.CharacterDevice(resolved) is not { } device)
            {
                throw new PithyxException(ErrorCodes.AppDeviceUnavailable, new Dictionary<string, object?> { ["path"] = path });
            }
            if (!device.Open && device.Gid != 0)
            {
                groups[path] = (int)device.Gid;
            }
        }
        return groups;
    }

    /// <summary>The Linux bridge of an app's internal network: <c>px-</c> and the app's uid without 40000 (D309, at most 15 characters).</summary>
    public static string BridgeName(long uid) => "px-" + (uid - Users.UidAllocator.MinAppUid).ToString(CultureInfo.InvariantCulture);

    /// <summary>The bridge of the outside network of an app with <c>network.internet</c> or <c>network.lan</c>, which is not internal.</summary>
    public static string InternetBridgeName(long uid) => "px-i" + (uid - Users.UidAllocator.MinAppUid).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Starts the backend of an installed version and makes it reachable through the proxy. Throws when something it
    /// needs is missing, when an image cannot be brought in or does not match the lock, or when Compose fails.
    /// </summary>
    public async Task StartAsync(string pool, BackendApp app, Action<Jobs.JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.HasBackend || !options.Apply)
        {
            return;
        }
        try
        {
            await StartCoreAsync(pool, app, progress, cancellationToken);
        }
        catch when (!backends.IsRunning(app.AppId))
        {
            // Never left as "starting": the caller stops what came up, and the app sees that its backend failed.
            backends.SetStatus(app.AppId, AppBackends.Unavailable);
            throw;
        }
    }

    private async Task StartCoreAsync(string pool, BackendApp app, Action<Jobs.JobProgress>? progress, CancellationToken cancellationToken)
    {
        var root = storage.RootOf(pool, app.AppId) ?? throw new PithyxException(
            ErrorCodes.AppPoolUnavailable, new Dictionary<string, object?> { ["pool"] = pool, ["reason"] = "locked" });
        var mappings = await MappingsAsync(app, cancellationToken);
        backends.SetStatus(app.AppId, AppBackends.Starting);
        progress?.Invoke(new Jobs.JobProgress(0, 3, 0, 0, "docker"));
        await host.EnsureRunningAsync(pool, cancellationToken);
        await host.AllowNetworkAsync(InternetBridgeName(app.Uid), app.Internet, app.Lan, cancellationToken);

        var version = Path.Combine(root, AppStorage.VersionsFolder, app.Version);
        var composePath = app.Manifest.GetProperty("backend").GetProperty("compose").GetString()!;
        var validation = ComposeValidator.Validate(await File.ReadAllBytesAsync(Path.Combine(version, composePath), cancellationToken), app.Manifest);
        if (!validation.IsValid)
        {
            throw validation.Issues[0].ToException();
        }
        var lockFile = ImagesLock.Parse(await File.ReadAllBytesAsync(Path.Combine(version, ImagesLock.FileName), cancellationToken))
            ?? throw ImagesLock.Invalid("invalid");
        progress?.Invoke(new Jobs.JobProgress(1, 3, 0, 0, "images"));
        var images = await ImagesAsync(app.AppId, version, lockFile, cancellationToken);

        var composeHost = new ComposeHost(
            (int)app.Uid,
            root,
            BridgeName(app.Uid),
            images,
            mappings,
            app.Outside ? InternetBridgeName(app.Uid) : null,
            app.GrantedElevated,
            DeviceGroups(app.GrantedElevated));
        var rewritten = ComposeRewriter.Rewrite(validation.Document!, app.Manifest, composeHost);
        var run = Path.Combine(root, AppStorage.RunFolder);
        Directory.CreateDirectory(run);
        await File.WriteAllTextAsync(Path.Combine(run, ComposeFile), rewritten, cancellationToken);
        await VolumesAsync(root, validation.Document!, app, mappings, cancellationToken);

        progress?.Invoke(new Jobs.JobProgress(2, 3, 0, 0, "start"));
        var project = ComposeRewriter.ProjectName(app.AppId);
        var up = await commands.RunAsync(
            new CommandRequest("docker", Compose(project, run, "up", "--detach", "--wait", "--remove-orphans"), StartTimeout + TimeSpan.FromMinutes(1)),
            cancellationToken);
        if (up.ExitCode != 0)
        {
            LogStartFailed(app.AppId, up.ExitCode);
            throw new PithyxException(ErrorCodes.AppBackendStartFailed, new Dictionary<string, object?> { ["exitCode"] = up.ExitCode });
        }
        var endpoints = Endpoints(app.Manifest, project, await engine.ContainersAsync(app.AppId, cancellationToken));
        await HealthyAsync(app, endpoints, cancellationToken);
        backends.SetRunning(app.AppId, endpoints);
        progress?.Invoke(new Jobs.JobProgress(3, 3, 0, 0, null));
        LogStarted(app.AppId, app.Version, endpoints.Count);
    }

    /// <summary>
    /// Takes over a backend whose containers still run (after a daemon restart): when every service of the Compose project
    /// runs and every endpoint has an address, the proxy reaches it again without a new start.
    /// </summary>
    public async Task<bool> AdoptAsync(BackendApp app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.HasBackend || !options.Apply)
        {
            return false;
        }
        var containers = await engine.ContainersAsync(app.AppId, cancellationToken);
        if (containers.Count == 0 || containers.Any(container => container.State != "running"))
        {
            return false;
        }
        var endpoints = Endpoints(app.Manifest, ComposeRewriter.ProjectName(app.AppId), containers);
        if (endpoints.Count != app.Manifest.GetProperty("backend").GetProperty("endpoints").EnumerateObject().Count())
        {
            return false;
        }
        backends.SetRunning(app.AppId, endpoints);
        return true;
    }

    /// <summary>
    /// The manifest's <c>health</c> (D327): <c>GET path</c> on its endpoint must answer 2xx within
    /// <see cref="HealthTimeout"/> before the backend counts as running; Compose's healthchecks have passed already.
    /// </summary>
    private static async Task HealthyAsync(BackendApp app, IReadOnlyDictionary<string, Uri> endpoints, CancellationToken cancellationToken)
    {
        if (!app.Manifest.GetProperty("backend").TryGetProperty("health", out var health))
        {
            return;
        }
        var endpoint = health.GetProperty("endpoint").GetString()!;
        if (!endpoints.TryGetValue(endpoint, out var address))
        {
            throw new PithyxException(ErrorCodes.AppBackendStartFailed, new Dictionary<string, object?> { ["exitCode"] = 0 });
        }
        var url = new Uri(address, health.GetProperty("path").GetString()!.TrimStart('/'));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HealthTimeout);
        try
        {
            while (true)
            {
                try
                {
                    using var answer = await _healthClient.GetAsync(url, deadline.Token);
                    if (answer.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !deadline.IsCancellationRequested)
                {
                    // Not listening yet, or a single slow answer: the next try decides.
                }
                await Task.Delay(HealthPollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PithyxException(ErrorCodes.AppBackendStartFailed, new Dictionary<string, object?> { ["exitCode"] = 0 });
        }
    }

    /// <summary>
    /// Takes the backend out of the proxy and removes its containers; volumes and images stay. With the app's pool, Compose
    /// reads the rewritten file as for the start; without it (the pool is gone), it finds the containers by project name.
    /// </summary>
    public async Task StopAsync(string appId, string? pool, CancellationToken cancellationToken)
    {
        backends.SetStopped(appId);
        if (!options.Apply || !await engine.PingAsync(cancellationToken))
        {
            return;
        }
        var project = ComposeRewriter.ProjectName(appId);
        var run = pool is null ? null : storage.RootOf(pool, appId) is { } root ? Path.Combine(root, AppStorage.RunFolder) : null;
        var stop = new[] { "down", "--timeout", StopSeconds.ToString(CultureInfo.InvariantCulture), "--remove-orphans" };
        var arguments = run is not null && File.Exists(Path.Combine(run, ComposeFile))
            ? Compose(project, run, stop)
            : ["compose", "--project-name", project, "--env-file", "/dev/null", .. stop];
        var down = await commands.RunAsync(new CommandRequest("docker", arguments, TimeSpan.FromMinutes(3)), cancellationToken);
        if (down.ExitCode != 0)
        {
            LogStopFailed(appId, down.ExitCode);
            throw new PithyxException(ErrorCodes.AppDockerUnavailable);
        }
        LogStopped(appId);
    }

    /// <summary>
    /// Removes images an app no longer needs (after an uninstall, or the old version's after an update) unless another
    /// lock still names them; Docker refuses to remove one a container uses. Stops Docker when no backend app is left.
    /// </summary>
    public async Task ForgetAsync(IReadOnlySet<string> images, IReadOnlySet<string> kept, bool lastBackend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(kept);
        if (!options.Apply || !await engine.PingAsync(cancellationToken))
        {
            return;
        }
        foreach (var image in images.Where(image => !kept.Contains(image)).Order(StringComparer.Ordinal))
        {
            await engine.RemoveImageAsync(image, cancellationToken);
        }
        if (lastBackend)
        {
            await host.StopAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The storage requests a backend service must have that are not mapped yet; the backend cannot start before an
    /// administrator maps them (after the install, which is why an install does not try then).
    /// </summary>
    public static IReadOnlyList<string> UnmappedRequired(BackendApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Manifest.TryGetProperty("storage", out var requests))
        {
            return [];
        }
        return [.. requests.EnumerateArray()
            .Where(request => request.GetProperty("required").GetBoolean() && request.TryGetProperty("services", out _))
            .Select(request => request.GetProperty("id").GetString()!)
            .Where(id => app.Storage.All(mapping => mapping.StorageId != id))];
    }

    /// <summary>The arguments of a Compose call on the rewritten file, without a project <c>.env</c> (D310).</summary>
    public static IReadOnlyList<string> Compose(string project, string run, params string[] command) =>
        ["compose", "--project-name", project, "--project-directory", run, "--file", Path.Combine(run, ComposeFile), "--env-file", "/dev/null", "--progress", "plain", .. command];

    /// <summary>
    /// Where each endpoint of the manifest answers: the address of its service's running container on the app's network
    /// and the endpoint's port. An endpoint without a running container is left out, so the proxy answers 503.
    /// </summary>
    public static IReadOnlyDictionary<string, Uri> Endpoints(JsonElement manifest, string project, IReadOnlyList<DockerContainer> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        var endpoints = new SortedDictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var endpoint in manifest.GetProperty("backend").GetProperty("endpoints").EnumerateObject())
        {
            var service = endpoint.Value.GetProperty("service").GetString();
            var port = endpoint.Value.GetProperty("port").GetInt32();
            var container = containers.FirstOrDefault(candidate => candidate.Service == service && candidate.State == "running"
                && candidate.Addresses.ContainsKey(project));
            if (container is not null)
            {
                endpoints[endpoint.Name] = new Uri($"http://{container.Addresses[project]}:{port.ToString(CultureInfo.InvariantCulture)}/");
            }
        }
        return endpoints;
    }

    /// <summary>The folders of the storage requests: a required one must be mapped and its shared folder reachable.</summary>
    private async Task<IReadOnlyDictionary<string, StorageMapping>> MappingsAsync(BackendApp app, CancellationToken cancellationToken)
    {
        var mapped = new Dictionary<string, StorageMapping>(StringComparer.Ordinal);
        var requests = app.Manifest.TryGetProperty("storage", out var storageRequests) ? storageRequests.EnumerateArray().ToList() : [];
        if (requests.Count == 0)
        {
            return mapped;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        foreach (var request in requests)
        {
            var id = request.GetProperty("id").GetString()!;
            var required = request.GetProperty("required").GetBoolean() && request.TryGetProperty("services", out _);
            var mapping = app.Storage.FirstOrDefault(candidate => candidate.StorageId == id);
            if (mapping is null)
            {
                if (required)
                {
                    throw StorageUnavailable(id, "unmapped");
                }
                continue;
            }
            var share = await connection.QuerySingleOrDefaultAsync<(string Path, string? Dataset, long? ReadGid, long? WriteGid)>(
                "SELECT path, dataset, read_gid, write_gid FROM shares WHERE id = @ShareId", new { mapping.ShareId });
            if (share.Path is null || ShareAvailability.Reason(share.Dataset, pools.Current, pools.Available) is not null)
            {
                if (required)
                {
                    throw StorageUnavailable(id, "unavailable");
                }
                continue;
            }
            var readOnly = mapping.Access == AppStorageAccess.Read;
            var gid = readOnly ? share.ReadGid : share.WriteGid;
            mapped[id] = new StorageMapping(Path.Combine(share.Path, Files.FsPath.ForOpen(mapping.Path)).TrimEnd('/'), readOnly, gid is { } value ? (int)value : null);
        }
        return mapped;
    }

    /// <summary>Brings every image of the lock in by its digest and returns each reference with its checked id.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ImagesAsync(string appId, string version, ImagesLock lockFile, CancellationToken cancellationToken)
    {
        var architecture = ImagesLock.HostArchitecture ?? throw ImagesLock.Invalid("platform");
        // A private catalog's images come with its read token (D328); everything else is pulled anonymously.
        (string? Token, string? Host) credentials;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            credentials = await connection.QuerySingleOrDefaultAsync<(string?, string?)>(
                "SELECT c.registry_token, c.registry_host FROM apps a JOIN catalogs c ON c.catalog_id = a.catalog_id WHERE a.id = @appId",
                new { appId });
        }
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (reference, platforms) in lockFile.Images)
        {
            if (!platforms.TryGetValue(architecture, out var image))
            {
                throw ImagesLock.Invalid("platform");
            }
            if (await engine.ImageIdAsync(image.Config, cancellationToken) is null)
            {
                var archive = Path.Combine(version, PackageLayout.BackendFolder, "images", architecture, image.Config["sha256:".Length..] + ".tar");
                try
                {
                    if (File.Exists(archive))
                    {
                        await using var stream = File.OpenRead(archive);
                        foreach (var loaded in await engine.LoadAsync(stream, cancellationToken))
                        {
                            // An archive may carry more than the one image; whatever else came along goes again.
                            if (loaded != image.Config)
                            {
                                await engine.RemoveImageAsync(loaded, cancellationToken);
                            }
                        }
                    }
                    else
                    {
                        // The token only goes to the registry it was given for.
                        var token = credentials.Host is { } host && reference.StartsWith(host + "/", StringComparison.Ordinal) ? credentials.Token : null;
                        await engine.PullAsync(ImagesLock.PullReference(reference, image), architecture, cancellationToken, token);
                    }
                }
                catch (DockerEngineException exception)
                {
                    LogImageFailed(exception, reference);
                    throw new PithyxException(ErrorCodes.AppImageUnavailable, new Dictionary<string, object?> { ["image"] = Shorten(reference) });
                }
                if (await engine.ImageIdAsync(image.Config, cancellationToken) is null)
                {
                    throw new PithyxException(ErrorCodes.AppImageMismatch, new Dictionary<string, object?> { ["image"] = Shorten(reference) });
                }
            }
            ids[reference] = image.Config;
        }
        return ids;
    }

    /// <summary>
    /// Creates the folders of the named volumes (and of unmapped optional storage) in the app's dataset, owned by the
    /// app's uid, or by the remapped container root for <c>backend.root</c>; existing folders keep their owner.
    /// </summary>
    private async Task VolumesAsync(string root, ComposeMap document, BackendApp app, IReadOnlyDictionary<string, StorageMapping> mappings, CancellationToken cancellationToken)
    {
        var names = document.Get("volumes") is ComposeMap volumes ? volumes.Entries.Select(entry => entry.Key) : [];
        var owner = app.Manifest.GetProperty("backend").TryGetProperty("root", out var rootFlag) && rootFlag.GetBoolean()
            ? DockerHost.RemapBase
            : app.Uid;
        var folder = Path.Combine(root, AppStorage.VolumesFolder);
        Directory.CreateDirectory(folder);
        foreach (var name in names.Where(name => !mappings.ContainsKey(name)).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(folder, name);
            if (Directory.Exists(path))
            {
                continue;
            }
            Directory.CreateDirectory(path);
            var ids = owner.ToString(CultureInfo.InvariantCulture);
            var result = await commands.RunAsync(new CommandRequest("chown", [$"{ids}:{ids}", path]), cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new PithyxException(ErrorCodes.StorageCommandFailed, new Dictionary<string, object?> { ["command"] = "chown", ["exitCode"] = result.ExitCode });
            }
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
        }
    }

    private static PithyxException StorageUnavailable(string storageId, string reason) =>
        new(ErrorCodes.AppBackendStorageUnavailable, new Dictionary<string, object?> { ["storage"] = storageId, ["reason"] = reason });

    private static string Shorten(string value) => value.Length > 200 ? value[..200] : value;

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the backend of app {AppId} {Version} with {Endpoints} reachable endpoints")]
    private partial void LogStarted(string appId, string version, int endpoints);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backend of app {AppId} did not start: docker compose up ended with {ExitCode}")]
    private partial void LogStartFailed(string appId, int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped the backend of app {AppId}")]
    private partial void LogStopped(string appId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping the backend of app {AppId} failed: docker compose down ended with {ExitCode}")]
    private partial void LogStopFailed(string appId, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bringing in the image {Image} failed")]
    private partial void LogImageFailed(Exception exception, string image);
}
