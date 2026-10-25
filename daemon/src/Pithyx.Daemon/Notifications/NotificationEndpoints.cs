using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pithyx.Daemon.Auth;

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
