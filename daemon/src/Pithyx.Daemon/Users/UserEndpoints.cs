using System.Globalization;
using System.Threading.Channels;
using Dapper;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Users;

/// <summary>User management for administrators.</summary>
public static class UserEndpoints
{
    public static IServiceCollection AddPithyxUsers(this IServiceCollection services, AccountOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<LinuxAccounts>();
        services.AddSingleton<ShareAcls>();
        services.AddSingleton<UserService>();
        services.AddSingleton<GroupService>();
        return services;
    }

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Users");
        users.MapGet("", async (UserService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ListAsync(context.User, cancellationToken)))
            .WithName("GetUsers")
            .WithDescription("Administrators only. Every user with its Linux uid and home folder.");
        users.MapPost("", async (NewUser request, UserService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Created((string?)null, await service.CreateAsync(context.User, request, cancellationToken)))
            .WithName("CreateUser")
            .WithDescription("Administrators only. Creates a user with a Linux and a Samba account with the password (a uid from 20000 to 29999 that is never given out again, the group users, no login shell) and a home dataset below the first pool's homes when there is a pool.");
        users.MapPatch("/{userId:long}", async (long userId, UserUpdate request, UserService service, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                var user = await service.UpdateAsync(context.User, userId, request, cancellationToken);
                if (user.Disabled)
                {
                    hub.CloseUser(userId.ToString(CultureInfo.InvariantCulture));
                }
                return TypedResults.Ok(user);
            })
            .WithName("UpdateUser")
            .WithDescription("Administrators only. Changes the display name, the administrator right, the SMB and SFTP rights or whether the user is deactivated. Deactivating ends the user's sessions and expires the Linux account. The last active administrator keeps the right and stays active, and nobody deactivates themselves.");
        users.MapPut("/{userId:long}/password", async (long userId, PasswordReset request, UserService service, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.ResetPasswordAsync(context.User, userId, request, cancellationToken);
                hub.CloseUser(userId.ToString(CultureInfo.InvariantCulture));
                return TypedResults.NoContent();
            })
            .WithName("ResetUserPassword")
            .WithDescription("Administrators only. Sets a new password, for SMB and SFTP as well, and ends every session of the user.");
        users.MapDelete("/{userId:long}", async (long userId, UserService service, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(context.User, userId, cancellationToken);
                hub.CloseUser(userId.ToString(CultureInfo.InvariantCulture));
                return TypedResults.NoContent();
            })
            .WithName("DeleteUser")
            .WithDescription("Administrators only. Deletes the user and its Linux account; its files and home stay, and its uid is never given out again. Nobody deletes themselves, and the last administrator stays.");

        var groups = api.MapGroup("/groups").WithTags("Users");
        groups.MapGet("", async (GroupService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ListAsync(context.User, cancellationToken)))
            .WithName("GetGroups")
            .WithDescription("Administrators only. Every group with its members, the built-in group users (every user) first.");
        groups.MapPost("", async (NewGroup request, GroupService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Created((string?)null, await service.CreateAsync(context.User, request, cancellationToken)))
            .WithName("CreateGroup")
            .WithDescription("Administrators only. Creates a group with a Linux group (a gid from 30000 to 39999 that is never given out again) and its members.");
        groups.MapPatch("/{groupId:long}", async (long groupId, GroupUpdate request, GroupService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(context.User, groupId, request, cancellationToken)))
            .WithName("UpdateGroup")
            .WithDescription("Administrators only. Renames a group, changes its description or sets its members; the gid stays. The built-in group cannot be changed.");
        groups.MapDelete("/{groupId:long}", async (long groupId, GroupService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(context.User, groupId, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteGroup")
            .WithDescription("Administrators only. Deletes a group and its Linux group; files keep its gid, which is never given out again. The built-in group stays.");
    }
}

/// <summary>
/// Brings the Linux accounts in line at start and whenever the pools change, and gives every user a home dataset once
/// the first pool with the Pithyx layout is there. The ACLs of the shared folders follow at start, when the pools
/// change and whenever the shares, their rights or the users change (<see cref="ShareAcls"/>), and SMB and SFTP after
/// them (<see cref="FileServiceSync"/>), also when their settings change. Real mode only.
/// </summary>
public sealed partial class LinuxAccountSync(
    LinuxAccounts accounts,
    ShareAcls shareAcls,
    FileServiceSync fileServices,
    PoolInventory pools,
    ShareChanges shareChanges,
    FileServiceChanges fileServiceChanges,
    SqliteConnectionFactory connections,
    ILogger<LinuxAccountSync> logger) : BackgroundService
{
    public const string LoginDefsPath = "/etc/login.defs";

    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (File.Exists(LoginDefsPath) && LoginDefs.Overlaps(await File.ReadAllTextAsync(LoginDefsPath, stoppingToken)) is { Count: > 0 } overlaps)
        {
            LogLoginDefsOverlap(string.Join(", ", overlaps));
        }
        void Signal() => _changes.Writer.TryWrite(true);
        void SharesChanged(long revision) => Signal();
        pools.Changed += Signal;
        shareChanges.Changed += SharesChanged;
        fileServiceChanges.Changed += Signal;
        var first = true;
        try
        {
            Signal();
            await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using (var connection = await connections.OpenAsync(stoppingToken))
                    {
                        foreach (var (username, uid) in await connection.QueryAsync<(string, long)>("SELECT username, uid FROM users WHERE uid IS NOT NULL"))
                        {
                            await accounts.CreateHomeAsync(username, uid, stoppingToken);
                        }
                    }
                    await accounts.ReconcileAsync(stoppingToken);
                    await shareAcls.ReconcileAsync(stoppingToken);
                    // The first run checks every file and service, whatever changed while the daemon was not running.
                    await fileServices.ReconcileAsync(force: first, stoppingToken);
                    first = false;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A command that cannot start (a missing tool, no root) must not stop the daemon; the next change tries again.
                    LogSyncFailed(exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            pools.Changed -= Signal;
            shareChanges.Changed -= SharesChanged;
            fileServiceChanges.Changed -= Signal;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Bringing the Linux accounts and share ACLs in line failed")]
    private partial void LogSyncFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "/etc/login.defs lets useradd outside Pithyx allocate ids in 20000 to 49999 ({Settings}); accounts made that way could collide with Pithyx users")]
    private partial void LogLoginDefsOverlap(string settings);
}
