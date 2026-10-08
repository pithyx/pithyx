using System.Data.Common;
using System.Numerics;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Apps;

/// <summary>What an uninstall does with the app's data.</summary>
public enum AppDataChoice
{
    /// <summary>Removes the frontends but keeps the dataset with the app's volumes; installing the app again finds them.</summary>
    Keep,

    /// <summary>Destroys the app's dataset with every snapshot.</summary>
    Delete,
}

/// <summary>
/// Installing, updating and removing third-party apps (A10): an upload is checked and kept for 15 minutes
/// (<see cref="AppPackages"/>), its install or update runs as a job (<see cref="AppVersions"/>), a removal too.
/// Administrators only; who else may use an app is a grant on <c>app:&lt;id&gt;</c>.
/// </summary>
public sealed partial class AppService(
    AppRegistry registry,
    AppPackages packages,
    AppStorage storage,
    JobService jobs,
    SqliteConnectionFactory connections,
    PermissionService permissions,
    AppTokens tokens,
    ShareService shares,
    Docker.AppBackendRuntime backends,
    Proxy.AppBackends backendStates,
    TimeProvider time,
    ILogger<AppService> logger)
{
    public async Task<AppPackagePreview> UploadAsync(ClaimsPrincipal caller, Stream body, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var package = await packages.UploadAsync(CurrentUser.Id(caller), body, cancellationToken);
        LogChecked(package.AppId, package.Version, package.Package.Sha256);
        return Preview(package);
    }

    public AppPackagePreview GetPreview(ClaimsPrincipal caller, string packageId)
    {
        RequireAdmin(caller);
        return Preview(packages.Get(CurrentUser.Id(caller), packageId));
    }

    public void Discard(ClaimsPrincipal caller, string packageId)
    {
        RequireAdmin(caller);
        packages.Discard(CurrentUser.Id(caller), packageId);
    }

    /// <summary>Starts the install or update of a checked package as a job; the checks that need no files run first.</summary>
    public async Task<Job> InstallAsync(ClaimsPrincipal caller, string packageId, InstallAppRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var userId = CurrentUser.Id(caller);
        var package = packages.Get(userId, packageId);
        var appId = package.AppId;
        var pool = ChoosePool(request.Pool);
        storage.RequirePool(pool);
        if (!registry.TryBegin(appId))
        {
            throw new PithyxException(ErrorCodes.AppAppBusy, new Dictionary<string, object?> { ["id"] = appId });
        }
        // Read under the app's lock, so a removal or install that finished meanwhile is seen.
        InstalledApp? installed;
        try
        {
            installed = registry.Find(appId);
            package = packages.Take(userId, packageId);
        }
        catch
        {
            registry.End(appId);
            throw;
        }
        var access = request.Access ?? SuggestedAccess(package.Manifest);
        var kind = installed is null ? "apps.install" : "apps.update";
        try
        {
            return await jobs.StartAsync(userId, kind, new { id = appId, version = package.Version, from = installed?.Version }, async context =>
            {
                try
                {
                    // The running version's backend stops before the snapshot, so its volumes are still; a failed update
                    // rolls the dataset back and starts it again. Its lock names the images the update may remove afterwards.
                    var previous = installed is not null && installed.Enabled ? Docker.BackendApp.Of(installed) : null;
                    var previousImages = previous?.HasBackend == true ? Docker.ImagesLock.HostImages(LockOf(pool, installed!)) : new HashSet<string>();
                    try
                    {
                        if (previous?.HasBackend == true)
                        {
                            await backends.StopAsync(appId, pool, context.Cancellation);
                            backendStates.SetStatus(appId, Proxy.AppBackends.Updating);
                        }
                        await AppVersions.InstallAsync(storage, pool, appId, package.Package, package.Version, update: installed is not null,
                            async token =>
                            {
                                var next = await NextBackendAsync(package, installed, token);
                                // A required folder can only be mapped once the app is installed; its backend starts then.
                                var start = next.HasBackend && installed?.Enabled != false && Docker.AppBackendRuntime.UnmappedRequired(next).Count == 0;
                                try
                                {
                                    if (start)
                                    {
                                        await backends.StartAsync(pool, next, context.Report, token);
                                    }
                                    await CommitAsync(package, pool, access, installed, token);
                                }
                                catch
                                {
                                    // Before the dataset rolls back: the new containers must not write into the volumes it restores.
                                    if (start)
                                    {
                                        await StopQuietlyAsync(appId, pool);
                                    }
                                    throw;
                                }
                            },
                            context.Cancellation,
                            published: async () =>
                            {
                                permissions.Invalidate();
                                await registry.ReloadAsync(CancellationToken.None);
                            });
                        LogInstalled(appId, package.Version, installed?.Version);
                    }
                    catch
                    {
                        await RecoverBackendAsync(pool, appId, previous);
                        throw;
                    }
                    if (previousImages.Count > 0)
                    {
                        await ForgetImagesAsync(previousImages, lastBackend: false);
                    }
                }
                finally
                {
                    packages.Delete(package);
                    registry.End(appId);
                }
            }, cancellationToken);
        }
        catch
        {
            packages.Delete(package);
            registry.End(appId);
            throw;
        }
    }

    /// <summary>
    /// Enables or disables an app; a disabled app loses its tokens at once. A backend stops, or starts, in a job that
    /// holds the app like an install does.
    /// </summary>
    public async Task<AppDetails> SetEnabledAsync(ClaimsPrincipal caller, string appId, UpdateAppRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var app = RequireInstalled(appId);
        var backend = Docker.BackendApp.Of(app).HasBackend && app.Enabled != request.Enabled;
        await ChangeAsync(appId, backend, async () =>
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync("UPDATE apps SET enabled = @enabled WHERE id = @appId", new { enabled = request.Enabled ? 1 : 0, appId });
            }
            if (!request.Enabled)
            {
                tokens.RevokeApp(appId);
            }
            await registry.ReloadAsync(cancellationToken);
            LogEnabled(appId, request.Enabled);
        }, CurrentUser.Id(caller), request.Enabled ? "apps.backend.start" : "apps.backend.stop", cancellationToken);
        return registry.Details(RequireInstalled(appId));
    }

    /// <summary>
    /// Runs a change of an app and, when its running backend must follow, a job that stops it and starts it again as the
    /// registry now says (a disabled app stays stopped, one with an unmapped required folder too). The app is held from
    /// before the change until the job ends, so no install, removal or other backend job runs in between.
    /// </summary>
    private async Task ChangeAsync(string appId, bool backend, Func<Task> change, long userId, string kind, CancellationToken cancellationToken)
    {
        if (!backend || registry.Pool is not { } pool)
        {
            await change();
            return;
        }
        if (!registry.TryBegin(appId))
        {
            throw new PithyxException(ErrorCodes.AppAppBusy, new Dictionary<string, object?> { ["id"] = appId });
        }
        try
        {
            await change();
            await jobs.StartAsync(userId, kind, new { id = appId }, async context =>
            {
                try
                {
                    await backends.StopAsync(appId, pool, context.Cancellation);
                    if (registry.Find(appId) is { Enabled: true } app && Docker.BackendApp.Of(app) is var next
                        && Docker.AppBackendRuntime.UnmappedRequired(next).Count == 0)
                    {
                        await backends.StartAsync(pool, next, context.Report, context.Cancellation);
                    }
                }
                finally
                {
                    registry.End(appId);
                }
            }, cancellationToken);
        }
        catch
        {
            registry.End(appId);
            throw;
        }
    }

    /// <summary>Grants or revokes permissions of the app's manifest; an update keeps a revoked one revoked.</summary>
    public async Task<AppDetails> SetPermissionsAsync(ClaimsPrincipal caller, string appId, AppPermissionsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var requested = RequireInstalled(appId).Permissions.Select(permission => permission.Permission).ToHashSet(StringComparer.Ordinal);
        foreach (var change in request.Permissions ?? [])
        {
            if (change?.Permission is null || !requested.Contains(change.Permission))
            {
                throw new PithyxException(ErrorCodes.AppPermissionNotRequested, new Dictionary<string, object?> { ["permission"] = Shorten(change?.Permission ?? "") });
            }
        }
        // Only the internet decides how a backend runs: its second network comes or goes with a restart.
        var app = RequireInstalled(appId);
        var internet = (request.Permissions ?? []).FirstOrDefault(change => change?.Permission == "network.internet") is { } wanted && wanted.Granted != app.HasInternet;
        await ChangeAsync(appId, internet && RunsBackend(app), async () =>
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                foreach (var change in request.Permissions ?? [])
                {
                    await connection.ExecuteAsync(
                        "UPDATE app_permissions SET granted = @granted WHERE app_id = @appId AND permission = @permission",
                        new { granted = change.Granted ? 1 : 0, appId, permission = change.Permission },
                        transaction);
                }
                await transaction.CommitAsync(cancellationToken);
            }
            await registry.ReloadAsync(cancellationToken);
            LogPermissions(appId);
        }, CurrentUser.Id(caller), "apps.backend.restart", cancellationToken);
        return registry.Details(RequireInstalled(appId));
    }

    /// <summary>
    /// Maps an existing folder in a shared folder to one storage request of the app's manifest, read-only or (when the
    /// request asks for it) read-write. The app's backend gets it mounted at its next start (A14).
    /// </summary>
    public async Task<AppDetails> MapStorageAsync(ClaimsPrincipal caller, string appId, string storageId, AppStorageMappingRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var app = RequireInstalled(appId);
        var wanted = StorageRequestAccess(app.Manifest, storageId) ?? throw MappingInvalid(storageId, "unknown");
        if (request.Access == AppStorageAccess.ReadWrite && wanted != AppStorageAccess.ReadWrite)
        {
            throw MappingInvalid(storageId, "access");
        }
        var path = FsPath.Normalize(request.Path);
        var share = await shares.ResolveAsync(caller, request.ShareId, write: false, cancellationToken);
        RequireFolder(share.Path, path);
        // The backend mounts its folders when it starts: a running one restarts, and one waiting for a required folder starts.
        await ChangeAsync(appId, app.Enabled && Docker.BackendApp.Of(app).HasBackend, async () =>
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO storage_mappings (app_id, storage_id, share_id, path, access) VALUES (@appId, @storageId, @shareId, @path, @access)
                    ON CONFLICT (app_id, storage_id) DO UPDATE SET share_id = excluded.share_id, path = excluded.path, access = excluded.access
                    """,
                    new { appId, storageId, shareId = share.Id, path, access = request.Access == AppStorageAccess.Read ? "read" : "readWrite" });
            }
            await registry.ReloadAsync(cancellationToken);
            LogMapped(appId, storageId, share.Id, path, request.Access);
        }, CurrentUser.Id(caller), "apps.backend.restart", cancellationToken);
        return registry.Details(RequireInstalled(appId));
    }

    /// <summary>Removes the folder mapped to a storage request; the request is not mapped any more.</summary>
    public async Task<AppDetails> UnmapStorageAsync(ClaimsPrincipal caller, string appId, string storageId, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var app = RequireInstalled(appId);
        if (StorageRequestAccess(app.Manifest, storageId) is null)
        {
            throw MappingInvalid(storageId, "unknown");
        }
        await ChangeAsync(appId, RunsBackend(app), async () =>
        {
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync("DELETE FROM storage_mappings WHERE app_id = @appId AND storage_id = @storageId", new { appId, storageId });
            }
            await registry.ReloadAsync(cancellationToken);
            LogUnmapped(appId, storageId);
        }, CurrentUser.Id(caller), "apps.backend.restart", cancellationToken);
        return registry.Details(RequireInstalled(appId));
    }

    /// <summary>Switches the app's debug log entries on or off (D305).</summary>
    public async Task<AppDetails> SetLogLevelAsync(ClaimsPrincipal caller, string appId, AppLogLevelRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        RequireInstalled(appId);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                "UPDATE apps SET log_level = @level WHERE id = @appId",
                new { level = request.Level == AppLogLevel.Debug ? "debug" : "info", appId });
        }
        await registry.ReloadAsync(cancellationToken);
        LogLevelSet(appId, request.Level);
        return registry.Details(RequireInstalled(appId));
    }

    /// <summary>Starts the removal of an app as a job: its registry entry and grants go first, then its data or its frontends.</summary>
    public async Task<Job> UninstallAsync(ClaimsPrincipal caller, string appId, AppDataChoice data, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        RequireInstalled(appId);
        var pool = registry.Pool;
        if (!registry.TryBegin(appId))
        {
            throw new PithyxException(ErrorCodes.AppAppBusy, new Dictionary<string, object?> { ["id"] = appId });
        }
        try
        {
            return await jobs.StartAsync(CurrentUser.Id(caller), "apps.uninstall", new { id = appId, data }, async context =>
            {
                try
                {
                    var removed = registry.Find(appId);
                    var hadBackend = removed is not null && Docker.BackendApp.Of(removed).HasBackend;
                    // The lock names the images to remove afterwards; it goes with the files.
                    var images = hadBackend && pool is not null ? Docker.ImagesLock.HostImages(LockOf(pool, removed!)) : new HashSet<string>();
                    if (hadBackend)
                    {
                        await backends.StopAsync(appId, pool, context.Cancellation);
                    }
                    await using (var connection = await connections.OpenAsync(context.Cancellation))
                    {
                        await connection.ExecuteAsync("DELETE FROM apps WHERE id = @appId", new { appId });
                        // What the app stored for its users is its data, too.
                        if (data == AppDataChoice.Delete)
                        {
                            await connection.ExecuteAsync("DELETE FROM app_storage WHERE app_id = @appId", new { appId });
                        }
                    }
                    tokens.RevokeApp(appId);
                    permissions.Invalidate();
                    await registry.ReloadAsync(CancellationToken.None);
                    if (pool is not null)
                    {
                        if (data == AppDataChoice.Delete)
                        {
                            await storage.DestroyAsync(pool, appId, context.Cancellation);
                        }
                        else if (storage.RootOf(pool, appId) is { } root)
                        {
                            AppVersions.RemoveFrontends(root);
                        }
                    }
                    if (hadBackend)
                    {
                        await ForgetImagesAsync(images, lastBackend: !registry.Apps.Values.Any(app => Docker.BackendApp.Of(app).HasBackend));
                    }
                    LogRemoved(appId, data);
                }
                finally
                {
                    registry.End(appId);
                }
            }, cancellationToken);
        }
        catch
        {
            registry.End(appId);
            throw;
        }
    }

    /// <summary>The icon of an app the caller may use (or any app for administrators).</summary>
    public async Task<byte[]> IconAsync(ClaimsPrincipal caller, string appId, CancellationToken cancellationToken)
    {
        var rights = permissions.ForUser(caller);
        if (!rights.IsAdmin && !registry.Allows(rights, appId))
        {
            throw NotFound(appId);
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<byte[]?>("SELECT icon FROM apps WHERE id = @appId", new { appId }) ?? throw NotFound(appId);
    }

    public AdminAppList List(ClaimsPrincipal caller)
    {
        RequireAdmin(caller);
        return registry.AdminList();
    }

    private AppPackagePreview Preview(CheckedPackage package)
    {
        var installed = registry.Find(package.AppId);
        var wanted = package.Permissions;
        var had = installed?.Permissions.Select(permission => permission.Permission).ToList() ?? [];
        var action = installed is null ? AppPackageAction.Install : CompareVersions(package.Version, installed.Version) switch
        {
            > 0 => AppPackageAction.Update,
            0 => AppPackageAction.Reinstall,
            _ => AppPackageAction.Downgrade,
        };
        return new AppPackagePreview(
            package.Id,
            package.ExpiresAt,
            package.AppId,
            package.Version,
            package.Manifest,
            action,
            installed?.Version,
            [.. wanted.Except(had, StringComparer.Ordinal)],
            [.. had.Except(wanted, StringComparer.Ordinal)],
            registry.Pool,
            package.Package.Files.Count,
            package.Package.UnpackedBytes,
            package.Package.CompressedBytes,
            package.Package.Sha256,
            SuggestedAccess(package.Manifest));
    }

    /// <summary>
    /// The backend of a package about to be installed: the app's uid (allocated now, in its own transaction, and kept
    /// for the app's id however the install ends), the manifest's permissions without the ones an administrator revoked,
    /// and the storage mappings that still fit.
    /// </summary>
    private async Task<Docker.BackendApp> NextBackendAsync(CheckedPackage package, InstalledApp? installed, CancellationToken cancellationToken)
    {
        long uid;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            uid = await UidAllocator.AllocateAppAsync(connection, transaction, package.AppId, SessionService.Format(time.GetUtcNow()));
            await transaction.CommitAsync(cancellationToken);
        }
        var revoked = installed?.Permissions.Where(state => !state.Granted).Select(state => state.Permission).ToHashSet(StringComparer.Ordinal) ?? [];
        var internet = package.Permissions.Contains("network.internet") && !revoked.Contains("network.internet");
        var storageIds = package.Manifest.TryGetProperty("storage", out var requests)
            ? requests.EnumerateArray().Select(request => request.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var mappings = installed?.Storage.Where(mapping => storageIds.Contains(mapping.StorageId)).ToList() ?? [];
        return new Docker.BackendApp(package.AppId, package.Version, package.Manifest, uid, internet, mappings);
    }

    /// <summary>
    /// After a failed install or update, whose new containers are gone already: the version that ran before starts again
    /// from the rolled-back dataset. A failure here is logged; the install's own error is what the job reports.
    /// </summary>
    private async Task RecoverBackendAsync(string pool, string appId, Docker.BackendApp? previous)
    {
        if (previous?.HasBackend != true || Docker.AppBackendRuntime.UnmappedRequired(previous).Count > 0)
        {
            return;
        }
        try
        {
            await backends.StartAsync(pool, previous, null, CancellationToken.None);
        }
#pragma warning disable CA1031 // Whatever failed, the job reports the install's error.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogRecoveryFailed(exception, appId);
        }
    }

    private async Task StopQuietlyAsync(string appId, string pool)
    {
        try
        {
            await backends.StopAsync(appId, pool, CancellationToken.None);
        }
#pragma warning disable CA1031 // The failure being handled is the one to report.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogRecoveryFailed(exception, appId);
        }
    }

    /// <summary>Removes images unless the lock of an installed app's version still names them; a failure is only logged.</summary>
    private async Task ForgetImagesAsync(IReadOnlySet<string> images, bool lastBackend)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (registry.Pool is { } pool)
        {
            foreach (var app in registry.Apps.Values)
            {
                kept.UnionWith(Docker.ImagesLock.HostImages(LockOf(pool, app)));
            }
        }
        try
        {
            await backends.ForgetAsync(images, kept, lastBackend, CancellationToken.None);
        }
#pragma warning disable CA1031 // Leftover images take space but break nothing.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogForgetFailed(exception);
        }
    }

    /// <summary>The images lock of an installed version, or null.</summary>
    private byte[]? LockOf(string pool, InstalledApp app)
    {
        var path = storage.RootOf(pool, app.Id) is { } root ? Path.Combine(root, AppStorage.VersionsFolder, app.Version, Docker.ImagesLock.FileName) : null;
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static bool RunsBackend(InstalledApp app) => app.Enabled && Docker.BackendApp.Of(app).HasBackend;

    /// <summary>The registry change of an install or update, in one transaction: the last step of <see cref="AppVersions"/>.</summary>
    private async Task CommitAsync(CheckedPackage package, string pool, AppAccess access, InstalledApp? installed, CancellationToken cancellationToken)
    {
        var appId = package.AppId;
        var now = SessionService.Format(time.GetUtcNow());
        var manifest = package.Manifest.GetRawText();
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (installed is null)
        {
            // Two first installs may have chosen different pools; the one that commits first fixes the pool.
            var fixedPool = await connection.ExecuteScalarAsync<string?>(
                "SELECT value FROM settings WHERE key = @key AND EXISTS (SELECT 1 FROM apps)", new { key = AppRegistry.PoolSetting }, transaction);
            if (fixedPool is not null && fixedPool != pool)
            {
                throw new PithyxException(ErrorCodes.AppPoolFixed, new Dictionary<string, object?> { ["pool"] = fixedPool });
            }
            var uid = await UidAllocator.AllocateAppAsync(connection, transaction, appId, now);
            await connection.ExecuteAsync(
                """
                INSERT INTO apps (id, version, manifest, source, icon, package_sha256, uid, enabled, installed_at, updated_at)
                VALUES (@appId, @version, @manifest, 'sideload', @icon, @sha256, @uid, 1, @now, @now)
                """,
                new { appId, version = package.Version, manifest, icon = package.Icon, sha256 = package.Package.Sha256, uid, now },
                transaction);
            if (access == AppAccess.Everybody)
            {
                await connection.ExecuteAsync(
                    "INSERT OR IGNORE INTO grants (subject_kind, subject_id, resource, level) VALUES ('group', @group, @resource, 'allow')",
                    new { group = GroupReconciler.UsersGid, resource = Resources.App(appId) },
                    transaction);
            }
            await connection.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@key, @pool) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                new { key = AppRegistry.PoolSetting, pool },
                transaction);
        }
        else
        {
            await connection.ExecuteAsync(
                "UPDATE apps SET version = @version, manifest = @manifest, icon = @icon, package_sha256 = @sha256, updated_at = @now WHERE id = @appId",
                new { appId, version = package.Version, manifest, icon = package.Icon, sha256 = package.Package.Sha256, now },
                transaction);
        }
        await SyncPermissionsAsync(connection, transaction, appId, package);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>New permissions of the manifest are granted, dropped ones go, revoked ones stay revoked; so do storage mappings.</summary>
    private static async Task SyncPermissionsAsync(DbConnection connection, DbTransaction transaction, string appId, CheckedPackage package)
    {
        var permissions = package.Permissions;
        await connection.ExecuteAsync("DELETE FROM app_permissions WHERE app_id = @appId AND permission NOT IN @permissions", new { appId, permissions }, transaction);
        foreach (var permission in permissions)
        {
            await connection.ExecuteAsync(
                "INSERT OR IGNORE INTO app_permissions (app_id, permission, granted) VALUES (@appId, @permission, 1)",
                new { appId, permission },
                transaction);
        }
        var storageIds = package.Manifest.TryGetProperty("storage", out var storage)
            ? storage.EnumerateArray().Select(request => request.GetProperty("id").GetString()!).ToList()
            : [];
        await connection.ExecuteAsync("DELETE FROM storage_mappings WHERE app_id = @appId AND storage_id NOT IN @storageIds", new { appId, storageIds }, transaction);
    }

    /// <summary>
    /// Who should use an app after its install (D304): administrators only when it asks for elevated rights, root in a
    /// container or the user directory (<c>directory.read</c>), everybody otherwise.
    /// </summary>
    public static AppAccess SuggestedAccess(JsonElement manifest)
    {
        var directory = manifest.TryGetProperty("permissions", out var permissions)
            && permissions.EnumerateArray().Any(permission => permission.GetString() == "directory.read");
        var backend = manifest.TryGetProperty("backend", out var declared) ? declared : default;
        var root = backend.ValueKind == JsonValueKind.Object && backend.TryGetProperty("root", out var flag) && flag.ValueKind == JsonValueKind.True;
        var elevated = backend.ValueKind == JsonValueKind.Object && backend.TryGetProperty("elevated", out var rights) && rights.GetArrayLength() > 0;
        return directory || root || elevated ? AppAccess.Admins : AppAccess.Everybody;
    }

    /// <summary>The access a storage request of the manifest asks for, or null when the manifest has no such request.</summary>
    private static AppStorageAccess? StorageRequestAccess(JsonElement manifest, string storageId)
    {
        if (!manifest.TryGetProperty("storage", out var storage))
        {
            return null;
        }
        foreach (var request in storage.EnumerateArray())
        {
            if (request.GetProperty("id").GetString() == storageId)
            {
                return request.GetProperty("access").GetString() == "readWrite" ? AppStorageAccess.ReadWrite : AppStorageAccess.Read;
            }
        }
        return null;
    }

    /// <summary>Checks that the folder exists below the shared folder's root, without following links out of it.</summary>
    private static void RequireFolder(string shareRoot, string path)
    {
        try
        {
            using var root = LinuxFs.OpenRoot(shareRoot);
            using var folder = LinuxFs.OpenBeneath(root, FsPath.ForOpen(path), LinuxFs.Path | LinuxFs.Directory);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error);
        }
    }

    private static PithyxException MappingInvalid(string storageId, string reason) =>
        new(ErrorCodes.AppMappingInvalid, new Dictionary<string, object?> { ["storage"] = Shorten(storageId), ["reason"] = reason });

    private static string Shorten(string value) => value.Length > 100 ? value[..100] : value;

    private string ChoosePool(string? requested)
    {
        if (registry.Pool is { } current && (requested is null || requested == current))
        {
            return current;
        }
        if (requested is null)
        {
            throw new PithyxException(ErrorCodes.AppPoolRequired);
        }
        // The app pool can change only while no app lives on it.
        if (registry.Pool is { } fixedPool && registry.Apps.Count > 0)
        {
            throw new PithyxException(ErrorCodes.AppPoolFixed, new Dictionary<string, object?> { ["pool"] = fixedPool });
        }
        return requested;
    }

    private InstalledApp RequireInstalled(string appId) => registry.Find(appId) ?? throw NotFound(appId);

    private static PithyxException NotFound(string appId) =>
        new(ErrorCodes.AppAppNotFound, new Dictionary<string, object?> { ["id"] = appId.Length > 100 ? appId[..100] : appId });

    /// <summary>Compares two SemVer 2.0 versions without build metadata: below zero when <paramref name="left"/> is older.</summary>
    public static int CompareVersions(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        static (BigInteger[] Core, string[]? Pre) Split(string version)
        {
            var dash = version.IndexOf('-', StringComparison.Ordinal);
            var core = (dash < 0 ? version : version[..dash]).Split('.').Select(part => BigInteger.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return (core, dash < 0 ? null : version[(dash + 1)..].Split('.'));
        }
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);
        for (var index = 0; index < 3; index++)
        {
            if (leftCore[index] != rightCore[index])
            {
                return leftCore[index].CompareTo(rightCore[index]);
            }
        }
        if (leftPre is null || rightPre is null)
        {
            return (leftPre is null ? 1 : 0) - (rightPre is null ? 1 : 0);
        }
        for (var index = 0; index < Math.Min(leftPre.Length, rightPre.Length); index++)
        {
            var leftNumeric = BigInteger.TryParse(leftPre[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = BigInteger.TryParse(rightPre[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber);
            var compared = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftPre[index], rightPre[index]),
            };
            if (compared != 0)
            {
                return Math.Sign(compared);
            }
        }
        return leftPre.Length.CompareTo(rightPre.Length);
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Checked the package of app {AppId} {Version} (SHA-256 {Sha256})")]
    private partial void LogChecked(string appId, string version, string sha256);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installed app {AppId} {Version} (before: {Previous})")]
    private partial void LogInstalled(string appId, string version, string? previous);

    [LoggerMessage(Level = LogLevel.Information, Message = "App {AppId} enabled: {Enabled}")]
    private partial void LogEnabled(string appId, bool enabled);

    [LoggerMessage(Level = LogLevel.Information, Message = "Permissions of app {AppId} changed")]
    private partial void LogPermissions(string appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mapped storage {StorageId} of app {AppId} to share {ShareId} folder {Path} ({Access})")]
    private partial void LogMapped(string appId, string storageId, long shareId, string path, AppStorageAccess access);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unmapped storage {StorageId} of app {AppId}")]
    private partial void LogUnmapped(string appId, string storageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Log level of app {AppId} set to {Level}")]
    private partial void LogLevelSet(string appId, AppLogLevel level);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up the backend of app {AppId} after a failed install failed")]
    private partial void LogRecoveryFailed(Exception exception, string appId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing images that no app needs any more failed")]
    private partial void LogForgetFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed app {AppId}, data: {Data}")]
    private partial void LogRemoved(string appId, AppDataChoice data);
}
