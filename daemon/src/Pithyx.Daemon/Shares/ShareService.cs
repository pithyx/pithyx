using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Shares;

/// <summary>
/// Shares and who may use them. Every file request resolves its share here, so a changed or revoked access applies to
/// the next request. A share the caller cannot use is reported as not found, so share names do not leak.
/// </summary>
public sealed class ShareService(SqliteConnectionFactory connections, TimeProvider time, PoolInventory? pools = null, Permissions.PermissionService? permissions = null)
{
    private readonly Permissions.PermissionService _permissions = permissions ?? new Permissions.PermissionService(connections);

    public const int MaxNameLength = 64;

    public const int MaxDescriptionLength = 200;

    // System locations that never become a share, and everything below them.
    private static readonly string[] _forbiddenRoots =
        ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/root", "/run", "/sbin", "/sys", "/usr", "/var"];

    /// <summary>
    /// Whether the user may read a share, for checks that run per realtime event and must not wait for the database:
    /// the resolved rights stay in memory until a right changes.
    /// </summary>
    public bool CanRead(ClaimsPrincipal user, long shareId) => _permissions.ForUser(user).Share(shareId) != ShareAccess.None;

    public static bool IsAdmin(ClaimsPrincipal user) => user?.IsInRole(SessionAuthenticationHandler.AdminRole) == true;

    public async Task<IReadOnlyList<ShareInfo>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (IsAdmin(user))
        {
            var all = await connection.QueryAsync<(long Id, string Name, string Path, string? Dataset)>("SELECT id, name, path, dataset FROM shares ORDER BY name");
            return [.. all.Select(share => new ShareInfo(share.Id, share.Name, ShareAccess.ReadWrite, share.Path, UnavailableReason(share.Dataset)))];
        }
        var rights = await _permissions.ForUserAsync(CurrentUser.Id(user), cancellationToken);
        var rows = await connection.QueryAsync<(long Id, string Name, string? Dataset)>("SELECT id, name, dataset FROM shares ORDER BY name");
        return [.. rows
            .Where(share => rights.Share(share.Id) != ShareAccess.None)
            .Select(share => new ShareInfo(share.Id, share.Name, rights.Share(share.Id), null, UnavailableReason(share.Dataset)))];
    }

    /// <summary>
    /// The share with the caller's access. Throws <see cref="ErrorCodes.FsShareNotFound"/> without access and
    /// <see cref="ErrorCodes.FsShareReadOnly"/> when <paramref name="write"/> is asked with read access only.
    /// </summary>
    public async Task<ResolvedShare> ResolveAsync(ClaimsPrincipal user, long shareId, bool write, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(long Id, string Name, string Path, string? Dataset)?>(
            "SELECT id, name, path, dataset FROM shares WHERE id = @shareId",
            new { shareId });
        var access = row is null ? ShareAccess.None : (await _permissions.ForUserAsync(CurrentUser.Id(user), cancellationToken)).Share(shareId);
        if (row is null || access == ShareAccess.None)
        {
            throw new PithyxException(ErrorCodes.FsShareNotFound);
        }
        if (write && access != ShareAccess.ReadWrite)
        {
            throw new PithyxException(ErrorCodes.FsShareReadOnly);
        }
        if (UnavailableReason(row.Value.Dataset) is { } reason)
        {
            throw new PithyxException(ErrorCodes.FsShareUnavailable, new Dictionary<string, object?> { ["name"] = row.Value.Name, ["reason"] = reason });
        }
        return new ResolvedShare(row.Value.Id, row.Value.Name, row.Value.Path, access);
    }

    private string? UnavailableReason(string? dataset) =>
        pools is null ? null : ShareAvailability.Reason(dataset, pools.Current, pools.Available);

    public async Task<ShareInfo> CreateAsync(ClaimsPrincipal user, NewShare share, CancellationToken cancellationToken)
    {
        RequireAdmin(user);
        ArgumentNullException.ThrowIfNull(share);
        var name = CheckName(share.Name);
        var path = CheckPath(share.Path);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var existing = await connection.QueryAsync<(string Name, string Path)>("SELECT name, path FROM shares");
        foreach (var other in existing)
        {
            if (string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new PithyxException(ErrorCodes.FsShareExists);
            }
            if (Overlaps(other.Path, path))
            {
                throw new PithyxException(ErrorCodes.FsShareInvalid);
            }
        }
        var now = SessionService.Format(time.GetUtcNow());
        var id = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO shares (name, path, created_at, updated_at) VALUES (@name, @path, @now, @now) RETURNING id",
            new { name, path, now });
        return new ShareInfo(id, name, ShareAccess.ReadWrite, path);
    }

    /// <summary>Every share with its dataset's space and a summary of the rights, for the Shared folders page.</summary>
    public async Task<IReadOnlyList<ShareDetails>> ListDetailsAsync(ClaimsPrincipal user, PoolList pools, CancellationToken cancellationToken)
    {
        RequireAdmin(user);
        ArgumentNullException.ThrowIfNull(pools);
        await using var connection = await connections.OpenAsync(cancellationToken);
        // Administrators use every share anyway, so the summary counts the others who are active.
        var others = (await Permissions.PermissionService.AllUsersAsync(connection, null)).Where(other => !other.Disabled && !other.Permissions.IsAdmin).ToList();
        var rows = await connection.QueryAsync<(long Id, string Name, string Description, string Path, string? Dataset, long? RestorePoints, long Smb, long Sftp, long SmbEncrypt)>(
            """
            SELECT shares.id, shares.name, shares.description, shares.path, shares.dataset,
                (SELECT enabled FROM snapshot_schedules WHERE snapshot_schedules.dataset = shares.dataset AND kind = 'daily'),
                shares.smb, shares.sftp, shares.smb_encrypt
            FROM shares ORDER BY shares.name
            """);
        var datasets = pools.Datasets.ToDictionary(dataset => dataset.Name, StringComparer.Ordinal);
        return [.. rows.Select(row =>
        {
            var dataset = row.Dataset is { } name && datasets.TryGetValue(name, out var found) ? found : null;
            return new ShareDetails(
                row.Id,
                row.Name,
                row.Description,
                row.Path,
                row.Dataset?.Split('/')[0],
                row.Dataset,
                dataset?.UsedBytes,
                dataset?.AvailableBytes,
                dataset?.QuotaBytes,
                others.Count(other => other.Permissions.Share(row.Id) == ShareAccess.Read),
                others.Count(other => other.Permissions.Share(row.Id) == ShareAccess.ReadWrite),
                row.Dataset is null ? null : row.RestorePoints == 1,
                row.Smb == 1,
                row.Sftp == 1,
                row.SmbEncrypt == 1);
        })];
    }

    /// <summary>Changes the name shown, the description and the protocols. The folder and the dataset stay where they are.</summary>
    public async Task UpdateAsync(ClaimsPrincipal user, long shareId, ShareUpdate update, CancellationToken cancellationToken)
    {
        RequireAdmin(user);
        ArgumentNullException.ThrowIfNull(update);
        var name = update.Name is null ? null : CheckName(update.Name);
        var description = update.Description is null ? null : CheckDescription(update.Description);
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await FindAsync(connection, shareId) is null)
        {
            throw new PithyxException(ErrorCodes.FsShareNotFound);
        }
        if (name is not null && FileServices.SmbConfig.ReservedNames.Contains(name))
        {
            throw new PithyxException(ErrorCodes.FsShareNameReserved, new Dictionary<string, object?> { ["name"] = name });
        }
        if (name is not null && await NameTakenAsync(connection, name, shareId))
        {
            throw new PithyxException(ErrorCodes.FsShareExists);
        }
        await connection.ExecuteAsync(
            """
            UPDATE shares SET name = COALESCE(@name, name), description = COALESCE(@description, description),
                smb = COALESCE(@smb, smb), sftp = COALESCE(@sftp, sftp), smb_encrypt = COALESCE(@smbEncrypt, smb_encrypt), updated_at = @now
            WHERE id = @shareId
            """,
            new { shareId, name, description, smb = update.Smb, sftp = update.Sftp, smbEncrypt = update.SmbEncrypt, now = SessionService.Format(time.GetUtcNow()) });
    }

    /// <summary>The share as stored, or null. Not for callers with a user: it checks no access.</summary>
    public async Task<ShareRecord?> FindAsync(long shareId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await FindAsync(connection, shareId);
    }

    /// <summary>Whether another share has this name, compared like the database does (case-insensitive).</summary>
    public async Task<bool> NameTakenAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await NameTakenAsync(connection, name, null);
    }

    /// <summary>
    /// Registers the share of a dataset a plan just created, mounted at <paramref name="path"/>. Nobody but
    /// administrators has access until it is granted.
    /// </summary>
    public async Task<long> RegisterDatasetAsync(string name, string description, string dataset, string path, CancellationToken cancellationToken)
    {
        name = CheckName(name);
        description = CheckDescription(description);
        ArgumentException.ThrowIfNullOrEmpty(dataset);
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await NameTakenAsync(connection, name, null))
        {
            throw new PithyxException(ErrorCodes.FsShareExists);
        }
        if ((await connection.QueryAsync<string>("SELECT path FROM shares")).Any(other => Overlaps(other, path)))
        {
            throw new PithyxException(ErrorCodes.FsSharePathTaken, new Dictionary<string, object?> { ["path"] = path });
        }
        var now = SessionService.Format(time.GetUtcNow());
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO shares (name, path, description, dataset, created_at, updated_at)
            VALUES (@name, @path, @description, @dataset, @now, @now) RETURNING id
            """,
            new { name, path, description, dataset, now });
    }

    /// <summary>
    /// Removes the share from Pithyx with its access and recycle bin rows. The folder or dataset is left as it is; a
    /// plan destroys a dataset before. True when the share was there.
    /// </summary>
    public async Task<bool> UnregisterAsync(long shareId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var removed = await connection.ExecuteAsync("DELETE FROM shares WHERE id = @shareId", new { shareId }) > 0;
        // Its grants went with it.
        _permissions.Invalidate();
        return removed;
    }

    /// <summary>Simulation and development: one share "Shared" in the data directory, so the file manager has files to show.</summary>
    public async Task SeedSimulationAsync(string directory, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM shares") > 0)
        {
            return;
        }
        var path = Path.Combine(directory, "shared");
        Directory.CreateDirectory(path);
        var now = SessionService.Format(time.GetUtcNow());
        await connection.ExecuteAsync(
            "INSERT INTO shares (name, path, created_at, updated_at) VALUES ('Shared', @path, @now, @now)",
            new { path = Canonical(path), now });
    }

    /// <summary>Every share's id, name and folder, for the storage planner's checks. Not for callers with a user.</summary>
    public async Task<IReadOnlyList<(long Id, string Name, string Path)>> ListPathsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<(long Id, string Name, string Path)>("SELECT id, name, path FROM shares ORDER BY name")];
    }

    /// <summary>
    /// After a dataset was renamed: every share whose dataset is <paramref name="oldName"/> or below it gets the same
    /// name below <paramref name="newName"/>. Returns how many moved.
    /// </summary>
    public async Task<int> MoveDatasetsAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (oldName == newName)
        {
            return 0;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(
            """
            UPDATE shares SET dataset = @newName || substr(dataset, length(@oldName) + 1), updated_at = @now
            WHERE dataset = @oldName OR substr(dataset, 1, length(@oldName) + 1) = @oldName || '/'
            """,
            new { oldName, newName, now = SessionService.Format(time.GetUtcNow()) });
    }

    /// <summary>
    /// After a dataset was renamed: every share whose folder is <paramref name="oldRoot"/> or below it moves to the same
    /// place below <paramref name="newRoot"/>. Returns how many moved.
    /// </summary>
    public async Task<int> MovePathsAsync(string oldRoot, string newRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldRoot);
        ArgumentException.ThrowIfNullOrEmpty(newRoot);
        oldRoot = oldRoot.TrimEnd('/');
        newRoot = newRoot.TrimEnd('/');
        if (oldRoot.Length == 0 || newRoot.Length == 0 || oldRoot == newRoot)
        {
            return 0;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        // substr instead of LIKE, so _ and % in folder names are not patterns.
        var moved = await connection.ExecuteAsync(
            """
            UPDATE shares SET path = @newRoot || substr(path, length(@oldRoot) + 1), updated_at = @now
            WHERE path = @oldRoot OR substr(path, 1, length(@oldRoot) + 1) = @oldRoot || '/'
            """,
            new { oldRoot, newRoot, now = SessionService.Format(time.GetUtcNow()) });
        return moved;
    }

    /// <summary>Names users see: letters, digits, spaces, dots, dashes and underscores, not starting with a dot.</summary>
    public static string CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        var valid = trimmed.Length is > 0 and <= MaxNameLength
            && trimmed[0] != '.'
            && trimmed.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_');
        return valid ? trimmed : throw new PithyxException(ErrorCodes.FsShareInvalid);
    }

    /// <summary>A description: trimmed, at most <see cref="MaxDescriptionLength"/> characters, one line.</summary>
    public static string CheckDescription(string? description)
    {
        var trimmed = description?.Trim() ?? "";
        return trimmed.Length <= MaxDescriptionLength && !trimmed.Any(char.IsControl)
            ? trimmed
            : throw new PithyxException(ErrorCodes.FsShareDescriptionInvalid, new Dictionary<string, object?> { ["max"] = MaxDescriptionLength });
    }

    /// <summary>An existing folder, given as its canonical absolute path, outside the system directories.</summary>
    public static string CheckPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path == "/" || !Directory.Exists(path))
        {
            throw new PithyxException(ErrorCodes.FsShareInvalid);
        }
        var canonical = Canonical(path);
        if (canonical != path.TrimEnd('/') || IsForbidden(canonical))
        {
            throw new PithyxException(ErrorCodes.FsShareInvalid);
        }
        return canonical;
    }

    public static bool IsForbidden(string canonical) =>
        canonical == "/" || _forbiddenRoots.Any(root => FsPath.IsSameOrInside(canonical, root));

    public static bool Overlaps(string a, string b) => FsPath.IsSameOrInside(a, b) || FsPath.IsSameOrInside(b, a);

    // The kernel's view of the folder: every symlink resolved.
    private static string Canonical(string path)
    {
        using var handle = LinuxFs.OpenRoot(path);
        return new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle()}").LinkTarget ?? path;
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!IsAdmin(user))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }

    private static async Task<ShareRecord?> FindAsync(System.Data.Common.DbConnection connection, long shareId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(long Id, string Name, string Path, string Description, string? Dataset)?>(
            "SELECT id, name, path, description, dataset FROM shares WHERE id = @shareId",
            new { shareId });
        return row is { } found ? new ShareRecord(found.Id, found.Name, found.Path, found.Description, found.Dataset) : null;
    }

    private static async Task<bool> NameTakenAsync(System.Data.Common.DbConnection connection, string name, long? exceptId) =>
        await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM shares WHERE name = @name COLLATE NOCASE AND (@exceptId IS NULL OR id <> @exceptId)",
            new { name, exceptId }) > 0;
}
