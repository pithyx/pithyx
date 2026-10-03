using System.Globalization;
using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.Users;

/// <summary>
/// Gives every shared folder on a dataset its POSIX ACL (D286, D102 since D290): accounts have the primary group
/// <c>users</c> and a umask of 077, so a share is reached only through its two named group entries, the share's read
/// group with <c>r-X</c> and its write group with <c>rwX</c> (<see cref="Permissions.ShareGroups"/>); the owning user
/// keeps its own files, and the owning group and others get nothing. Who may read or write is a matter of group
/// members, so a change of rights never walks the files. Folders also get the same default ACL, so what SMB, SFTP or
/// the daemon creates later inherits it. <c>setfacl</c> walks the tree without following symlinks. The ACL last set is
/// kept in <c>shares.acl</c>, so a share is walked only when its entries change, not at every start; the share's own
/// folder is compared at every run, and a difference is logged as a warning and set again. Real mode only.
/// </summary>
public sealed partial class ShareAcls(
    SqliteConnectionFactory connections,
    ICommandRunner commands,
    AccountOptions options,
    ILogger<ShareAcls> logger,
    FileServices.SmbRecycle? recycle = null) : IDisposable
{
    /// <summary>A large share takes a while; the walk is never cut short by the default command timeout.</summary>
    public static readonly TimeSpan WalkTimeout = TimeSpan.FromHours(2);

    private readonly SemaphoreSlim _running = new(1, 1);

    public void Dispose() => _running.Dispose();

    /// <summary>
    /// The <c>setfacl</c> entries for a share: the owner full, no owning group or other access, the read group
    /// <c>r-X</c> and the write group <c>rwX</c> (<c>X</c>: execute only for folders). The mask allows what the groups have.
    /// </summary>
    public static string Entries(long readGid, long writeGid) => string.Join(
        ',',
        "u::rwX",
        "g::---",
        "o::---",
        "m::rwX",
        $"g:{readGid.ToString(CultureInfo.InvariantCulture)}:r-X",
        $"g:{writeGid.ToString(CultureInfo.InvariantCulture)}:rwX");

    /// <summary>
    /// The access entries <c>getfacl</c> shows on the share's folder when <paramref name="entries"/> are set, in its
    /// long form (<c>group:30004:r-x</c>); <c>X</c> is <c>x</c> on a folder.
    /// </summary>
    public static IReadOnlySet<string> FolderEntries(string entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Split(',').Select(entry =>
        {
            var parts = entry.Split(':');
            var kind = parts[0] switch { "u" => "user", "g" => "group", "m" => "mask", _ => "other" };
            return $"{kind}:{parts[1]}:{parts[2].Replace('X', 'x')}";
        }).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The access entries of <c>getfacl --omit-header --numeric</c> output, without the default ACL and comments.</summary>
    public static IReadOnlySet<string> ParseGetfacl(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Split('\n')
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("default:", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The access ACL on everything below the share, then the default ACL on its folders.</summary>
    public static IReadOnlyList<CommandRequest> Commands(string path, string entries) =>
    [
        new CommandRequest("setfacl", ["-R", "-P", "--set", entries, "--", path], WalkTimeout),
        new CommandRequest("setfacl", ["-R", "-P", "-d", "--set", entries, "--", path], WalkTimeout),
    ];

    /// <summary>Every dataset share with its groups, its folder, the entries it should have and the ones last set.</summary>
    public async Task<IReadOnlyList<(long ShareId, string Path, string Entries, string? Applied)>> WantedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var shares = await connection.QueryAsync<(long Id, string Path, string? Acl, long ReadGid, long WriteGid)>(
            "SELECT id, path, acl, read_gid, write_gid FROM shares WHERE dataset IS NOT NULL AND read_gid IS NOT NULL AND write_gid IS NOT NULL ORDER BY id");
        return [.. shares.Select(share => (share.Id, share.Path, Entries(share.ReadGid, share.WriteGid), share.Acl))];
    }

    /// <summary>Sets the ACL of every share whose rights changed since it was last set. Never throws but for cancellation.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!options.Apply)
        {
            return;
        }
        await _running.WaitAsync(cancellationToken);
        try
        {
            foreach (var (shareId, path, entries, applied) in await WantedAsync(cancellationToken))
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }
                if (entries == applied)
                {
                    // Pithyx is the only source of truth (D102): an ACL changed behind its back is reported and set again.
                    var actual = await commands.RunAsync(new CommandRequest("getfacl", ["--omit-header", "--numeric", "--", path]), cancellationToken);
                    if (actual.ExitCode != 0 || ParseGetfacl(actual.StandardOutput).SetEquals(FolderEntries(entries)))
                    {
                        continue;
                    }
                    LogDrift(shareId, path, actual.StandardOutput.ReplaceLineEndings(" "));
                }
                if (await ApplyAsync(path, entries, cancellationToken))
                {
                    // The walk gave the recycle bin the share's ACL; it gets its own modes back (SmbRecycle).
                    PrepareBin(path);
                    await using var connection = await connections.OpenAsync(cancellationToken);
                    await connection.ExecuteAsync("UPDATE shares SET acl = @entries WHERE id = @shareId", new { entries, shareId });
                    LogApplied(shareId, path);
                }
                else
                {
                    LogFailed(shareId, path);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The next change tries again; files keep the ACL they had.
            LogError(exception);
        }
        finally
        {
            _running.Release();
        }
    }

    private void PrepareBin(string path)
    {
        try
        {
            recycle?.Prepare(path);
        }
        catch (Exception exception) when (exception is Files.FsError or IOException or UnauthorizedAccessException)
        {
            LogBinFailed(exception, path);
        }
    }

    private async Task<bool> ApplyAsync(string path, string entries, CancellationToken cancellationToken)
    {
        foreach (var command in Commands(path, entries))
        {
            if ((await commands.RunAsync(command, cancellationToken)).ExitCode != 0)
            {
                return false;
            }
        }
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Share {ShareId} at {Path}: ACL set")]
    private partial void LogApplied(long shareId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Share {ShareId} at {Path}: its ACL differs from its rights ({Actual}); setting it again")]
    private partial void LogDrift(long shareId, string path, string actual);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Share {ShareId} at {Path}: setting the ACL failed")]
    private partial void LogFailed(long shareId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Giving the recycle bin of {Path} its modes back failed")]
    private partial void LogBinFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setting the ACLs of the shared folders failed")]
    private partial void LogError(Exception exception);
}
