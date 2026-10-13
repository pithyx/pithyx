using System.Globalization;
using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Users;

/// <summary>
/// What administrators do with the homes (D348): the home pool and moving every home there as a job, the default
/// quota and each user's own, and the homes of deleted users, which stay, are deleted or move into a shared folder.
/// </summary>
public sealed partial class HomeService(
    SqliteConnectionFactory connections,
    Homes homes,
    ICommandRunner commands,
    PoolInventory pools,
    SystemStateService state,
    HomeStorageOptions options,
    JobService jobs,
    FileService files,
    FileOperations operations,
    ShareService shares,
    SftpViews views,
    PermissionService permissions,
    ShareChanges shareChanges,
    TimeProvider time,
    ILogger<HomeService> logger,
    FolderWatches? watches = null)
{
    /// <summary>The smallest quota besides none; ZFS refuses one below the space a dataset needs for itself.</summary>
    public const long MinimumQuotaBytes = 1024 * 1024;

    public const string MoveJobKind = "homes.move";

    public const string ReleaseJobKind = "homes.release";

    private long _moving;

    public async Task<HomeOverview> OverviewAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var current = pools.Current;
        var pool = await homes.PoolAsync(cancellationToken);
        var datasets = current.Datasets.ToDictionary(dataset => dataset.Name, StringComparer.Ordinal);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var users = (await connection.QueryAsync<(long Id, string Username, string? DisplayName, long? Quota)>(
            "SELECT id, username, display_name, home_quota_bytes FROM users")).ToDictionary(user => user.Id);
        var list = (await Homes.RowsAsync(connection)).Select(row =>
        {
            var user = row.UserId is { } id && users.TryGetValue(id, out var found) ? found : default;
            var dataset = row.Dataset is { } name && datasets.TryGetValue(name, out var value) ? value : null;
            return new HomeInfo(
                row.ShareId,
                row.Folder,
                row.UserId,
                user.Username,
                user.DisplayName,
                row.Pool ?? pool,
                dataset?.UsedBytes,
                dataset?.QuotaBytes,
                user.Quota,
                ShareAvailability.Reason(row.Dataset, current, pools.Available));
        }).OrderBy(home => home.UserId is null).ThenBy(home => home.Folder, StringComparer.Ordinal).ToList();
        var candidates = current.Pools.Select(candidate => new HomePool(
            candidate.Name,
            options.UseZfs && datasets.TryGetValue(HomePlans.HomesDataset(candidate.Name), out var root) ? root.AvailableBytes : null,
            homes.Unavailable(current, candidate.Name))).OrderBy(candidate => candidate.Name, StringComparer.Ordinal).ToList();
        var moving = Interlocked.Read(ref _moving);
        return new HomeOverview(pool, candidates, await homes.DefaultQuotaAsync(cancellationToken), list, moving == 0 ? null : moving);
    }

    public async Task<HomeOverview> SetDefaultQuotaAsync(ClaimsPrincipal caller, HomeSettingsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (request.DefaultQuotaBytes is { } bytes && bytes < MinimumQuotaBytes)
        {
            throw QuotaInvalid();
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            if (request.DefaultQuotaBytes is { } value)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                    new { key = Homes.QuotaSetting, value = value.ToString(CultureInfo.InvariantCulture) });
            }
            else
            {
                await connection.ExecuteAsync("DELETE FROM settings WHERE key = @key", new { key = Homes.QuotaSetting });
            }
        }
        await homes.ReconcileAsync(cancellationToken);
        return await OverviewAsync(caller, cancellationToken);
    }

    public async Task<HomeOverview> SetUserQuotaAsync(ClaimsPrincipal caller, long userId, HomeQuotaRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (request.QuotaBytes is { } bytes && bytes != 0 && bytes < MinimumQuotaBytes)
        {
            throw QuotaInvalid();
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            if (await connection.ExecuteAsync("UPDATE users SET home_quota_bytes = @quota WHERE id = @userId", new { userId, quota = request.QuotaBytes }) == 0)
            {
                throw new PithyxException(ErrorCodes.UsersUserNotFound);
            }
        }
        await homes.ReconcileAsync(cancellationToken);
        return await OverviewAsync(caller, cancellationToken);
    }

    /// <summary>
    /// Moves every home to <paramref name="request"/>'s pool as a job, one home after the other: read-only, a snapshot,
    /// <c>zfs send | zfs receive</c> to the new pool, then the row points there and the old dataset goes. A home that
    /// fails stays where it was, writable again; the homes moved before stay moved, and the pool stays the old one.
    /// </summary>
    public async Task<Job> StartMoveAsync(ClaimsPrincipal caller, HomeMoveRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var current = pools.Current;
        var target = request.Pool ?? "";
        if (homes.Unavailable(current, target) is { } reason)
        {
            throw new PithyxException(ErrorCodes.HomesPoolUnavailable, new Dictionary<string, object?> { ["pool"] = target, ["reason"] = reason });
        }
        if (await homes.PoolAsync(cancellationToken) == target)
        {
            throw new PithyxException(ErrorCodes.HomesPoolUnchanged);
        }
        // A home on a pool that is gone cannot be copied; it stays where it is and can be deleted on its own.
        var moving = (await homes.RowsAsync(cancellationToken))
            .Where(row => (row.Pool != target || !options.UseZfs) && !OnMissingPool(row, current))
            .ToList();
        var root = homes.RootOf(current, target)!;
        if (moving.FirstOrDefault(row => Directory.Exists(Path.Combine(root, row.Folder))) is { } taken)
        {
            throw new PithyxException(ErrorCodes.HomesPoolTaken, new Dictionary<string, object?> { ["folder"] = taken.Folder });
        }
        var datasets = current.Datasets.ToDictionary(dataset => dataset.Name, StringComparer.Ordinal);
        var needed = moving.Sum(row => row.Dataset is { } name && datasets.TryGetValue(name, out var dataset) ? dataset.UsedBytes : 0);
        if (options.UseZfs && datasets.TryGetValue(HomePlans.HomesDataset(target), out var homesRoot) && homesRoot.AvailableBytes < needed)
        {
            throw new PithyxException(ErrorCodes.HomesPoolNoSpace, new Dictionary<string, object?> { ["neededBytes"] = needed, ["availableBytes"] = homesRoot.AvailableBytes });
        }
        if (Interlocked.CompareExchange(ref _moving, -1, 0) != 0)
        {
            throw new PithyxException(ErrorCodes.HomesMoveRunning);
        }
        try
        {
            var job = await jobs.StartAsync(CurrentUser.Id(caller), MoveJobKind, new { pool = target }, context => MoveAsync(target, root, moving, needed, context), cancellationToken);
            Interlocked.Exchange(ref _moving, job.Id);
            return job;
        }
        catch
        {
            Interlocked.Exchange(ref _moving, 0);
            throw;
        }
    }

    private async Task MoveAsync(string target, string root, List<HomeRow> moving, long totalBytes, JobContext context)
    {
        var done = 0;
        var bytes = 0L;
        try
        {
            foreach (var row in moving)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                context.Report(new JobProgress(done, moving.Count, bytes, totalBytes, row.Folder));
                var used = row.Dataset is { } name ? pools.Current.Datasets.FirstOrDefault(dataset => dataset.Name == name)?.UsedBytes ?? 0 : 0;
                await MoveOneAsync(target, root, row, context.Cancellation);
                done++;
                bytes += used;
            }
            await homes.SetPoolAsync(target, CancellationToken.None);
            context.Report(new JobProgress(done, moving.Count, totalBytes, totalBytes, null));
            context.SetResult(new { pool = target, moved = done });
            LogMoved(done, target);
        }
        finally
        {
            Interlocked.Exchange(ref _moving, 0);
            await state.RefreshPoolsAsync(CancellationToken.None);
            permissions.Invalidate();
            shareChanges.Announce();
        }
    }

    private async Task MoveOneAsync(string target, string root, HomeRow row, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, row.Folder);
        await ReleaseAsync(row.Path, cancellationToken);
        if (!options.UseZfs)
        {
            using (await homes.LockAsync(cancellationToken))
            {
                Directory.CreateDirectory(root);
                Directory.Move(row.Path, path);
                await PointAsync(row, path, null);
            }
            return;
        }
        var source = row.Dataset!;
        var dataset = HomePlans.Dataset(target, row.Folder);
        var snapshot = SnapshotNames.For(SnapshotKinds.Move, time.GetUtcNow());
        await CopyAsync(commands, source, dataset, snapshot, cancellationToken);
        // From here until the old copy is gone, a check of the homes would take it for a home without a row.
        using var held = await homes.LockAsync(CancellationToken.None);
        await PointAsync(row, path, dataset);
        await commands.RunAsync(HomePlans.DestroySnapshot(dataset, snapshot), CancellationToken.None);
        // The views of the old place go before it is destroyed; nothing writes there since it is read-only.
        await ReleaseAsync(row.Path, CancellationToken.None);
        if ((await commands.RunAsync(HomePlans.Destroy(source), CancellationToken.None)).ExitCode != 0)
        {
            LogLeftover(source);
        }
    }

    /// <summary>
    /// Copies a home's dataset to <paramref name="dataset"/> on another pool and mounts it there: the old one turns
    /// read-only first, so nothing written after its snapshot is lost, and stays so. When a step fails, what the copy
    /// added goes, the old one is writable again and the error is thrown.
    /// </summary>
    internal static async Task CopyAsync(ICommandRunner commands, string source, string dataset, string snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        // A dataset there already is never touched, not even by the clean-up of a failed copy.
        if ((await commands.RunAsync(HomePlans.Exists(dataset), cancellationToken)).ExitCode == 0)
        {
            throw new PithyxException(ErrorCodes.HomesPoolTaken, new Dictionary<string, object?> { ["folder"] = dataset.Split('/')[^1] });
        }
        await RunAsync(commands, HomePlans.SetReadOnly(source, true), cancellationToken);
        try
        {
            await RunAsync(commands, HomePlans.Snapshot(source, snapshot), cancellationToken);
            await RunAsync(commands, HomePlans.SendReceive(source, snapshot, dataset), cancellationToken);
            await RunAsync(commands, HomePlans.Mount(dataset), cancellationToken);
        }
        catch
        {
            await commands.RunAsync(HomePlans.Destroy(dataset), CancellationToken.None);
            await commands.RunAsync(HomePlans.SetReadOnly(source, false), CancellationToken.None);
            await commands.RunAsync(HomePlans.DestroySnapshot(source, snapshot), CancellationToken.None);
            throw;
        }
    }

    private async Task PointAsync(HomeRow row, string path, string? dataset)
    {
        await using var connection = await connections.OpenAsync(CancellationToken.None);
        await connection.ExecuteAsync(
            "UPDATE shares SET path = @path, dataset = @dataset, updated_at = @now WHERE id = @id",
            new { id = row.ShareId, path, dataset, now = Now() });
    }

    /// <summary>
    /// Checks what an administrator chose for the home of a user about to be deleted, before anything is deleted.
    /// </summary>
    public async Task CheckChoiceAsync(string username, HomeDataChoice choice, long? shareId, string? confirm, CancellationToken cancellationToken)
    {
        switch (choice)
        {
            case HomeDataChoice.Move when shareId is null:
            case HomeDataChoice.Delete when confirm is null:
                throw new PithyxException(ErrorCodes.HomesChoiceInvalid);
            case HomeDataChoice.Move:
                await using (var connection = await connections.OpenAsync(cancellationToken))
                {
                    if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM shares WHERE id = @shareId AND home = 0", new { shareId }) == 0)
                    {
                        throw new PithyxException(ErrorCodes.FsShareNotFound);
                    }
                }
                break;
            case HomeDataChoice.Delete when confirm != username:
                throw new PithyxException(ErrorCodes.HomesHomeNameMismatch, new Dictionary<string, object?> { ["name"] = username });
        }
    }

    /// <summary>
    /// After a user was deleted: its home stays under <c>&lt;name&gt;.deleted-&lt;uid&gt;</c>, is deleted, or stays so and
    /// moves into a shared folder as a job, which is returned.
    /// </summary>
    public async Task<Job?> UserDeletedAsync(ClaimsPrincipal caller, long shareId, string username, long uid, HomeDataChoice choice, long? targetShareId, CancellationToken cancellationToken)
    {
        var row = (await homes.RowsAsync(cancellationToken)).FirstOrDefault(candidate => candidate.ShareId == shareId) ?? throw new PithyxException(ErrorCodes.HomesHomeNotFound);
        if (choice == HomeDataChoice.Delete)
        {
            await DestroyAsync(row, cancellationToken);
            return null;
        }
        var kept = await KeepAsync(row, Homes.DeletedFolder(username, uid), cancellationToken);
        return choice == HomeDataChoice.Move ? await StartReleaseAsync(caller, kept.ShareId, new HomeReleaseRequest(targetShareId!.Value), cancellationToken, username) : null;
    }

    /// <summary>
    /// Deletes the home of a deleted user with every file, after its folder name was typed; also a user's home whose
    /// pool is gone, so the user gets a new one on the home pool.
    /// </summary>
    public async Task DeleteAsync(ClaimsPrincipal caller, long shareId, string? confirm, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        var row = (await homes.RowsAsync(cancellationToken)).FirstOrDefault(candidate => candidate.ShareId == shareId) ?? throw new PithyxException(ErrorCodes.HomesHomeNotFound);
        if (row.UserId is not null && !OnMissingPool(row, pools.Current))
        {
            throw new PithyxException(ErrorCodes.HomesHomeInUse);
        }
        if (confirm != row.Folder)
        {
            throw new PithyxException(ErrorCodes.HomesHomeNameMismatch, new Dictionary<string, object?> { ["name"] = row.Folder });
        }
        await DestroyAsync(row, cancellationToken);
        if (row.UserId is not null)
        {
            await homes.ReconcileAsync(cancellationToken);
        }
    }

    private bool OnMissingPool(HomeRow row, PoolList current) =>
        ShareAvailability.Reason(row.Dataset, current, pools.Available) == ShareAvailability.PoolMissing;

    /// <summary>
    /// Moves the files of a deleted user's home into a new folder of a shared folder as a job, then deletes the home.
    /// The home's recycle bin goes with it. The folder is named after the user (<paramref name="name"/>, otherwise
    /// the home's folder), with a number when the name is taken.
    /// </summary>
    public async Task<Job> StartReleaseAsync(ClaimsPrincipal caller, long shareId, HomeReleaseRequest request, CancellationToken cancellationToken, string? name = null)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var row = await OrphanAsync(shareId, cancellationToken);
        var resolved = await shares.ResolveAsync(caller, request.ShareId, write: true, cancellationToken);
        var target = resolved.Kind == ShareKind.Share ? resolved.Id : throw new PithyxException(ErrorCodes.FsShareNotFound);
        var folder = name ?? row.Folder;
        return await jobs.StartAsync(
            CurrentUser.Id(caller),
            ReleaseJobKind,
            new { shareId = row.ShareId, folder = row.Folder, targetShareId = target },
            async context =>
            {
                var created = await CreateFreeFolderAsync(caller, target, folder, context.Cancellation);
                var result = await operations.RunInJobAsync(
                    caller,
                    new FileOperationRequest(FileOperationKind.Move, Target: new FileLocation(target, created), OnConflict: ConflictPolicy.Rename, AllIn: new FolderSelection(new FileLocation(row.ShareId, ""), [])),
                    context);
                await DestroyAsync(row, CancellationToken.None);
                context.SetResult(new { targetShareId = target, folder = created, moved = result.Done });
            },
            cancellationToken);
    }

    private async Task<string> CreateFreeFolderAsync(ClaimsPrincipal caller, long shareId, string name, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? name : $"{name} ({attempt.ToString(CultureInfo.InvariantCulture)})";
            try
            {
                return (await files.CreateFolderAsync(caller, shareId, candidate, cancellationToken)).Name;
            }
            catch (PithyxException exception) when (exception.Code == ErrorCodes.FsEntryExists && attempt < 1000)
            {
                // Taken; the next number.
            }
        }
    }

    private async Task<HomeRow> OrphanAsync(long shareId, CancellationToken cancellationToken)
    {
        var row = (await homes.RowsAsync(cancellationToken)).FirstOrDefault(candidate => candidate.ShareId == shareId) ?? throw new PithyxException(ErrorCodes.HomesHomeNotFound);
        return row.UserId is null ? row : throw new PithyxException(ErrorCodes.HomesHomeInUse);
    }

    /// <summary>Renames a deleted user's home so the user name is free; it stays where it is when the rename fails.</summary>
    private async Task<HomeRow> KeepAsync(HomeRow row, string folder, CancellationToken cancellationToken)
    {
        if (row.Folder == folder)
        {
            return row;
        }
        var parent = Path.GetDirectoryName(row.Path)!;
        var path = Path.Combine(parent, folder);
        string? dataset = null;
        await ReleaseAsync(row.Path, cancellationToken);
        using var held = await homes.LockAsync(cancellationToken);
        if (options.UseZfs && row.Pool is { } pool)
        {
            if ((await commands.RunAsync(HomePlans.Rename(pool, row.Folder, folder), cancellationToken)).ExitCode != 0)
            {
                LogKeepFailed(row.Folder, folder);
                return row;
            }
            dataset = HomePlans.Dataset(pool, folder);
            await state.RefreshPoolsAsync(cancellationToken);
        }
        else
        {
            Directory.Move(row.Path, path);
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                "UPDATE shares SET name = @name, path = @path, dataset = @dataset, home_user_id = NULL, updated_at = @now WHERE id = @id",
                new { id = row.ShareId, name = Homes.RowName(folder), path, dataset, now = Now() });
        }
        shareChanges.Announce();
        return row with { Folder = folder, Path = path, Dataset = dataset, UserId = null };
    }

    /// <summary>Destroys a home with its snapshots and removes its row with its recycle bin rows.</summary>
    private async Task DestroyAsync(HomeRow row, CancellationToken cancellationToken)
    {
        await ReleaseAsync(row.Path, cancellationToken);
        using var held = await homes.LockAsync(cancellationToken);
        if (options.UseZfs && row.Dataset is { } dataset)
        {
            if (pools.Current.Datasets.Any(candidate => candidate.Name == dataset))
            {
                await RunAsync(HomePlans.Destroy(dataset), cancellationToken);
            }
            await state.RefreshPoolsAsync(cancellationToken);
        }
        else if (Directory.Exists(row.Path))
        {
            Directory.Delete(row.Path, recursive: true);
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("DELETE FROM shares WHERE id = @id", new { id = row.ShareId });
        }
        LogDestroyed(row.Folder);
        permissions.Invalidate();
        shareChanges.Announce();
    }

    /// <summary>Lets go of everything that holds a home's folder: the Files watches and the SFTP views.</summary>
    private async Task ReleaseAsync(string path, CancellationToken cancellationToken)
    {
        watches?.ReleaseBelow([path]);
        await views.ReleaseAsync(path, cancellationToken);
    }

    private Task RunAsync(CommandRequest request, CancellationToken cancellationToken) => RunAsync(commands, request, cancellationToken);

    private static async Task RunAsync(ICommandRunner commands, CommandRequest request, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(request, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.StorageCommandFailed, new Dictionary<string, object?> { ["command"] = request.FileName, ["exitCode"] = result.ExitCode });
        }
    }

    private static PithyxException QuotaInvalid() =>
        new(ErrorCodes.HomesQuotaInvalid, new Dictionary<string, object?> { ["minBytes"] = MinimumQuotaBytes });

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved {Count} homes to pool {Pool}")]
    private partial void LogMoved(int count, string pool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The old copy {Dataset} of a moved home could not be destroyed; it stays read-only until an administrator removes it")]
    private partial void LogLeftover(string dataset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The home {Folder} of a deleted user could not be renamed to {NewFolder}; it keeps its name")]
    private partial void LogKeepFailed(string folder, string newFolder);

    [LoggerMessage(Level = LogLevel.Information, Message = "The home {Folder} was deleted")]
    private partial void LogDestroyed(string folder);
}
