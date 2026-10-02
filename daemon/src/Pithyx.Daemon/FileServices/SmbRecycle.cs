using Dapper;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.FileServices;

/// <summary>
/// One recycle bin for every protocol (D287). Samba's <c>vfs_recycle</c> moves a file deleted over SMB to
/// <c>.pithyx-trash/.smb/&lt;user&gt;/&lt;path&gt;</c> in the deleting user's name; this turns every such file into an
/// item of the share's recycle bin with a row (the path it had, who deleted it), exactly like a delete in Files, so
/// Files lists and restores it. It runs when the audit log reports a delete over SMB and once at start.
/// <para>
/// The bin folder must let Samba in: <c>.pithyx-trash</c> is root's with mode 0711 (users pass through but cannot
/// list it) and <c>.smb</c> has mode 1733 (users create their own folder, sticky like <c>/tmp</c>), both without the
/// share's ACL. If Samba cannot create its folder, it deletes the file for good, so <see cref="Prepare"/> runs for
/// every share before SMB offers it. Everything works on handles below the share root and never follows a symlink a
/// user put there.
/// </para>
/// </summary>
public sealed partial class SmbRecycle(SqliteConnectionFactory connections, TrashService trash, ILogger<SmbRecycle> logger) : IDisposable
{
    private const uint BinMode = 0x1c9; // 0711
    private const uint IncomingMode = 0x3db; // 1733

    private readonly SemaphoreSlim _running = new(1, 1);

    public void Dispose() => _running.Dispose();

    /// <summary>Creates the bin folders of a share with the modes Samba needs and removes the ACLs the share's ACL walk gave them.</summary>
    public void Prepare(string sharePath)
    {
        using var root = LinuxFs.OpenRoot(sharePath);
        using (var bin = TrashService.OpenFolder(root))
        {
            try
            {
                LinuxFs.MakeDirectory(bin, SmbConfig.RecycleFolder, 0x1c0);
            }
            catch (FsError error) when (error.Errno == LinuxFs.EEXIST)
            {
                // Already there.
            }
        }
        using (var bin = LinuxFs.OpenBeneath(root, FsPath.TrashFolder, LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow))
        {
            LinuxFs.RemoveAcls(bin);
            LinuxFs.ChangeOwner(bin, 0, 0);
            LinuxFs.ChangeMode(bin, BinMode);
        }
        using var incoming = LinuxFs.OpenBeneath(root, $"{FsPath.TrashFolder}/{SmbConfig.RecycleFolder}", LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
        LinuxFs.RemoveAcls(incoming);
        LinuxFs.ChangeOwner(incoming, 0, 0);
        LinuxFs.ChangeMode(incoming, IncomingMode);
    }

    /// <summary>Turns every file Samba moved to the bin of the share into a recycle bin item. Returns how many.</summary>
    public async Task<int> IngestAsync(long shareId, string sharePath, CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, long> users;
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                users = (await connection.QueryAsync<(string Username, long Id)>("SELECT username, id FROM users")).ToDictionary(user => user.Username, user => user.Id, StringComparer.Ordinal);
            }
            SafeFileHandle root;
            try
            {
                root = LinuxFs.OpenRoot(sharePath);
            }
            catch (FsError)
            {
                // The share's dataset is not mounted; its files wait.
                return 0;
            }
            using (root)
            using (var bin = TrashService.OpenFolder(root))
            {
                SafeFileHandle incoming;
                try
                {
                    incoming = LinuxFs.OpenBeneath(bin, SmbConfig.RecycleFolder, LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
                }
                catch (FsError error) when (error.Errno == LinuxFs.ENOENT)
                {
                    return 0;
                }
                using (incoming)
                {
                    var count = 0;
                    foreach (var (name, kind) in LinuxFs.ReadNames(incoming))
                    {
                        if (kind != FsKind.Folder)
                        {
                            continue;
                        }
                        using (var folder = OpenFolder(incoming, name))
                        {
                            if (folder is null)
                            {
                                continue;
                            }
                            count += Ingest(shareId, bin, folder, "", users.TryGetValue(name, out var userId) ? userId : null);
                        }
                        TryRemoveFolder(incoming, name);
                    }
                    if (count > 0)
                    {
                        LogIngested(count, shareId);
                    }
                    return count;
                }
            }
        }
        finally
        {
            _running.Release();
        }
    }

    private int Ingest(long shareId, SafeFileHandle bin, SafeFileHandle folder, string relative, long? userId)
    {
        var count = 0;
        foreach (var (name, kind) in LinuxFs.ReadNames(folder))
        {
            var path = relative.Length == 0 ? name : $"{relative}/{name}";
            if (kind == FsKind.Folder)
            {
                using (var child = OpenFolder(folder, name))
                {
                    if (child is not null)
                    {
                        count += Ingest(shareId, bin, child, path, userId);
                    }
                }
                TryRemoveFolder(folder, name);
                continue;
            }
            FsStat stat;
            try
            {
                stat = LinuxFs.Stat(folder, name);
            }
            catch (FsError)
            {
                continue;
            }
            var id = trash.Reserve(shareId, path, stat.Kind, stat.Kind == FsKind.File ? stat.Size : 0, userId);
            try
            {
                LinuxFs.Rename(folder, name, bin, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                count++;
            }
            catch (FsError error)
            {
                trash.Forget(id);
                LogMoveFailed(path, error.Errno);
            }
        }
        return count;
    }

    /// <summary>A folder entry opened without following a symlink, or null when it is not a folder (any more).</summary>
    private static SafeFileHandle? OpenFolder(SafeFileHandle parent, string name)
    {
        try
        {
            return LinuxFs.OpenBeneath(parent, name, LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
        }
        catch (FsError)
        {
            return null;
        }
    }

    private static void TryRemoveFolder(SafeFileHandle parent, string name)
    {
        try
        {
            LinuxFs.Unlink(parent, name, folder: true);
        }
        catch (FsError)
        {
            // Not empty (a delete arrived meanwhile) or gone; the next run takes it.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved {Count} files deleted over SMB into the recycle bin of share {ShareId}")]
    private partial void LogIngested(int count, long shareId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not move {Path}, deleted over SMB, into the recycle bin (errno {Errno})")]
    private partial void LogMoveFailed(string path, int errno);
}
