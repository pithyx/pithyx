using System.Security.Claims;
using Dapper;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.Files;

/// <summary>The Linux owner of what the file service creates for a user: the user's uid and the group <c>users</c>.</summary>
public sealed record FileOwner(uint Uid, uint Gid)
{
    /// <summary>Debian's group <c>users</c>, the primary group of every Pithyx account.</summary>
    public const uint UsersGroup = 100;

    public void Apply(SafeFileHandle file) => LinuxFs.ChangeOwner(file, Uid, Gid);

    public void ApplyAt(SafeFileHandle folder, string name) => LinuxFs.ChangeOwnerAt(folder, name, Uid, Gid);
}

/// <summary>
/// Finds the owner for the requesting user (release blocker D89): files and folders the daemon creates belong to the
/// user's Linux account, so SMB, NFS and SFTP see the right owner. Null when the daemon does not run as root
/// (simulation, tests, development without root) or the user has no account yet; the files then stay the daemon's.
/// </summary>
public sealed class FileOwners(SqliteConnectionFactory connections)
{
    private readonly bool _root = LinuxFs.IsRoot;

    public async Task<FileOwner?> ForAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!_root)
        {
            return null;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        var uid = await connection.ExecuteScalarAsync<long?>("SELECT uid FROM users WHERE id = @id", new { id = CurrentUser.Id(user) });
        return uid is { } value ? new FileOwner((uint)value, FileOwner.UsersGroup) : null;
    }
}
