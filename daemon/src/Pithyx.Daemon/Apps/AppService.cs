using System.Data.Common;
using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Permissions;
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
    TimeProvider time,
    ILogger<AppService> logger)
{
    /// <summary>The built-in group every user belongs to; a grant to it is "Everybody" (D304).</summary>
    public const long EverybodyGroupId = 100;

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
        var installed = registry.Find(appId);
        var pool = ChoosePool(request.Pool);
        storage.RequirePool(pool);
        if (!registry.TryBegin(appId))
        {
            throw new PithyxException(ErrorCodes.AppAppBusy, new Dictionary<string, object?> { ["id"] = appId });
        }
        try
        {
            package = packages.Take(userId, packageId);
        }
        catch
        {
            registry.End(appId);
            throw;
        }
        var access = request.Access ?? AppAccess.Everybody;
        var kind = installed is null ? "apps.install" : "apps.update";
        try
        {
            return await jobs.StartAsync(userId, kind, new { id = appId, version = package.Version, from = installed?.Version }, async context =>
            {
                try
                {
                    await AppVersions.InstallAsync(storage, pool, appId, package.Package, package.Version, update: installed is not null,
                        token => CommitAsync(package, pool, access, installed, token), context.Cancellation);
                    permissions.Invalidate();
                    await registry.ReloadAsync(CancellationToken.None);
                    LogInstalled(appId, package.Version, installed?.Version);
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

    public async Task<AppDetails> SetEnabledAsync(ClaimsPrincipal caller, string appId, UpdateAppRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        RequireInstalled(appId);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("UPDATE apps SET enabled = @enabled WHERE id = @appId", new { enabled = request.Enabled ? 1 : 0, appId });
        }
        await registry.ReloadAsync(cancellationToken);
        LogEnabled(appId, request.Enabled);
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
                    await using (var connection = await connections.OpenAsync(context.Cancellation))
                    {
                        await connection.ExecuteAsync("DELETE FROM apps WHERE id = @appId", new { appId });
                    }
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
            package.Package.Sha256);
    }

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
                    new { group = EverybodyGroupId, resource = Resources.App(appId) },
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
        static (long[] Core, string[]? Pre) Split(string version)
        {
            var dash = version.IndexOf('-', StringComparison.Ordinal);
            var core = (dash < 0 ? version : version[..dash]).Split('.').Select(part => long.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
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
            var leftNumeric = long.TryParse(leftPre[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(rightPre[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed app {AppId}, data: {Data}")]
    private partial void LogRemoved(string appId, AppDataChoice data);
}
