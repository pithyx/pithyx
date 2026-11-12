using System.Data.Common;
using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Notifications;

namespace Pithyx.Daemon.Permissions;

/// <summary>
/// Who may use the special folder Apps (D409): administrators only, until an administrator gives it to other users
/// or groups. Apps holds the data of every app, so a grant that gives somebody more of it than before needs the
/// confirmation that they may see it, and tells every other administrator. The editors of a share, a user and a group
/// and a new user's grants all go through here.
/// </summary>
public static class AppsAccess
{
    public const string GrantedCode = "shares.apps.granted";

    /// <summary>The id of Apps, or null before the daemon added it.</summary>
    public static Task<long?> ShareIdAsync(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteScalarAsync<long?>("SELECT id FROM shares WHERE system = @system", new { system = Apps.AppsShare.SystemKey }, transaction);
    }

    /// <summary>
    /// The grants of <paramref name="after"/> that give a subject more than <paramref name="before"/> did: a new grant,
    /// or read raised to read and write. Less access or the same needs nothing.
    /// </summary>
    public static IReadOnlyList<SubjectGrant> Widened(IReadOnlyList<SubjectGrant> before, IReadOnlyList<SubjectGrant> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return [.. after.Where(grant =>
        {
            var old = before.FirstOrDefault(entry => entry.SubjectKind == grant.SubjectKind && entry.SubjectId == grant.SubjectId);
            return old is null || (old.Level == GrantLevel.Read && grant.Level == GrantLevel.ReadWrite);
        })];
    }

    /// <summary>Refuses a widening without the confirmation (<see cref="ErrorCodes.FsAppsConfirmationRequired"/>).</summary>
    public static void RequireConfirmation(IReadOnlyList<SubjectGrant> widened, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(widened);
        if (widened.Count > 0 && !confirmed)
        {
            throw new PithyxException(ErrorCodes.FsAppsConfirmationRequired);
        }
    }

    /// <summary>
    /// Tells every administrator but the caller who got access to Apps (D409), on the desktop and the box's channels.
    /// A failed notification does not undo the grant.
    /// </summary>
    public static async Task NotifyAsync(NotificationService? notifications, SqliteConnectionFactory connections, ClaimsPrincipal caller, IReadOnlyList<SubjectGrant> widened)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(widened);
        if (notifications is null || widened.Count == 0)
        {
            return;
        }
        await using var connection = await connections.OpenAsync(CancellationToken.None);
        foreach (var grant in widened)
        {
            var subject = grant.SubjectKind == SubjectKind.Group
                ? await connection.ExecuteScalarAsync<string?>("SELECT name FROM groups WHERE id = @id", new { id = grant.SubjectId })
                : await connection.ExecuteScalarAsync<string?>("SELECT username FROM users WHERE id = @id", new { id = grant.SubjectId });
            await notifications.CreateForAdministratorsAsync(
                new NotificationDraft(GrantedCode, new Dictionary<string, object?>
                {
                    ["user"] = caller.Identity?.Name ?? "",
                    ["subject"] = subject ?? "",
                    ["kind"] = PermissionService.KindText(grant.SubjectKind),
                    ["access"] = PermissionService.LevelText(grant.Level),
                }, NotificationTone.Warning),
                CancellationToken.None,
                except: CurrentUser.Id(caller));
        }
    }
}
