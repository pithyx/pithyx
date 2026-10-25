using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.Layouts;

/// <summary>
/// Keeps every user's desktop shortcuts to files and folders on their target when the daemon renames or moves it
/// (Files, apps, WebDAV, FTP). Changes made past the daemon (SMB, SFTP, SSH) leave a shortcut pointing at a path that
/// no longer exists; the desktop shows it as missing (D382).
/// </summary>
public sealed class DesktopFileLinks(SqliteConnectionFactory connections, TimeProvider time)
{
    /// <summary>
    /// The entry at <paramref name="fromPath"/> in share <paramref name="fromShare"/> is now at <paramref name="toPath"/>
    /// in <paramref name="toShare"/>: shortcuts to it and to anything below it follow. Paths are normalized and relative
    /// to the share.
    /// </summary>
    public async Task MovedAsync(long fromShare, string fromPath, long toShare, string toPath, CancellationToken cancellationToken)
    {
        if (fromPath.Length == 0 || (fromShare == toShare && fromPath == toPath))
        {
            return;
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            UPDATE desktop_items
            SET data = json_set(data, '$.share', @toShare, '$.path', @toPath || substr(json_extract(data, '$.path'), length(@fromPath) + 1)),
                version = version + 1, updated_at = @now
            WHERE kind = 'file' AND json_extract(data, '$.share') = @fromShare
                AND (json_extract(data, '$.path') = @fromPath
                    OR substr(json_extract(data, '$.path'), 1, length(@fromPath) + 1) = @fromPath || '/')
            """,
            new { fromShare, fromPath, toShare, toPath, now = SessionService.Format(time.GetUtcNow()) });
    }
}
