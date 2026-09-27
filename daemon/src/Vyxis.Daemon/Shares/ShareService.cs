using System.Collections.Concurrent;
using System.Security.Claims;
using Dapper;
using Vyxis.Daemon.Auth;
using Vyxis.Daemon.Database;
using Vyxis.Daemon.Errors;
using Vyxis.Daemon.Files;

namespace Vyxis.Daemon.Shares;

/// <summary>
/// Shares and who may use them. Every file request resolves its share here, so a changed or revoked access applies to
/// the next request. A share the caller cannot use is reported as not found, so share names do not leak.
/// </summary>
public sealed class ShareService(SqliteConnectionFactory connections, TimeProvider time)
{
    public const int MaxNameLength = 64;

    // System locations that never become a share, and everything below them.
    private static readonly string[] _forbiddenRoots =
        ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/root", "/run", "/sbin", "/sys", "/usr", "/var"];

    /// <summary>How long a read check of <see cref="CanRead"/> is reused; access changes clear it at once.</summary>
    public static readonly TimeSpan ReadCheckLifetime = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<(long User, long Share), (bool Allowed, DateTimeOffset Until)> _readChecks = new();

    /// <summary>
    /// Whether the user may read a share, for checks that run per realtime event and must not wait for the database.
    /// </summary>
    public bool CanRead(ClaimsPrincipal user, long shareId)
    {
        if (IsAdmin(user))
        {
            return true;
        }
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }
        var userId = CurrentUser.Id(user);
        var now = time.GetUtcNow();
        if (_readChecks.TryGetValue((userId, shareId), out var check) && check.Until > now)
        {
            return check.Allowed;
        }
        using var connection = connections.Open();
        var allowed = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM share_access WHERE share_id = @shareId AND user_id = @userId",
            new { shareId, userId }) > 0;
        _readChecks[(userId, shareId)] = (allowed, now + ReadCheckLifetime);
        return allowed;
    }

    public static bool IsAdmin(ClaimsPrincipal user) => user?.IsInRole(SessionAuthenticationHandler.AdminRole) == true;

    public async Task<IReadOnlyList<ShareInfo>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (IsAdmin(user))
        {
            var all = await connection.QueryAsync<(long Id, string Name, string Path)>("SELECT id, name, path FROM shares ORDER BY name");
            return [.. all.Select(share => new ShareInfo(share.Id, share.Name, ShareAccess.ReadWrite, share.Path))];
        }
        var rows = await connection.QueryAsync<(long Id, string Name, string Level)>(
            "SELECT shares.id, shares.name, share_access.level FROM shares JOIN share_access ON share_access.share_id = shares.id WHERE share_access.user_id = @userId ORDER BY shares.name",
            new { userId = CurrentUser.Id(user) });
        return [.. rows.Select(share => new ShareInfo(share.Id, share.Name, Parse(share.Level), null))];
    }

    /// <summary>
    /// The share with the caller's access. Throws <see cref="ErrorCodes.FsShareNotFound"/> without access and
    /// <see cref="ErrorCodes.FsShareReadOnly"/> when <paramref name="write"/> is asked with read access only.
    /// </summary>
    public async Task<ResolvedShare> ResolveAsync(ClaimsPrincipal user, long shareId, bool write, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(long Id, string Name, string Path, string? Level)?>(
            """
            SELECT shares.id, shares.name, shares.path, share_access.level
            FROM shares LEFT JOIN share_access ON share_access.share_id = shares.id AND share_access.user_id = @userId
            WHERE shares.id = @shareId
            """,
            new { shareId, userId = CurrentUser.Id(user) });
        var access = row is null ? ShareAccess.None : IsAdmin(user) ? ShareAccess.ReadWrite : row.Value.Level is { } level ? Parse(level) : ShareAccess.None;
        if (row is null || access == ShareAccess.None)
        {
            throw new VyxisException(ErrorCodes.FsShareNotFound);
        }
        if (write && access != ShareAccess.ReadWrite)
        {
            throw new VyxisException(ErrorCodes.FsShareReadOnly);
        }
        return new ResolvedShare(row.Value.Id, row.Value.Name, row.Value.Path, access);
    }

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
                throw new VyxisException(ErrorCodes.FsShareExists);
            }
            if (Overlaps(other.Path, path))
            {
                throw new VyxisException(ErrorCodes.FsShareInvalid);
            }
        }
        var now = SessionService.Format(time.GetUtcNow());
        var id = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO shares (name, path, created_at, updated_at) VALUES (@name, @path, @now, @now) RETURNING id",
            new { name, path, now });
        return new ShareInfo(id, name, ShareAccess.ReadWrite, path);
    }

    /// <summary>Removes the share from Vyxis. Its files stay where they are.</summary>
    public async Task DeleteAsync(ClaimsPrincipal user, long shareId, CancellationToken cancellationToken)
    {
        RequireAdmin(user);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM shares WHERE id = @shareId", new { shareId });
        _readChecks.Clear();
    }

    public async Task SetAccessAsync(ClaimsPrincipal user, long shareId, long userId, ShareAccess access, CancellationToken cancellationToken)
    {
        RequireAdmin(user);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<long>(
            "SELECT (SELECT COUNT(*) FROM shares WHERE id = @shareId) * (SELECT COUNT(*) FROM users WHERE id = @userId)",
            new { shareId, userId });
        if (exists == 0)
        {
            throw new VyxisException(ErrorCodes.FsShareNotFound);
        }
        _readChecks.Clear();
        if (access == ShareAccess.None)
        {
            await connection.ExecuteAsync("DELETE FROM share_access WHERE share_id = @shareId AND user_id = @userId", new { shareId, userId });
            return;
        }
        await connection.ExecuteAsync(
            """
            INSERT INTO share_access (share_id, user_id, level) VALUES (@shareId, @userId, @level)
            ON CONFLICT (share_id, user_id) DO UPDATE SET level = excluded.level
            """,
            new { shareId, userId, level = access == ShareAccess.Read ? "read" : "readWrite" });
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

    /// <summary>Names users see: letters, digits, spaces, dots, dashes and underscores, not starting with a dot.</summary>
    public static string CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        var valid = trimmed.Length is > 0 and <= MaxNameLength
            && trimmed[0] != '.'
            && trimmed.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_');
        return valid ? trimmed : throw new VyxisException(ErrorCodes.FsShareInvalid);
    }

    /// <summary>An existing folder, given as its canonical absolute path, outside the system directories.</summary>
    public static string CheckPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path == "/" || !Directory.Exists(path))
        {
            throw new VyxisException(ErrorCodes.FsShareInvalid);
        }
        var canonical = Canonical(path);
        if (canonical != path.TrimEnd('/') || IsForbidden(canonical))
        {
            throw new VyxisException(ErrorCodes.FsShareInvalid);
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
            throw new VyxisException(ErrorCodes.AuthPermissionDenied);
        }
    }

    private static ShareAccess Parse(string level) => level == "readWrite" ? ShareAccess.ReadWrite : ShareAccess.Read;
}
