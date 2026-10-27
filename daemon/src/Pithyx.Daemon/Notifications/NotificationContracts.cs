using System.Text.Json;

namespace Pithyx.Daemon.Notifications;

/// <summary>How a notification looks: the color of its card and toast.</summary>
public enum NotificationTone
{
    Info,
    Success,
    Warning,
    Critical,
}

/// <summary>
/// An entry of a user's notification history. The text is not part of it: the desktop translates <c>code</c> with
/// <c>params</c> from the <c>notifications</c> namespace (<c>&lt;code&gt;.title</c>, <c>.body</c>).
/// </summary>
/// <param name="Id">Stable id; never reused.</param>
/// <param name="Code">
/// Message code, for example <c>jobs.job.failed</c>, <c>storage.alert.poolDegraded</c>,
/// <c>storage.alert.resolved</c> or <c>app.message</c> (an app's own plain text in <c>params.title</c> and <c>params.body</c>).
/// </param>
/// <param name="Params">Message parameters: strings and numbers, plus nested objects where a code says so.</param>
/// <param name="Tone">How the desktop shows it.</param>
/// <param name="App">Id of the app that sent it; null for the system.</param>
/// <param name="CreatedAt">When it was created (UTC).</param>
/// <param name="ReadAt">When the user read it (UTC); null while unread.</param>
public sealed record Notification(
    long Id,
    string Code,
    IReadOnlyDictionary<string, JsonElement> Params,
    NotificationTone Tone,
    string? App,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>Snapshot of the <c>notifications</c> topic and answer of <c>GET /api/notifications</c>.</summary>
/// <param name="Items">The signed-in user's notifications, newest first; at most 200, none older than 30 days.</param>
public sealed record NotificationList(IReadOnlyList<Notification> Items);

/// <summary>What changed in a user's notifications.</summary>
public enum NotificationChange
{
    /// <summary><c>notification</c> is new; <c>ids</c> lists older ones dropped to stay within 200.</summary>
    Created,

    /// <summary>The notifications in <c>ids</c> were read at <c>readAt</c>.</summary>
    Read,

    /// <summary>The notifications in <c>ids</c> were deleted: dismissed, cleared or expired.</summary>
    Deleted,
}

/// <summary>Event of the <c>notifications</c> topic. Only the user whose notifications changed receives it.</summary>
/// <param name="Change">What happened.</param>
/// <param name="Notification">The new notification of a <c>created</c> event.</param>
/// <param name="Ids">The notifications read or deleted; for <c>created</c> the ones dropped.</param>
/// <param name="ReadAt">When the notifications of a <c>read</c> event were read (UTC).</param>
public sealed record NotificationEvent(NotificationChange Change, Notification? Notification, IReadOnlyList<long> Ids, DateTimeOffset? ReadAt);

/// <summary>Body of <c>POST /api/notifications/read</c>.</summary>
/// <param name="Ids">The notifications to mark as read; null or missing marks every unread one.</param>
public sealed record MarkNotificationsReadRequest(IReadOnlyList<long>? Ids = null);

/// <summary>Body of <c>POST /api/notifications/app</c>: a notification a core app sends through the SDK.</summary>
/// <param name="App">Id of the core app (<c>org.pithyx.*</c>); third-party apps send theirs to <c>/api/app/notifications</c>.</param>
/// <param name="Title">Plain text from the app, at most 200 characters.</param>
/// <param name="Body">Plain text from the app, at most 1000 characters.</param>
/// <param name="Tone">Defaults to <c>info</c>.</param>
public sealed record AppNotificationRequest(string App, string Title, string? Body = null, NotificationTone? Tone = null);

/// <summary>A notification to create: message code, parameters, tone and the app that sent it (null for the system).</summary>
public sealed record NotificationDraft(string Code, IReadOnlyDictionary<string, object?> Params, NotificationTone Tone, string? App = null);

/// <summary>How serious an app's notification is; <c>error</c> shows as critical.</summary>
public enum AppNotificationLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A button of an app's notification.</summary>
/// <param name="Id">Comes back to the app when the user clicks it; 1 to 64 characters, unique in the notification.</param>
/// <param name="Label">Plain text from the app, 1 to 40 characters.</param>
public sealed record AppNotificationAction(string Id, string Label);

/// <summary>Body of <c>POST /api/app/notifications</c>: the SDK's <c>notify</c> of a third-party app.</summary>
/// <param name="Title">Plain text, 1 to 200 characters.</param>
/// <param name="Body">Plain text, at most 1000 characters.</param>
/// <param name="Level">Defaults to <c>info</c>.</param>
/// <param name="Actions">At most 2 buttons.</param>
/// <param name="Data">Any JSON the app gets back with a click, at most 4096 characters.</param>
/// <param name="Tag">Replaces the app's earlier notification with the same tag for this user; 1 to 64 characters.</param>
/// <param name="Category">One of the manifest's <c>notifications.categories</c> (D390); a user who muted it gets the notification as read, without a toast or channel.</param>
public sealed record AppNotifyRequest(
    string Title,
    string? Body = null,
    AppNotificationLevel? Level = null,
    IReadOnlyList<AppNotificationAction>? Actions = null,
    JsonElement? Data = null,
    string? Tag = null,
    string? Category = null);

/// <summary>One kind of notification an app the user may open sends (D390).</summary>
/// <param name="App">The app's id.</param>
/// <param name="AppName">The app's name by language.</param>
/// <param name="Id">The category's id in the manifest.</param>
/// <param name="Name">Its name by language.</param>
/// <param name="Description">Its description by language, if any.</param>
/// <param name="Muted">The user muted it.</param>
public sealed record AppNotificationCategory(
    string App,
    IReadOnlyDictionary<string, string> AppName,
    string Id,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string>? Description,
    bool Muted);

/// <summary>The notification categories of the apps the user may open, by app and in manifest order.</summary>
public sealed record AppNotificationCategoryList(IReadOnlyList<AppNotificationCategory> Categories);

/// <summary>Body of <c>PUT /api/notifications/categories/{app}/{category}</c>.</summary>
/// <param name="Muted">Whether the user mutes the category.</param>
public sealed record NotificationCategoryMuteRequest(bool Muted);
