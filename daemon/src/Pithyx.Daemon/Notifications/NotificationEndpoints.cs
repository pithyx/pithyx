using Microsoft.AspNetCore.Http.HttpResults;

namespace Pithyx.Daemon.Notifications;

/// <summary>The signed-in user's notification history. Everyone reads and changes only their own notifications.</summary>
public static class NotificationEndpoints
{
    public static IServiceCollection AddPithyxNotifications(this IServiceCollection services)
    {
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
    }
}
