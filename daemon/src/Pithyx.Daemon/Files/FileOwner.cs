using System.Security.Claims;
using Dapper;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.Files;

/// <summary>
/// The Linux owner of what the file service creates for a user: the user's uid and the group <c>users</c>; in Apps
/// (D409, <paramref name="FromFolder"/>) the owner of the folder it is created in, the app, as Samba's
/// <c>inherit owner</c> does there.
/// </summary>
public sealed record FileOwner(uint Uid, uint Gid, bool FromFolder = false)
{
    /// <summary>Debian's group <c>users</c>, the primary group of every Pithyx account.</summary>
    public const uint UsersGroup = 100;

    /// <summary>What is created belongs to the owner of its folder.</summary>
    public static FileOwner OfFolder { get; } = new(0, 0, FromFolder: true);

    /// <summary>Gives a new file its owner; <paramref name="folder"/>, the folder it is in, is needed in Apps.</summary>
    public void Apply(SafeFileHandle file, SafeFileHandle? folder = null)
    {
        var (uid, gid) = For(folder);
        LinuxFs.ChangeOwner(file, uid, gid);
    }

    public void ApplyAt(SafeFileHandle folder, string name)
    {
        var (uid, gid) = For(folder);
        LinuxFs.ChangeOwnerAt(folder, name, uid, gid);
    }

    private (uint Uid, uint Gid) For(SafeFileHandle? folder)
    {
        if (!FromFolder)
        {
            return (Uid, Gid);
        }
        ArgumentNullException.ThrowIfNull(folder);
        var stat = LinuxFs.Stat(folder, "");
        return (stat.Uid, stat.Gid);
    }
}

/// <summary>
/// Finds the owner for the requesting user (release blocker D89): files and folders the daemon creates belong to the
/// user's Linux account, so SMB, NFS and SFTP see the right owner. Null when the daemon does not run as root
/// (simulation, tests, development without root) or the user has no account yet; the files then stay the daemon's.
/// </summary>
public sealed class FileOwners(SqliteConnectionFactory connections)
{
    private readonly bool _root = LinuxFs.IsRoot;

    /// <param name="user">The user the request is for.</param>
    /// <param name="target">The share things are created in; in Apps they belong to their folder's owner.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<FileOwner?> ForAsync(ClaimsPrincipal user, Shares.ResolvedShare? target, CancellationToken cancellationToken)
    {
        if (!_root)
        {
            return null;
        }
        if (target?.Kind == Shares.ShareKind.Apps)
        {
            return FileOwner.OfFolder;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        var uid = await connection.ExecuteScalarAsync<long?>("SELECT uid FROM users WHERE id = @id", new { id = CurrentUser.Id(user) });
        return uid is { } value ? new FileOwner((uint)value, FileOwner.UsersGroup) : null;
    }
}
