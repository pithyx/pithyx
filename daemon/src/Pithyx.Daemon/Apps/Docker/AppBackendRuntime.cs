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
/// <param name="Internet">Whether the app holds <c>network.internet</c>, which gives its services a second network with a way out.</param>
/// <param name="Storage">The folders the administrator mapped to its storage requests.</param>
public sealed record BackendApp(string AppId, string Version, JsonElement Manifest, long Uid, bool Internet, IReadOnlyList<AppStorageMapping> Storage)
{
    public static BackendApp Of(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new(app.Id, app.Version, app.Manifest, app.Uid, app.HasInternet, app.Storage);
    }

    public bool HasBackend => Manifest.TryGetProperty("backend", out _);
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

    /// <summary>How long a service gets to stop before it is killed.</summary>
    public const int StopSeconds = 30;

    /// <summary>The Linux bridge of an app's internal network: <c>px-</c> and the app's uid without 40000 (D309, at most 15 characters).</summary>
    public static string BridgeName(long uid) => "px-" + (uid - Users.UidAllocator.MinAppUid).ToString(CultureInfo.InvariantCulture);

    /// <summary>The bridge of the second network of an app with <c>network.internet</c>, which is not internal.</summary>
    public static string InternetBridgeName(long uid) => "px-i" + (uid - Users.UidAllocator.MinAppUid).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Starts the backend of an installed version and makes it reachable through the proxy. Throws when something it
    /// needs is missing, when an image cannot be brought in or does not match the lock, or when Compose fails.
    /// </summary>
    public async Task StartAsync(string pool, BackendApp app, Action<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.HasBackend || !options.Apply)
        {
            return;
        }
        var root = storage.RootOf(pool, app.AppId) ?? throw new PithyxException(
            ErrorCodes.AppPoolUnavailable, new Dictionary<string, object?> { ["pool"] = pool, ["reason"] = "locked" });
        var mappings = await MappingsAsync(app, cancellationToken);
        progress?.Invoke("docker");
        await host.EnsureRunningAsync(pool, cancellationToken);

        var version = Path.Combine(root, AppStorage.VersionsFolder, app.Version);
        var composePath = app.Manifest.GetProperty("backend").GetProperty("compose").GetString()!;
        var validation = ComposeValidator.Validate(await File.ReadAllBytesAsync(Path.Combine(version, composePath), cancellationToken), app.Manifest);
        if (!validation.IsValid)
        {
            throw validation.Issues[0].ToException();
        }
        var lockFile = ImagesLock.Parse(await File.ReadAllBytesAsync(Path.Combine(version, ImagesLock.FileName), cancellationToken))
            ?? throw LockInvalid("invalid");
        progress?.Invoke("images");
        var images = await ImagesAsync(version, lockFile, cancellationToken);

        var composeHost = new ComposeHost(
            (int)app.Uid,
            root,
            BridgeName(app.Uid),
            images,
            mappings,
            app.Internet ? InternetBridgeName(app.Uid) : null);
        var rewritten = ComposeRewriter.Rewrite(validation.Document!, app.Manifest, composeHost);
        var run = Path.Combine(root, AppStorage.RunFolder);
        Directory.CreateDirectory(run);
        await File.WriteAllTextAsync(Path.Combine(run, ComposeFile), rewritten, cancellationToken);
        await VolumesAsync(root, validation.Document!, app, mappings, cancellationToken);

        progress?.Invoke("start");
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
        backends.SetRunning(app.AppId, endpoints);
        LogStarted(app.AppId, app.Version, endpoints.Count);
    }

    /// <summary>Takes the backend out of the proxy and removes its containers; volumes and images stay.</summary>
    public async Task StopAsync(string appId, CancellationToken cancellationToken)
    {
        backends.SetStopped(appId);
        if (!options.Apply || !await engine.PingAsync(cancellationToken))
        {
            return;
        }
        var project = ComposeRewriter.ProjectName(appId);
        var down = await commands.RunAsync(
            new CommandRequest("docker", ["compose", "--project-name", project, "down", "--timeout", StopSeconds.ToString(CultureInfo.InvariantCulture), "--remove-orphans"], TimeSpan.FromMinutes(3)),
            cancellationToken);
        if (down.ExitCode != 0)
        {
            LogStopFailed(appId, down.ExitCode);
            throw new PithyxException(ErrorCodes.AppDockerUnavailable);
        }
        LogStopped(appId);
    }

    /// <summary>
    /// After an uninstall: removes the images of the app's lock that no container uses, and stops Docker when no backend
    /// app is left.
    /// </summary>
    public async Task ForgetAsync(byte[]? lockBytes, bool lastBackend, CancellationToken cancellationToken)
    {
        if (!options.Apply || !await engine.PingAsync(cancellationToken))
        {
            return;
        }
        if (lockBytes is not null && ImagesLock.Parse(lockBytes) is { } lockFile && ImagesLock.HostArchitecture is { } architecture)
        {
            foreach (var platforms in lockFile.Images.Values)
            {
                if (platforms.TryGetValue(architecture, out var image))
                {
                    await engine.RemoveImageAsync(image.Config, cancellationToken);
                }
            }
        }
        if (lastBackend)
        {
            await host.StopAsync(cancellationToken);
        }
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
    private async Task<IReadOnlyDictionary<string, string>> ImagesAsync(string version, ImagesLock lockFile, CancellationToken cancellationToken)
    {
        var architecture = ImagesLock.HostArchitecture ?? throw LockInvalid("platform");
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (reference, platforms) in lockFile.Images)
        {
            if (!platforms.TryGetValue(architecture, out var image))
            {
                throw new PithyxException(ErrorCodes.AppPackageLockInvalid, new Dictionary<string, object?> { ["reason"] = "platform", ["architecture"] = architecture });
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
                        await engine.PullAsync(ImagesLock.PullReference(reference, image), architecture, cancellationToken);
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

    private static PithyxException LockInvalid(string reason) =>
        new(ErrorCodes.AppPackageLockInvalid, new Dictionary<string, object?> { ["reason"] = reason, ["architecture"] = ImagesLock.HostArchitecture ?? "" });

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
