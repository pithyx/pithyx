using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Apps;

/// <summary>Where the special folder Apps lives.</summary>
/// <param name="Apply">On a box: the folder is a small read-only tmpfs with one bind mount per app. In simulation each app gets a plain folder there.</param>
/// <param name="Root">The folder the share shows.</param>
public sealed record AppsShareOptions(bool Apply, string Root)
{
    public const string BoxRoot = "/run/pithyx/apps";
}

/// <summary>One folder of Apps: the volumes of an app on the app pool.</summary>
/// <param name="Id">The app's id, the folder's name.</param>
/// <param name="Source">The app's <c>volumes/</c> folder.</param>
/// <param name="Dataset">The app's dataset, or null in simulation.</param>
public sealed record AppsFolder(string Id, string Source, string? Dataset);

/// <summary>One step that brings the folders of Apps in line.</summary>
public abstract record AppsFolderAction(string Target);

public sealed record UnmountAppsFolder(string Target) : AppsFolderAction(Target);

public sealed record MountAppsFolder(string Source, string Target) : AppsFolderAction(Target);

/// <summary>An app folder of Apps as Files shows it.</summary>
/// <param name="Id">The app's id, the folder's name.</param>
/// <param name="Name">The app's name in English, or null when it is not installed.</param>
/// <param name="Installed">False for the data an uninstall kept.</param>
/// <param name="Running">Its backend runs or is on its way: Files warns before writing into its folder.</param>
public sealed record AppsFolderInfo(string Id, string? Name, bool Installed, bool Running);

/// <summary>Apps for the user who may use it.</summary>
/// <param name="ShareId">The share Files opens.</param>
/// <param name="Folders">Every app folder, by id.</param>
/// <param name="SmbConnections">How many SMB connections use Apps now; only administrators get it, for the uninstall dialog.</param>
public sealed record AppsShareState(long ShareId, IReadOnlyList<AppsFolderInfo> Folders, int? SmbConnections);

/// <summary>
/// The special folder Apps (D409, after D314): a system share with one folder per app on the app pool that shows the
/// app's <c>volumes/</c> and nothing else of it. Samba cannot show a part of a folder, so on a box
/// <see cref="AppsShareOptions.BoxRoot"/> is a small tmpfs, read-only so nothing can be put next to the app folders,
/// with a bind mount per app that follows no symlinks (<c>nosymfollow</c>), so neither SMB nor Files leaves an app's
/// folder through a link the app made. The folders follow the registry and the pools, never polled; the data an
/// uninstall kept stays as a folder. Before a pool is exported or destroyed and before an app's dataset is destroyed,
/// the bind mounts of its datasets go (<see cref="ReleaseBelowAsync"/>): <c>zfs unmount</c> would leave them behind,
/// and a file held open over SMB keeps a dataset busy until Samba lets go of the share. Rights come only from the
/// daemon: no POSIX ACLs; Samba works as root in this share and gives new files the owner of their folder, the app.
/// </summary>
public sealed partial class AppsShare(
    SqliteConnectionFactory connections,
    ICommandRunner commands,
    SystemFiles files,
    PoolInventory pools,
    AppRegistry registry,
    AppStorageOptions storage,
    AppsShareOptions options,
    PermissionService permissions,
    Proxy.AppBackends backends,
    FileServices.FileServiceActivity activity,
    ShareChanges shareChanges,
    TimeProvider time,
    ILogger<AppsShare> logger) : BackgroundService
{
    /// <summary>The value of <c>shares.system</c> for Apps.</summary>
    public const string SystemKey = "apps";

    /// <summary>The name users see in Files and over SMB.</summary>
    public const string Name = "Apps";

    /// <summary>The row's name in <c>shares</c>; never shown.</summary>
    public const string RowName = "system:apps";

    /// <summary>How long after an SMB connect or disconnect the connections are counted again.</summary>
    public static readonly TimeSpan CountDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan _smbTimeout = TimeSpan.FromSeconds(10);

    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Lock _lock = new();
    private ITimer? _countTimer;
    private int _smbConnections;
    private string? _announced;
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    /// <summary>The share's id once <see cref="EnsureRowAsync"/> ran.</summary>
    public long? ShareId { get; private set; }

    /// <summary>
    /// Adds the row of Apps unless it is there and points it at this mode's folder. Only administrators have it until
    /// others are granted it; it is offered over SMB and nothing else.
    /// </summary>
    public async Task EnsureRowAsync(CancellationToken cancellationToken)
    {
        if (!options.Apply)
        {
            Directory.CreateDirectory(options.Root);
        }
        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO shares (name, path, system, smb, sftp, webdav, ftp, created_at, updated_at)
            SELECT @name, @path, @system, 1, 0, 0, 0, @now, @now WHERE NOT EXISTS (SELECT 1 FROM shares WHERE system = @system)
            """,
            new { name = RowName, path = options.Root, system = SystemKey, now });
        await connection.ExecuteAsync(
            "UPDATE shares SET path = @path, name = @name, sftp = 0, webdav = 0, ftp = 0, updated_at = @now WHERE system = @system AND (path <> @path OR name <> @name OR sftp + webdav + ftp > 0)",
            new { name = RowName, path = options.Root, system = SystemKey, now });
        ShareId = await connection.ExecuteScalarAsync<long>("SELECT id FROM shares WHERE system = @system", new { system = SystemKey });
    }

    /// <summary>
    /// Why Apps cannot be used now (<see cref="ShareAvailability"/>), or null when it can: it follows the app pool's
    /// <c>pithyx/apps</c>, in simulation the pool alone.
    /// </summary>
    public static string? Unavailable(string? pool, PoolList current, bool poolsRead, bool useZfs)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (pool is null)
        {
            return ShareAvailability.PoolMissing;
        }
        if (useZfs)
        {
            return ShareAvailability.Reason($"{pool}/pithyx/apps", current, poolsRead);
        }
        return !poolsRead || current.Pools.Any(candidate => candidate.Name == pool) ? null : ShareAvailability.PoolMissing;
    }

    /// <summary>The app folders Apps should show: every app dataset on the app pool that is mounted and has its volumes.</summary>
    public IReadOnlyList<AppsFolder> Wanted()
    {
        if (registry.Pool is not { } pool)
        {
            return [];
        }
        if (!storage.UseZfs)
        {
            var apps = Path.Combine(storage.FolderRoot, pool, ".pithyx", "apps");
            return !Directory.Exists(apps)
                ? []
                : [.. Directory.EnumerateDirectories(apps)
                    .Select(folder => new AppsFolder(Path.GetFileName(folder), Path.Combine(folder, AppStorage.VolumesFolder), null))
                    .Where(folder => Manifest.AppId.IsValid(folder.Id) && Directory.Exists(folder.Source))
                    .OrderBy(folder => folder.Id, StringComparer.Ordinal)];
        }
        var prefix = $"{pool}/pithyx/apps/";
        HashSet<string> held;
        lock (_lock)
        {
            held = [.. _held];
        }
        return [.. pools.Current.Datasets
            .Where(dataset => dataset.Name.StartsWith(prefix, StringComparison.Ordinal) && !dataset.Name[prefix.Length..].Contains('/')
                && dataset is { Mounted: true, MountPoint: not null } && !held.Contains(dataset.Name))
            .Select(dataset => new AppsFolder(dataset.Name[prefix.Length..], Path.Combine(dataset.MountPoint!, AppStorage.VolumesFolder), dataset.Name))
            .Where(folder => Manifest.AppId.IsValid(folder.Id) && Directory.Exists(folder.Source))
            .OrderBy(folder => folder.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The difference between the wanted app folders and the mounts below <paramref name="root"/>. A bind mount stays
    /// only while it shows the <c>volumes/</c> of the app's mounted dataset; anything else below the root goes.
    /// </summary>
    public static IReadOnlyList<AppsFolderAction> Plan(string root, IReadOnlyList<AppsFolder> wanted, IReadOnlyList<MountEntry> mounts)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(mounts);
        var prefix = root.TrimEnd('/') + "/";
        var targets = wanted.ToDictionary(folder => prefix + folder.Id, StringComparer.Ordinal);
        var actions = new List<AppsFolderAction>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts.Where(mount => mount.Target.StartsWith(prefix, StringComparison.Ordinal)).OrderByDescending(mount => mount.Target.Length).ThenBy(mount => mount.Target, StringComparer.Ordinal))
        {
            if (targets.TryGetValue(mount.Target, out var folder)
                && folder.Dataset is { } dataset
                && mount.Source == dataset
                && mount.Root == "/" + AppStorage.VolumesFolder
                && MountTable.Containing(mounts, folder.Source) is { } origin
                && origin.Source == dataset
                && origin.MajorMinor == mount.MajorMinor
                && present.Add(mount.Target))
            {
                continue;
            }
            actions.Add(new UnmountAppsFolder(mount.Target));
        }
        foreach (var (target, folder) in targets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!present.Contains(target))
            {
                actions.Add(new MountAppsFolder(folder.Source, target));
            }
        }
        return actions;
    }

    /// <summary>Brings the app folders in line with the registry and the pools. Never throws but for cancellation.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var wanted = Wanted();
            if (!options.Apply)
            {
                // Simulation runs no backend, so a plain folder per app stands in for its volumes; it goes with the app's data.
                foreach (var folder in wanted)
                {
                    Directory.CreateDirectory(Path.Combine(options.Root, folder.Id));
                }
                foreach (var folder in Directory.EnumerateDirectories(options.Root).Where(folder => !wanted.Any(entry => entry.Id == Path.GetFileName(folder))))
                {
                    Directory.Delete(folder, recursive: true);
                }
                return;
            }
            var mounts = await MountsAsync(cancellationToken);
            if (!await EnsureRootAsync(mounts, cancellationToken))
            {
                return;
            }
            var actions = Plan(options.Root, wanted, mounts);
            var leftovers = Directory.EnumerateDirectories(options.Root)
                .Where(folder => !wanted.Any(entry => Path.Combine(options.Root, entry.Id) == folder) && !actions.Any(action => action.Target == folder))
                .ToList();
            if (actions.Count == 0 && leftovers.Count == 0)
            {
                return;
            }
            await RunAsync(new CommandRequest("mount", ["-o", "remount,rw", "--", options.Root]), cancellationToken);
            try
            {
                foreach (var action in actions)
                {
                    switch (action)
                    {
                        case UnmountAppsFolder unmount:
                            await UnmountAsync(unmount.Target, cancellationToken);
                            TryRemove(unmount.Target);
                            break;
                        case MountAppsFolder mount:
                            await MountAsync(mount, cancellationToken);
                            break;
                    }
                }
                foreach (var folder in leftovers)
                {
                    TryRemove(folder);
                }
            }
            finally
            {
                await RunAsync(new CommandRequest("mount", ["-o", "remount,ro", "--", options.Root]), CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The next change tries again.
            LogFailed(exception);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// Takes away the app folders whose volumes lie below one of <paramref name="paths"/> (the mount points of the
    /// datasets an export, destroy or uninstall is about to unmount), after Samba let go of Apps, and keeps them away
    /// until the returned hold is disposed, after the operation; then what is still there comes back. Real mode only.
    /// </summary>
    public async Task<IDisposable> ReleaseBelowAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!options.Apply || paths.Count == 0)
        {
            return new Hold(this, []);
        }
        var held = pools.Current.Datasets
            .Where(dataset => dataset.MountPoint is { } mount && paths.Any(path => FsPath.IsSameOrInside(mount, path) || FsPath.IsSameOrInside(path, mount)) && dataset.Name.Contains("/pithyx/apps/", StringComparison.Ordinal))
            .Select(dataset => dataset.Name)
            .ToList();
        lock (_lock)
        {
            _held.UnionWith(held);
        }
        var hold = new Hold(this, held);
        try
        {
            await UnmountBelowAsync(paths, cancellationToken);
        }
        catch
        {
            hold.Dispose();
            throw;
        }
        return hold;
    }

    private async Task UnmountBelowAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var mounts = await MountsAsync(cancellationToken);
            var prefix = options.Root.TrimEnd('/') + "/";
            var datasets = paths
                .Select(path => MountTable.Containing(mounts, path))
                .OfType<MountEntry>()
                .Where(origin => origin.FileSystem == "zfs")
                .Select(origin => origin.Source)
                .ToHashSet(StringComparer.Ordinal);
            var below = mounts
                .Where(mount => mount.Target.StartsWith(prefix, StringComparison.Ordinal)
                    && (datasets.Any(dataset => mount.Source == dataset || mount.Source.StartsWith(dataset + "/", StringComparison.Ordinal))
                        || paths.Any(path => FsPath.IsSameOrInside(mount.Target, path))))
                .Select(mount => mount.Target)
                .ToList();
            if (below.Count == 0)
            {
                return;
            }
            await CloseSmbAsync(cancellationToken);
            await RunAsync(new CommandRequest("mount", ["-o", "remount,rw", "--", options.Root]), cancellationToken);
            try
            {
                foreach (var target in below)
                {
                    await UnmountAsync(target, cancellationToken);
                    TryRemove(target);
                }
            }
            finally
            {
                await RunAsync(new CommandRequest("mount", ["-o", "remount,ro", "--", options.Root]), CancellationToken.None);
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Apps with its app folders, for a user who may use it; <see cref="ErrorCodes.FsShareNotFound"/> otherwise.</summary>
    public AppsShareState State(ClaimsPrincipal user)
    {
        if (ShareId is not { } shareId || registry.Pool is null || permissions.ForUser(user).Share(shareId) == ShareAccess.None)
        {
            throw new PithyxException(ErrorCodes.FsShareNotFound);
        }
        var installed = registry.Apps;
        var folders = Wanted()
            .Select(folder => installed.TryGetValue(folder.Id, out var app)
                ? new AppsFolderInfo(folder.Id, NameOf(app), true, backends.StatusOf(folder.Id) is Proxy.AppBackends.Starting or Proxy.AppBackends.Running or Proxy.AppBackends.Updating)
                : new AppsFolderInfo(folder.Id, null, false, false))
            .ToList();
        var admin = user.IsInRole(SessionAuthenticationHandler.AdminRole);
        return new AppsShareState(shareId, folders, admin ? Volatile.Read(ref _smbConnections) : null);
    }

    /// <summary>The number of tree connections to Apps in <c>smbstatus --json</c>.</summary>
    public static int CountConnections(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tcons", out var tcons) || tcons.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        return tcons.EnumerateObject().Count(tcon =>
            tcon.Value.TryGetProperty("service", out var service) && string.Equals(service.GetString(), Name, StringComparison.OrdinalIgnoreCase));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Signal() => _changes.Writer.TryWrite(true);
        pools.Changed += Signal;
        registry.Changed += Signal;
        activity.SmbConnectionsChanged += ConnectionsChanged;
        try
        {
            Signal();
            if (options.Apply)
            {
                await CountConnectionsAsync(stoppingToken);
            }
            await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
            {
                await ReconcileAsync(stoppingToken);
                // Whether Apps can be used follows the app pool; the share lists and SMB hear of it when that changes.
                var availability = $"{registry.Pool}|{Unavailable(registry.Pool, pools.Current, pools.Available, storage.UseZfs)}";
                if (availability != _announced)
                {
                    _announced = availability;
                    shareChanges.Announce();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the folders stay for SMB and come back in line at the next start.
        }
        finally
        {
            pools.Changed -= Signal;
            registry.Changed -= Signal;
            activity.SmbConnectionsChanged -= ConnectionsChanged;
        }
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            _countTimer?.Dispose();
        }
        _running.Dispose();
        base.Dispose();
    }

    private void ConnectionsChanged(string share)
    {
        if (!options.Apply || !string.Equals(share, Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        lock (_lock)
        {
            // A burst of connects (Explorer opens several) is counted once.
            _countTimer ??= time.CreateTimer(_ => _ = CountConnectionsAsync(CancellationToken.None), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _countTimer.Change(CountDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task CountConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!FileServices.FileServiceSync.Installed("smbd"))
            {
                return;
            }
            var status = await commands.RunAsync(new CommandRequest("smbstatus", ["--json"], _smbTimeout), cancellationToken);
            Volatile.Write(ref _smbConnections, status.ExitCode == 0 ? CountConnections(status.StandardOutput) : 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCountFailed(exception);
        }
    }

    /// <summary>Ends every SMB connection to Apps, so open files no longer keep an app's dataset busy.</summary>
    private async Task CloseSmbAsync(CancellationToken cancellationToken)
    {
        if (!FileServices.FileServiceSync.Installed("smbd"))
        {
            return;
        }
        var result = await commands.RunAsync(new CommandRequest("smbcontrol", ["smbd", "close-share", Name], _smbTimeout), cancellationToken);
        if (result.ExitCode != 0)
        {
            LogCloseFailed(result.ExitCode);
        }
    }

    /// <summary>Keeps released app folders away while their datasets are exported, destroyed or uninstalled.</summary>
    private sealed class Hold(AppsShare owner, IReadOnlyList<string> datasets) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || datasets.Count == 0)
            {
                return;
            }
            lock (owner._lock)
            {
                owner._held.ExceptWith(datasets);
            }
            owner._changes.Writer.TryWrite(true);
        }
    }

    private async Task<IReadOnlyList<MountEntry>> MountsAsync(CancellationToken cancellationToken) =>
        MountTable.ParseMountInfo(await File.ReadAllTextAsync(files.MountInfo, cancellationToken));

    /// <summary>Mounts the tmpfs of Apps unless it is there; false when it cannot be.</summary>
    private async Task<bool> EnsureRootAsync(IReadOnlyList<MountEntry> mounts, CancellationToken cancellationToken)
    {
        if (mounts.Any(mount => mount.Target == options.Root && mount.FileSystem == "tmpfs"))
        {
            return true;
        }
        Directory.CreateDirectory(options.Root);
        var result = await commands.RunAsync(
            new CommandRequest("mount", ["-t", "tmpfs", "-o", "mode=0755,size=1m,nr_inodes=20000,nosuid,nodev,noexec,ro", "pithyx-apps", options.Root]),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            LogRootFailed(result.ExitCode);
            return false;
        }
        return true;
    }

    private async Task MountAsync(MountAppsFolder mount, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(mount.Target);
        if ((await commands.RunAsync(new CommandRequest("mount", ["--bind", "--", mount.Source, mount.Target]), cancellationToken)).ExitCode != 0)
        {
            LogMountFailed(mount.Target);
            TryRemove(mount.Target);
            return;
        }
        // Links an app made point somewhere in its container, not on the box: nothing follows them here.
        if ((await commands.RunAsync(new CommandRequest("mount", ["-o", "remount,bind,nosymfollow,nosuid,nodev,noexec", "--", mount.Target]), cancellationToken)).ExitCode != 0)
        {
            LogMountFailed(mount.Target);
            await UnmountAsync(mount.Target, cancellationToken);
            TryRemove(mount.Target);
        }
    }

    private async Task UnmountAsync(string target, CancellationToken cancellationToken)
    {
        if ((await commands.RunAsync(new CommandRequest("umount", ["--", target]), cancellationToken)).ExitCode == 0)
        {
            return;
        }
        // Samba or Files still holds a file: the folder goes from the share now and from the kernel when it closes.
        await CloseSmbAsync(cancellationToken);
        if ((await commands.RunAsync(new CommandRequest("umount", ["--", target]), cancellationToken)).ExitCode != 0)
        {
            await commands.RunAsync(new CommandRequest("umount", ["--lazy", "--", target]), cancellationToken);
        }
    }

    private async Task RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(request, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.StorageCommandFailed, new Dictionary<string, object?> { ["command"] = request.FileName, ["exitCode"] = result.ExitCode });
        }
    }

    private void TryRemove(string folder)
    {
        try
        {
            // Never recursive: a folder that still is a mount point is busy and stays.
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder);
            }
        }
        catch (IOException)
        {
            LogLeftover(folder);
        }
    }

    private static string? NameOf(InstalledApp app) =>
        app.Manifest.TryGetProperty("name", out var name) && name.TryGetProperty("en", out var english) ? english.GetString() : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bringing the folders of Apps in line failed")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mounting the folder {Target} of Apps failed")]
    private partial void LogMountFailed(string target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mounting the folder of Apps failed with exit code {ExitCode}")]
    private partial void LogRootFailed(int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "The folder {Folder} of Apps is still in use and stays for now")]
    private partial void LogLeftover(string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing the SMB connections to Apps failed with exit code {ExitCode}")]
    private partial void LogCloseFailed(int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Counting the SMB connections to Apps failed")]
    private partial void LogCountFailed(Exception exception);
}
