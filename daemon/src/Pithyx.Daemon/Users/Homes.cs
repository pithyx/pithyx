using System.Globalization;
using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Users;

/// <param name="UseZfs">
/// Real mode: every home is a dataset below <c>&lt;pool&gt;/pithyx/homes</c>. Simulation: a folder
/// <paramref name="FolderRoot"/><c>/&lt;pool&gt;/homes/&lt;folder&gt;</c>, registered without a dataset.
/// </param>
/// <param name="FolderRoot">The stand-in for the pools' mount points in simulation.</param>
public sealed record HomeStorageOptions(bool UseZfs, string FolderRoot);

/// <summary>A home as stored in <c>shares</c>.</summary>
/// <param name="ShareId">Its row in <c>shares</c>; file calls name it like a shared folder.</param>
/// <param name="Folder">Its folder below <c>homes</c>: the user name, or <c>&lt;name&gt;.deleted-&lt;uid&gt;</c> after its user was deleted.</param>
/// <param name="Path">Where it is mounted.</param>
/// <param name="Dataset">Its dataset; null in simulation.</param>
/// <param name="UserId">Its user, or null when the user was deleted and the home kept.</param>
public sealed record HomeRow(long ShareId, string Folder, string Path, string? Dataset, long? UserId)
{
    /// <summary>The pool the home is on; null in simulation.</summary>
    public string? Pool => Dataset?.Split('/')[0];
}

/// <summary>
/// The homes (D105, D348): every user with a Linux account gets a home on the home pool (setting
/// <see cref="PoolSetting"/>, the first pool that can take it until an administrator picks another), owned by the user
/// with mode 0700, and a row in <c>shares</c> marked as a home, so Files, the recycle bin, the share ACLs and every
/// protocol use it like a shared folder. Users see it as <see cref="OwnName"/>, administrators every home below
/// <see cref="AllName"/>. A home of a deleted user stays with its row until an administrator deletes it or moves its
/// files; homes found below <c>homes</c> without a row (from before D348) get one the same way. A new user never takes
/// over an old home: one that belongs to another uid is renamed to <c>&lt;name&gt;.deleted-&lt;uid&gt;</c> first.
/// </summary>
public sealed partial class Homes(
    SqliteConnectionFactory connections,
    ICommandRunner commands,
    PoolInventory pools,
    SystemStateService state,
    HomeStorageOptions options,
    PermissionService permissions,
    ShareChanges shareChanges,
    TimeProvider time,
    ILogger<Homes> logger) : IDisposable
{
    public const string PoolSetting = "homes.pool";

    /// <summary>The default quota of every home in bytes; missing means none.</summary>
    public const string QuotaSetting = "homes.quota";

    /// <summary>What users see as their own home in Files and every protocol.</summary>
    public const string OwnName = "home";

    /// <summary>What administrators see as the folder of every home.</summary>
    public const string AllName = "homes";

    /// <summary>The names of home rows in <c>shares</c>; a shared folder's name can never start like this.</summary>
    public const string RowPrefix = "home:";

    private readonly SemaphoreSlim _running = new(1, 1);

    public void Dispose() => _running.Dispose();

    /// <summary>
    /// Holds off <see cref="ReconcileAsync"/> while a change renames, moves or removes a home, so it never registers a
    /// half-done change as a home without a row. Never call <see cref="ReconcileAsync"/> while holding it.
    /// </summary>
    public async Task<IDisposable> LockAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        return new Releaser(_running);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }

    public static string RowName(string folder) => RowPrefix + folder;

    /// <summary>The folder a deleted user's home is renamed to, so the user name is free again; uids are never reused.</summary>
    public static string DeletedFolder(string username, long uid) => $"{username}.deleted-{uid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The home pool, or null before the first pool that can take homes.</summary>
    public async Task<string?> PoolAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = PoolSetting });
    }

    /// <summary>The default quota in bytes, or null for none.</summary>
    public async Task<long?> DefaultQuotaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = QuotaSetting });
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 ? bytes : null;
    }

    /// <summary>Why <paramref name="pool"/> cannot take homes now, or null when it can.</summary>
    public string? Unavailable(PoolList current, string pool)
    {
        ArgumentNullException.ThrowIfNull(current);
        var found = current.Pools.FirstOrDefault(candidate => candidate.Name == pool);
        if (found is null)
        {
            return "missing";
        }
        if (found.State is not ("ONLINE" or "DEGRADED"))
        {
            return "offline";
        }
        if (found.ReadOnly)
        {
            return "read_only";
        }
        if (!options.UseZfs)
        {
            return null;
        }
        var homes = current.Datasets.FirstOrDefault(dataset => dataset.Name == HomePlans.HomesDataset(pool));
        if (homes is null)
        {
            return "not_prepared";
        }
        if (homes.KeyStatus is not (null or "available"))
        {
            return "locked";
        }
        return homes is { Mounted: true, MountPoint: not null } ? null : "not_mounted";
    }

    /// <summary>The folder that holds the homes of <paramref name="pool"/> while it is there, otherwise null.</summary>
    public string? RootOf(PoolList current, string pool)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!options.UseZfs)
        {
            return Path.Combine(options.FolderRoot, pool, "homes");
        }
        return current.Datasets.FirstOrDefault(dataset => dataset.Name == HomePlans.HomesDataset(pool)) is { Mounted: true, MountPoint: { } mount } ? mount : null;
    }

    /// <summary>Every home row.</summary>
    public async Task<IReadOnlyList<HomeRow>> RowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await RowsAsync(connection);
    }

    public static async Task<IReadOnlyList<HomeRow>> RowsAsync(System.Data.Common.DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var rows = await connection.QueryAsync<(long Id, string Name, string Path, string? Dataset, long? UserId)>(
            "SELECT id, name, path, dataset, home_user_id FROM shares WHERE home = 1 ORDER BY name");
        return [.. rows.Select(row => new HomeRow(row.Id, row.Name[RowPrefix.Length..], row.Path, row.Dataset, row.UserId))];
    }

    /// <summary>The path of a user's home, or null when it has none yet.</summary>
    public static async Task<string?> PathOfAsync(System.Data.Common.DbConnection connection, long userId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return await connection.ExecuteScalarAsync<string?>("SELECT path FROM shares WHERE home = 1 AND home_user_id = @userId", new { userId });
    }

    /// <summary>
    /// Brings the homes in line with the users: picks the home pool when there is none yet, creates missing homes,
    /// registers homes without a row and sets every quota. Never throws but for cancellation; the next change tries again.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        var changed = false;
        var ranCommand = false;
        try
        {
            var current = pools.Current;
            var pool = await PoolAsync(cancellationToken) ?? await ChoosePoolAsync(current, cancellationToken);
            if (pool is null || Unavailable(current, pool) is not null || RootOf(current, pool) is not { } root)
            {
                return;
            }
            await using var connection = await connections.OpenAsync(cancellationToken);
            var users = (await connection.QueryAsync<(long Id, string Username, long Uid, long? Quota)>(
                "SELECT id, username, uid, home_quota_bytes FROM users WHERE uid IS NOT NULL ORDER BY uid")).ToList();
            var rows = (await RowsAsync(connection)).ToList();
            if (!options.UseZfs)
            {
                Directory.CreateDirectory(root);
            }
            foreach (var user in users.Where(user => rows.All(row => row.UserId != user.Id)))
            {
                var path = Path.Combine(root, user.Username);
                // The folder of an old home with this name: renamed out of the way unless it is this user's. Without root
                // (simulation) nothing has a user's owner, so nothing is renamed.
                if (Files.LinuxFs.IsRoot && Directory.Exists(path) && OwnerOf(root, user.Username) is { } owner && owner != user.Uid)
                {
                    var old = rows.FirstOrDefault(row => row.Path == path);
                    var folder = DeletedFolder(user.Username, owner);
                    if (!await MoveAsideAsync(connection, pool, root, old, user.Username, folder, cancellationToken))
                    {
                        continue;
                    }
                    ranCommand = true;
                    changed = true;
                    rows = [.. await RowsAsync(connection)];
                }
                if (!Directory.Exists(path))
                {
                    if (!await CreateAsync(pool, root, user.Username, user.Uid, cancellationToken))
                    {
                        continue;
                    }
                    ranCommand |= options.UseZfs;
                }
                var existing = rows.FirstOrDefault(row => row.Path == path);
                if (existing is not null)
                {
                    await connection.ExecuteAsync("UPDATE shares SET home_user_id = @userId, updated_at = @now WHERE id = @id", new { userId = user.Id, id = existing.ShareId, now = Now() });
                }
                else
                {
                    await InsertAsync(connection, user.Username, path, options.UseZfs ? HomePlans.Dataset(pool, user.Username) : null, user.Id);
                }
                LogCreated(user.Username, pool);
                changed = true;
                rows = [.. await RowsAsync(connection)];
            }
            foreach (var folder in Folders(current, pool, root).Where(folder => rows.All(row => row.Path != Path.Combine(root, folder))))
            {
                await InsertAsync(connection, folder, Path.Combine(root, folder), options.UseZfs ? HomePlans.Dataset(pool, folder) : null, null);
                LogKept(folder, pool);
                changed = true;
            }
            if (options.UseZfs)
            {
                ranCommand |= await QuotasAsync(connection, current, users, await DefaultQuotaAsync(cancellationToken), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(exception);
        }
        finally
        {
            _running.Release();
        }
        if (ranCommand)
        {
            await state.RefreshPoolsAsync(cancellationToken);
        }
        if (changed)
        {
            permissions.Invalidate();
            shareChanges.Announce();
        }
    }

    /// <summary>The first pool that can take homes, stored as the home pool, or null while there is none.</summary>
    private async Task<string?> ChoosePoolAsync(PoolList current, CancellationToken cancellationToken)
    {
        var pool = current.Pools.Select(candidate => candidate.Name).Order(StringComparer.Ordinal).FirstOrDefault(name => Unavailable(current, name) is null);
        if (pool is not null)
        {
            await SetPoolAsync(pool, cancellationToken);
        }
        return pool;
    }

    public async Task SetPoolAsync(string pool, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @pool) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { key = PoolSetting, pool });
    }

    /// <summary>The folders below the pool's homes: child datasets in real mode, folders in simulation.</summary>
    private List<string> Folders(PoolList current, string pool, string root)
    {
        if (!options.UseZfs)
        {
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Where(HomePlans.IsFolder).Order(StringComparer.Ordinal).ToList() : [];
        }
        var prefix = HomePlans.HomesDataset(pool) + "/";
        return current.Datasets
            .Where(dataset => dataset.Name.StartsWith(prefix, StringComparison.Ordinal) && dataset.Type == "filesystem" && dataset is { Mounted: true })
            .Select(dataset => dataset.Name[prefix.Length..])
            .Where(folder => !folder.Contains('/', StringComparison.Ordinal) && HomePlans.IsFolder(folder))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<bool> CreateAsync(string pool, string root, string username, long uid, CancellationToken cancellationToken)
    {
        if (options.UseZfs)
        {
            var result = await commands.RunAsync(HomePlans.Create(pool, username), cancellationToken);
            if (result.ExitCode != 0 || !Directory.Exists(Path.Combine(root, username)))
            {
                LogCreateFailed(username, pool);
                return false;
            }
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(root, username));
        }
        if (Files.LinuxFs.IsRoot)
        {
            using var parent = Files.LinuxFs.OpenRoot(root);
            Files.LinuxFs.ChangeOwnerAt(parent, username, (uint)uid, Files.FileOwner.UsersGroup);
        }
        // The daemon runs on Linux only; the analyzer cannot know that.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(Path.Combine(root, username), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return true;
    }

    /// <summary>Renames an old home that belongs to another uid, so the user name gets a new, empty home.</summary>
    private async Task<bool> MoveAsideAsync(System.Data.Common.DbConnection connection, string pool, string root, HomeRow? row, string folder, string newFolder, CancellationToken cancellationToken)
    {
        if (Directory.Exists(Path.Combine(root, newFolder)))
        {
            LogMoveAsideFailed(folder, newFolder);
            return false;
        }
        if (options.UseZfs)
        {
            if ((await commands.RunAsync(HomePlans.Rename(pool, folder, newFolder), cancellationToken)).ExitCode != 0)
            {
                LogMoveAsideFailed(folder, newFolder);
                return false;
            }
        }
        else
        {
            Directory.Move(Path.Combine(root, folder), Path.Combine(root, newFolder));
        }
        var path = Path.Combine(root, newFolder);
        var dataset = options.UseZfs ? HomePlans.Dataset(pool, newFolder) : null;
        if (row is null)
        {
            await InsertAsync(connection, newFolder, path, dataset, null);
        }
        else
        {
            await connection.ExecuteAsync(
                "UPDATE shares SET name = @name, path = @path, dataset = @dataset, home_user_id = NULL, updated_at = @now WHERE id = @id",
                new { id = row.ShareId, name = RowName(newFolder), path, dataset, now = Now() });
        }
        LogMovedAside(folder, newFolder);
        return true;
    }

    private async Task InsertAsync(System.Data.Common.DbConnection connection, string folder, string path, string? dataset, long? userId) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO shares (name, path, dataset, home, home_user_id, created_at, updated_at)
            VALUES (@name, @path, @dataset, 1, @userId, @now, @now)
            """,
            new { name = RowName(folder), path, dataset, userId, now = Now() });

    /// <summary>Sets the quota of every user's home that differs; returns whether a command ran.</summary>
    private async Task<bool> QuotasAsync(
        System.Data.Common.DbConnection connection,
        PoolList current,
        IReadOnlyList<(long Id, string Username, long Uid, long? Quota)> users,
        long? defaultQuota,
        CancellationToken cancellationToken)
    {
        var ran = false;
        var datasets = current.Datasets.ToDictionary(dataset => dataset.Name, StringComparer.Ordinal);
        foreach (var row in (await RowsAsync(connection)).Where(row => row is { UserId: not null, Dataset: not null }))
        {
            var user = users.FirstOrDefault(candidate => candidate.Id == row.UserId);
            if (user.Username is null || !datasets.TryGetValue(row.Dataset!, out var dataset))
            {
                continue;
            }
            var wanted = WantedQuota(user.Quota, defaultQuota);
            if (dataset.QuotaBytes != wanted)
            {
                ran = true;
                if ((await commands.RunAsync(HomePlans.SetQuota(row.Dataset!, wanted), cancellationToken)).ExitCode != 0)
                {
                    LogQuotaFailed(row.Folder);
                }
            }
        }
        return ran;
    }

    /// <summary>The quota a home should have: the user's own (0 for none), otherwise the default.</summary>
    public static long? WantedQuota(long? own, long? defaultQuota) => own switch
    {
        0 => null,
        { } bytes => bytes,
        null => defaultQuota,
    };

    private static long? OwnerOf(string root, string folder)
    {
        try
        {
            using var parent = Files.LinuxFs.OpenRoot(root);
            return Files.LinuxFs.Stat(parent, folder).Uid;
        }
        catch (Files.FsError)
        {
            return null;
        }
    }

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "The home of {Name} is on pool {Pool}")]
    private partial void LogCreated(string name, string pool);

    [LoggerMessage(Level = LogLevel.Information, Message = "The home {Folder} on pool {Pool} has no user; it stays until an administrator deletes it")]
    private partial void LogKept(string folder, string pool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create the home of {Name} on pool {Pool}")]
    private partial void LogCreateFailed(string name, string pool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The home {Folder} belongs to another uid and was renamed to {NewFolder}")]
    private partial void LogMovedAside(string folder, string newFolder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The home {Folder} belongs to another uid and could not be renamed to {NewFolder}; its user gets no home until it is")]
    private partial void LogMoveAsideFailed(string folder, string newFolder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setting the quota of the home {Folder} failed")]
    private partial void LogQuotaFailed(string folder);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bringing the homes in line failed")]
    private partial void LogFailed(Exception exception);
}
