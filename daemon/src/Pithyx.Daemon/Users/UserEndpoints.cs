using System.Globalization;
using System.Threading.Channels;
using Dapper;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Users;

/// <summary>User management for administrators.</summary>
public static class UserEndpoints
{
    public static IServiceCollection AddPithyxUsers(this IServiceCollection services, AccountOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<LinuxAccounts>();
        services.AddSingleton<UserService>();
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
            .WithDescription("Administrators only. Creates a user with a Linux account (a uid from 20000 to 29999 that is never given out again, the group users, no login shell) and a home dataset below the first pool's homes when there is a pool.");
        users.MapPatch("/{userId:long}", async (long userId, UserUpdate request, UserService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(context.User, userId, request, cancellationToken)))
            .WithName("UpdateUser")
            .WithDescription("Administrators only. Changes the display name or the administrator right; the last administrator keeps the right.");
        users.MapPut("/{userId:long}/password", async (long userId, PasswordReset request, UserService service, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.ResetPasswordAsync(context.User, userId, request, cancellationToken);
                hub.CloseUser(userId.ToString(CultureInfo.InvariantCulture));
                return TypedResults.NoContent();
            })
            .WithName("ResetUserPassword")
            .WithDescription("Administrators only. Sets a new password and ends every session of the user.");
        users.MapDelete("/{userId:long}", async (long userId, UserService service, TopicHub hub, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(context.User, userId, cancellationToken);
                hub.CloseUser(userId.ToString(CultureInfo.InvariantCulture));
                return TypedResults.NoContent();
            })
            .WithName("DeleteUser")
            .WithDescription("Administrators only. Deletes the user and its Linux account; its files and home stay, and its uid is never given out again. Nobody deletes themselves, and the last administrator stays.");
    }
}

/// <summary>
/// Brings the Linux accounts in line at start and whenever the pools change, and gives every user a home dataset once
/// the first pool with the Pithyx layout is there. Real mode only.
/// </summary>
public sealed partial class LinuxAccountSync(LinuxAccounts accounts, PoolInventory pools, SqliteConnectionFactory connections, ILogger<LinuxAccountSync> logger) : BackgroundService
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
        pools.Changed += Signal;
        try
        {
            Signal();
            await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
            {
                await using (var connection = await connections.OpenAsync(stoppingToken))
                {
                    foreach (var (username, uid) in await connection.QueryAsync<(string, long)>("SELECT username, uid FROM users WHERE uid IS NOT NULL"))
                    {
                        await accounts.CreateHomeAsync(username, uid, stoppingToken);
                    }
                }
                await accounts.ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            pools.Changed -= Signal;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "/etc/login.defs lets useradd outside Pithyx allocate ids in 20000 to 49999 ({Settings}); accounts made that way could collide with Pithyx users")]
    private partial void LogLoginDefsOverlap(string settings);
}
