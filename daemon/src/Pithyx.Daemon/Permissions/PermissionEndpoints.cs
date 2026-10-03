using System.Globalization;
using System.Security.Claims;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Permissions;

/// <summary>A user's rights changed; only that user gets them on <c>session/permissions</c>.</summary>
/// <param name="UserId">Whose rights.</param>
/// <param name="Permissions">The new rights.</param>
public sealed record PermissionsChanged(long UserId, SessionPermissions Permissions);

/// <summary>The grant editors and the per-user topic <c>session/permissions</c>.</summary>
public static class PermissionEndpoints
{
    public const string Topic = "session/permissions";

    /// <summary>Asks for the snapshot of the subscriber's own rights.</summary>
    private sealed record OwnPermissions;

    public static IServiceCollection AddPithyxPermissions(this IServiceCollection services)
    {
        services.AddSingleton<PermissionService>();
        services.AddSingleton<GrantService>();
        services.AddSingleton(provider => Definition(provider.GetRequiredService<PermissionService>()));
        services.AddHostedService<PermissionChangePublisher>();
        return services;
    }

    /// <summary>
    /// Every subscriber gets its own rights as the snapshot and whenever they change. A user who lost the desktop
    /// login right is signed out instead (<see cref="PermissionChangePublisher"/>).
    /// </summary>
    public static TopicDefinition Definition(PermissionService permissions) => new(
        Topic,
        typeof(SessionPermissions),
        typeof(SessionPermissions),
        (_, _) => ValueTask.FromResult<object>(new OwnPermissions()),
        TopicDefinition.AnyUser,
        ForUser: (user, data) => data switch
        {
            OwnPermissions => GrantService.Session(permissions.ForUser(user)),
            PermissionsChanged changed when changed.UserId.ToString(CultureInfo.InvariantCulture) == user.FindFirstValue(ClaimTypes.NameIdentifier) => changed.Permissions,
            _ => null,
        });

    public static void MapPermissionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/permissions/catalog", (HttpContext context) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(GrantService.Catalog);
            })
            .WithTags("Permissions")
            .RequireAuthorization(Policies.Admin)
            .WithName("GetPermissionCatalog")
            .WithDescription("Administrators only. The special rights, file protocols and apps a grant can name, in the order the editors show them.");
        api.MapGet("/users/{userId:long}/grants", async (long userId, GrantService grants, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await grants.UserGrantsAsync(context.User, userId, cancellationToken)))
            .WithTags("Permissions")
            .RequireAuthorization(Policies.Admin)
            .WithName("GetUserGrants")
            .WithDescription("Administrators only. The user's own grants, the grants its groups give it (the built-in group users included) and what it may do in the end.");
        api.MapPut("/users/{userId:long}/grants", async (long userId, GrantsRequest request, GrantService grants, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await grants.ReplaceUserGrantsAsync(context.User, userId, request, cancellationToken)))
            .WithTags("Permissions")
            .RequireAuthorization(Policies.Admin)
            .WithName("SetUserGrants")
            .WithDescription("Administrators only. Replaces every grant of the user. Refused when no active administrator would be left; applies to the user's next request and is pushed to its desktop.");
        api.MapGet("/groups/{groupId:long}/grants", async (long groupId, GrantService grants, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await grants.GroupGrantsAsync(context.User, groupId, cancellationToken)))
            .WithTags("Permissions")
            .RequireAuthorization(Policies.Admin)
            .WithName("GetGroupGrants")
            .WithDescription("Administrators only. Every grant of the group; its members have them all.");
        api.MapPut("/groups/{groupId:long}/grants", async (long groupId, GrantsRequest request, GrantService grants, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await grants.ReplaceGroupGrantsAsync(context.User, groupId, request, cancellationToken)))
            .WithTags("Permissions")
            .RequireAuthorization(Policies.Admin)
            .WithName("SetGroupGrants")
            .WithDescription("Administrators only. Replaces every grant of the group. Refused when no active administrator would be left.");
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new Errors.PithyxException(Errors.ErrorCodes.AuthPermissionDenied);
        }
    }
}

/// <summary>
/// After every change of rights: shares and file services are brought in line (Linux groups, ACLs, SMB, SFTP), every
/// open WebSocket gets the rights of its user anew, so topics follow at once, and each user gets its rights on
/// <c>session/permissions</c>. A connection whose user may no longer sign in to the desktop is closed.
/// </summary>
public sealed class PermissionChangePublisher(
    PermissionService permissions,
    TopicHub hub,
    ShareChanges shareChanges,
    FileServiceChanges fileServiceChanges) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        permissions.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        permissions.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged()
    {
        shareChanges.Announce();
        fileServiceChanges.Announce();
        var published = new HashSet<long>();
        hub.Reauthorize(user =>
        {
            var userId = CurrentUser.Id(user);
            var effective = permissions.ForUser(userId);
            if (!effective.Has(Resources.DesktopLogin))
            {
                return null;
            }
            if (published.Add(userId))
            {
                hub.Publish(PermissionEndpoints.Topic, new PermissionsChanged(userId, GrantService.Session(effective)));
            }
            return SessionAuthenticationHandler.WithRole(user, effective.IsAdmin);
        });
    }
}
