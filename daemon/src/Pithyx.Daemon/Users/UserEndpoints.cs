using System.Globalization;
using System.Threading.Channels;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Users;

/// <summary>User management for administrators.</summary>
public static class UserEndpoints
{
    public static IServiceCollection AddPithyxUsers(this IServiceCollection services, AccountOptions options, HomeStorageOptions homes)
    {
        services.AddSingleton(options);
        services.AddSingleton(homes);
        services.AddSingleton<Homes>();
        services.AddSingleton<HomeService>();
        services.AddSingleton<FileServices.SmbSessions>();
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
            .RequireAuthorization(Policies.Admin)
            .WithName("GetUsers")
            .WithDescription("Administrators only. Every user with its Linux uid and home folder.");
        users.MapPost("", async (NewUser request, UserService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Created((string?)null, await service.CreateAsync(context.User, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("CreateUser")
            .WithDescription("Administrators only. Creates a user with a Linux and a Samba account with the password (a uid from 20000 to 29999 that is never given out again, the group users, no login shell) and its home on the home pool when there is one.");
        users.MapPatch("/{userId:long}", async (long userId, UserUpdate request, UserService service, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                var user = await service.UpdateAsync(context.User, userId, request, cancellationToken);
                if (user.Disabled)
                {
                    hub.CloseUser(userId.ToString(CultureInfo.InvariantCulture));
                }
                return TypedResults.Ok(user);
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("UpdateUser")
            .WithDescription("Administrators only. Changes the display name, the administrator right, the SMB and SFTP rights or whether the user is deactivated. Deactivating ends the user's sessions and expires the Linux account. The last active administrator keeps the right and stays active, and nobody deactivates themselves.");
        users.MapPut("/{userId:long}/password", async (long userId, PasswordReset request, UserService service, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.ResetPasswordAsync(context.User, userId, request, cancellationToken);
                hub.CloseUser(userId.ToString(CultureInfo.InvariantCulture));
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("ResetUserPassword")
            .WithDescription("Administrators only. Sets a new password, for SMB and SFTP as well, and ends every session of the user.");
        users.MapDelete("/{userId:long}", async (long userId, UserService service, TopicHub hub, HttpContext context, CancellationToken cancellationToken, string? home = null, long? shareId = null, string? confirm = null) =>
            {
                var choice = home switch
                {
                    null or "keep" => HomeDataChoice.Keep,
                    "move" => HomeDataChoice.Move,
                    "delete" => HomeDataChoice.Delete,
                    _ => throw new PithyxException(ErrorCodes.HomesChoiceInvalid),
                };
                var deletion = await service.DeleteAsync(context.User, userId, choice, shareId, confirm, cancellationToken);
                hub.CloseUser(userId.ToString(CultureInfo.InvariantCulture));
                return TypedResults.Ok(deletion);
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("DeleteUser")
            .WithDescription("Administrators only. Deletes the user and its Linux account; its uid is never given out again and its other files stay. Its home stays as <name>.deleted-<uid> (home=keep, the default), is deleted with every file (home=delete with confirm set to the user name) or moves into a folder of the shared folder shareId as a job (home=move), which the answer returns. Nobody deletes themselves, and the last administrator stays.");

        var homes = api.MapGroup("/homes").WithTags("Users").RequireAuthorization(Policies.Admin);
        homes.MapGet("", async (HomeService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.OverviewAsync(context.User, cancellationToken)))
            .WithName("GetHomes")
            .WithDescription("Administrators only. The home pool, every pool with whether it can take the homes, the default quota and every home with its user, space and quota, the homes of deleted users included.");
        homes.MapPut("/settings", async (HomeSettingsRequest request, HomeService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SetDefaultQuotaAsync(context.User, request, cancellationToken)))
            .WithName("SetHomeSettings")
            .WithDescription("Administrators only. Sets the quota of every home without its own (at least 1 MiB, null for none).");
        homes.MapPut("/users/{userId:long}/quota", async (long userId, HomeQuotaRequest request, HomeService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SetUserQuotaAsync(context.User, userId, request, cancellationToken)))
            .WithName("SetHomeQuota")
            .WithDescription("Administrators only. Sets a user's own home quota: at least 1 MiB, 0 for none, null for the default.");
        homes.MapPost("/move", async (HomeMoveRequest request, HomeService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var job = await service.StartMoveAsync(context.User, request, cancellationToken);
                return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
            })
            .WithName("MoveHomes")
            .WithDescription("Administrators only. Moves every home to another pool as a job: each home is read-only while it is copied with its files, owners and ACLs, then it is used from the new pool and the old copy is destroyed. A home that fails stays where it was.");
        homes.MapDelete("/{shareId:long}", async (long shareId, string? confirm, HomeService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(context.User, shareId, confirm, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteHome")
            .WithDescription("Administrators only. Deletes the home of a deleted user with every file and snapshot, after its folder name was typed (confirm).");
        homes.MapPost("/{shareId:long}/release", async (long shareId, HomeReleaseRequest request, HomeService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var job = await service.StartReleaseAsync(context.User, shareId, request, cancellationToken);
                return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
            })
            .WithName("ReleaseHome")
            .WithDescription("Administrators only. Moves the files of a deleted user's home into a new folder of a shared folder as a job, then deletes the home with its recycle bin.");

        var groups = api.MapGroup("/groups").WithTags("Users");
        groups.MapGet("", async (GroupService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ListAsync(context.User, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("GetGroups")
            .WithDescription("Administrators only. Every group with its members, the built-in group users (every user) first.");
        groups.MapPost("", async (NewGroup request, GroupService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Created((string?)null, await service.CreateAsync(context.User, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("CreateGroup")
            .WithDescription("Administrators only. Creates a group with a Linux group (a gid from 30000 to 39999 that is never given out again) and its members.");
        groups.MapPatch("/{groupId:long}", async (long groupId, GroupUpdate request, GroupService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(context.User, groupId, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("UpdateGroup")
            .WithDescription("Administrators only. Renames a group, changes its description or sets its members; the gid stays. The built-in group cannot be changed.");
        groups.MapDelete("/{groupId:long}", async (long groupId, GroupService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(context.User, groupId, cancellationToken);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
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
    Homes homes,
    ShareAcls shareAcls,
    FileServiceSync fileServices,
    PoolInventory pools,
    ShareChanges shareChanges,
    FileServiceChanges fileServiceChanges,
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
                    await homes.ReconcileAsync(stoppingToken);
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

/// <summary>
/// Simulation only: brings the homes in line at start, when the pools change and when users or rights change, which
/// <see cref="LinuxAccountSync"/> does on a box.
/// </summary>
public sealed class SimulatedHomeSync(Homes homes, PoolInventory pools, Permissions.PermissionService permissions) : BackgroundService
{
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Signal() => _changes.Writer.TryWrite(true);
        pools.Changed += Signal;
        permissions.Changed += Signal;
        try
        {
            Signal();
            await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
            {
                await homes.ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            pools.Changed -= Signal;
            permissions.Changed -= Signal;
        }
    }
}
