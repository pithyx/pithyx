using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Notifications;

/// <summary>The signed-in user's notification history. Everyone reads and changes only their own notifications.</summary>
public static class NotificationEndpoints
{
    public static IServiceCollection AddPithyxNotifications(this IServiceCollection services)
    {
        services.AddSingleton<NotificationOutbox>();
        services.AddSingleton<DeliveryThrottle>();
        services.TryAddSingleton(new NotificationDeliveryOptions());
        services.AddSingleton(provider => new ChannelSender(provider.GetRequiredService<NotificationDeliveryOptions>()));
        services.AddSingleton<ChannelService>();
        services.AddHostedService<NotificationDelivery>();
        services.AddSingleton<NotificationStore>();
        services.AddSingleton(provider => provider.GetRequiredService<NotificationStore>().Definition());
        services.AddSingleton<NotificationService>();
        services.AddHostedService<NotificationCleanupService>();
        return services;
    }

    public static void MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var notifications = api.MapGroup("/notifications").WithTags("Notifications");
        notifications.MapGet("", async (NotificationService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ListAsync(context.User, cancellationToken)))
            .WithName("ListNotifications")
            .WithDescription(
                "The signed-in user's notifications, newest first: at most 200, none older than 30 days. Live through the "
                + "notifications topic, whose snapshot is the same list.");
        notifications.MapPost("/read", async (MarkNotificationsReadRequest request, NotificationService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.MarkReadAsync(context.User, request.Ids, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("MarkNotificationsRead")
            .WithDescription("Marks the given notifications of the signed-in user as read, or all of them without ids. Unknown ids are ignored.");
        notifications.MapDelete("/{id:long}", async (long id, NotificationService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(context.User, id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteNotification")
            .WithDescription("Deletes one notification of the signed-in user. Someone else's answers 404 notifications.notification.not_found.");
        notifications.MapDelete("", async (NotificationService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.ClearAsync(context.User, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("ClearNotifications")
            .WithDescription("Deletes every notification of the signed-in user. Storage alerts are not notifications and stay.");
        notifications.MapGet("/categories", async (NotificationService service, Apps.AppRegistry registry, Permissions.PermissionService permissions, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await NotificationCategories.ListAsync(CurrentUser.Id(context.User), service, registry, permissions, cancellationToken)))
            .WithName("ListNotificationCategories")
            .WithDescription(
                "The kinds of notifications the apps the signed-in user may open declare in their manifest (D390), each with "
                + "whether the user muted it.");
        notifications.MapPut("/categories/{app}/{category}", async (string app, string category, NotificationCategoryMuteRequest request, NotificationService service, Apps.AppRegistry registry, Permissions.PermissionService permissions, HttpContext context, CancellationToken cancellationToken) =>
            {
                var userId = CurrentUser.Id(context.User);
                if (!(await NotificationCategories.ListAsync(userId, service, registry, permissions, cancellationToken)).Categories.Any(entry => entry.App == app && entry.Id == category))
                {
                    throw new PithyxException(ErrorCodes.NotificationsCategoryNotFound, new Dictionary<string, object?> { ["app"] = app, ["category"] = category });
                }
                await service.SetMutedAsync(userId, app, category, request.Muted, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("MuteNotificationCategory")
            .WithDescription(
                "Mutes or unmutes one kind of an app's notifications for the signed-in user (D390). A muted notification is kept "
                + "as read, without a toast and without the user's channels. An app the user may not open or a category its "
                + "manifest does not declare answers 404 notifications.category.not_found.");
        notifications.MapPost("/app", async Task<Created<Notification>> (AppNotificationRequest request, NotificationService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var notification = await service.CreateFromAppAsync(context.User, request, cancellationToken);
                return TypedResults.Created($"/api/notifications/{notification.Id}", notification);
            })
            .WithName("CreateAppNotification")
            .WithDescription(
                "What a core app's SDK notify call becomes: a notification for the signed-in user with the app's plain text, "
                + "stored as app.message. Only for core apps (org.pithyx.*, auth.permission.denied otherwise); third-party apps "
                + "send theirs to /api/app/notifications with their token. Invalid fields answer 400 notifications.message.invalid.");

        notifications.MapPut("/language", async (NotificationLanguageRequest request, ChannelService channels, HttpContext context, CancellationToken cancellationToken) =>
            {
                await channels.SetLanguageAsync(CurrentUser.Id(context.User), request.Language, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("SetNotificationLanguage")
            .WithDescription("The language the signed-in user's desktop uses (en or de), for the emails and webhooks the box writes to them (D379). Without it the system language applies. Others answer 400 notifications.language.unsupported.");

        var own = notifications.MapGroup("/channels");
        MapChannels(own, context => CurrentUser.Id(context.User), "", "the signed-in user's own channels, which get the notifications that reach the user");
        var system = api.MapGroup("/system/notifications").WithTags("Notifications").RequireAuthorization(Policies.Admin);
        MapChannels(system.MapGroup("/channels"), _ => null, "System", "the box's channels, which get what the system tells every administrator");
        system.MapGet("/smtp", async Task<Ok<SmtpStatus>> (ChannelService channels, CancellationToken cancellationToken) =>
                TypedResults.Ok(await channels.SmtpStatusAsync(cancellationToken)))
            .WithName("GetNotificationSmtp")
            .WithDescription("Administrators only. The SMTP server email channels send through, without its password.");
        system.MapPut("/smtp", async Task<Ok<SmtpStatus>> (SmtpRequest request, ChannelService channels, CancellationToken cancellationToken) =>
                TypedResults.Ok(await channels.SetSmtpAsync(request, cancellationToken)))
            .WithName("SetNotificationSmtp")
            .WithDescription("Administrators only. Saves the SMTP server; an empty host removes it. A missing password keeps the saved one, an empty one removes it. Invalid fields answer 400 notifications.smtp.invalid.");
        system.MapGet("/matrix", async Task<Ok<NotificationMatrix>> (ChannelService channels, CancellationToken cancellationToken) =>
                TypedResults.Ok(await channels.MatrixAsync(cancellationToken)))
            .WithName("GetNotificationMatrix")
            .WithDescription("Administrators only. Every event the box reports to its administrators, the box's channels and, per event, whether the administrators' desktop and each channel get it (D407). A cell nobody set shows its default: the desktop gets everything, a channel what its categories and least level take.");
        system.MapPut("/matrix", async Task<Ok<NotificationMatrix>> (NotificationMatrixRequest request, ChannelService channels, CancellationToken cancellationToken) =>
                TypedResults.Ok(await channels.SetMatrixAsync(request, cancellationToken)))
            .WithName("SetNotificationMatrix")
            .WithDescription("Administrators only. Sets cells of the matrix and answers with the whole matrix; the other cells stay. An unknown event answers 400 notifications.event.unknown, a channel that is not one of the box's 404 notifications.channel.not_found.");
        system.MapPost("/smtp/test", async (SmtpTestRequest request, ChannelService channels, CancellationToken cancellationToken) =>
            {
                await channels.TestSmtpAsync(request, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("TestNotificationSmtp")
            .WithDescription("Administrators only. Sends a test email to one address through the server given in the body, saved or not; a missing password takes the saved one, and nothing is stored. Invalid fields answer 400 notifications.smtp.invalid (field to for the address), a failure 502 notifications.channel.failed with the server's words.");
    }

    private static void MapChannels(RouteGroupBuilder group, Func<HttpContext, long?> owner, string prefix, string whose)
    {
        var admin = prefix.Length == 0 ? "" : "Administrators only. ";
        group.MapGet("", async Task<Ok<NotificationChannelList>> (ChannelService channels, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await channels.ListAsync(owner(context), cancellationToken)))
            .WithName($"List{prefix}NotificationChannels")
            .WithDescription($"{admin}Lists {whose}: email and webhook channels with what they get and how their last delivery went. Header values never leave the box.");
        group.MapPost("", async Task<Ok<NotificationChannel>> (NotificationChannelRequest request, ChannelService channels, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await channels.CreateAsync(owner(context), request, cancellationToken)))
            .WithName($"Create{prefix}NotificationChannel")
            .WithDescription($"{admin}Adds one of {whose}. An email channel needs the SMTP server (409 notifications.smtp.missing); invalid fields answer 400 notifications.channel.invalid, too many 409 notifications.channel.limit_reached.");
        group.MapPut("/{id:long}", async Task<Ok<NotificationChannel>> (long id, NotificationChannelRequest request, ChannelService channels, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await channels.UpdateAsync(owner(context), id, request, cancellationToken)))
            .WithName($"Update{prefix}NotificationChannel")
            .WithDescription($"{admin}Changes one of {whose}; its kind stays. A header without a value keeps the value saved before.");
        group.MapDelete("/{id:long}", async (long id, ChannelService channels, HttpContext context, CancellationToken cancellationToken) =>
            {
                await channels.DeleteAsync(owner(context), id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName($"Delete{prefix}NotificationChannel")
            .WithDescription($"{admin}Removes one of {whose}.");
        group.MapPost("/{id:long}/test", async Task<Ok<NotificationChannel>> (long id, ChannelService channels, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await channels.TestAsync(owner(context), id, cancellationToken)))
            .WithName($"Test{prefix}NotificationChannel")
            .WithDescription($"{admin}Sends a test message through one of {whose} now, also when it is off. A failure answers 502 notifications.channel.failed with the server's words and is kept on the channel.");
    }
}
