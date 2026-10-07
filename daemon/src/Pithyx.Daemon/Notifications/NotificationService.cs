using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Notifications;

/// <summary>
/// The users' notification history. Modules create notifications for one user or for every administrator; users read,
/// mark and delete only their own. Every change is written to SQLite first, then applied to
/// <see cref="NotificationStore"/> and published to that user alone on the <c>notifications</c> topic.
/// </summary>
public sealed partial class NotificationService : IDisposable
{
    /// <summary>A user keeps at most this many; a new one drops the oldest.</summary>
    public const int MaxPerUser = 200;

    /// <summary>Notifications are deleted this long after they were created.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 1000;
    public const int MaxActions = 2;
    public const int MaxActionIdLength = 64;
    public const int MaxActionLabelLength = 40;
    public const int MaxDataLength = 4096;
    public const int MaxTagLength = 64;

    /// <summary>The code of a notification an app sent, with its plain text in <c>title</c> and <c>body</c>.</summary>
    public const string AppMessageCode = "app.message";

    private readonly NotificationStore _store;
    private readonly SqliteConnectionFactory _connections;
    private readonly TopicHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationService> _logger;
    private readonly JsonSerializerOptions _storage;

    // One writer at a time, so memory and the published events follow the order of the database writes.
    private readonly SemaphoreSlim _writes = new(1, 1);

    public NotificationService(NotificationStore store, SqliteConnectionFactory connections, TopicHub hub, TimeProvider time, IOptions<JsonOptions> json, ILogger<NotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(json);
        _store = store;
        _connections = connections;
        _hub = hub;
        _time = time;
        _logger = logger;
        _storage = new JsonSerializerOptions(json.Value.SerializerOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    }

    /// <summary>Creates a notification for one user and publishes it to them.</summary>
    public Task<Notification> CreateAsync(long userId, NotificationDraft draft, CancellationToken cancellationToken) =>
        CreateAsync(userId, draft, null, cancellationToken);

    /// <summary>
    /// Creates a notification and, in the same write, deletes the user's notifications <paramref name="replaces"/>
    /// picks, so two writers never both keep theirs.
    /// </summary>
    private async Task<Notification> CreateAsync(long userId, NotificationDraft draft, Func<Notification, bool>? replaces, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrEmpty(draft.Code);
        var parameters = JsonSerializer.Serialize(draft.Params, _storage);
        await _store.LoadAsync(cancellationToken);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            long id;
            List<long> replaced = replaces is null ? [] : [.. _store.Of(userId).Where(replaces).Select(entry => entry.Id)];
            List<long> dropped;
            // One transaction: a failed trim must not leave a row that memory and the topic never saw.
            await using (var connection = await _connections.OpenAsync(cancellationToken))
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                if (replaced.Count > 0)
                {
                    await connection.ExecuteAsync("DELETE FROM notifications WHERE user_id = @userId AND id IN @replaced", new { userId, replaced }, transaction);
                }
                id = await connection.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO notifications (user_id, code, params, tone, app, created_at)
                    VALUES (@userId, @Code, @parameters, @tone, @App, @createdAt)
                    RETURNING id
                    """,
                    new { userId, draft.Code, parameters, tone = NotificationStore.ToneText(draft.Tone), draft.App, createdAt = SessionService.Format(now) },
                    transaction);
                dropped = [.. await connection.QueryAsync<long>(
                    """
                    DELETE FROM notifications
                    WHERE user_id = @userId AND id NOT IN (SELECT id FROM notifications WHERE user_id = @userId ORDER BY id DESC LIMIT @limit)
                    RETURNING id
                    """,
                    new { userId, limit = MaxPerUser },
                    transaction)];
                await transaction.CommitAsync(cancellationToken);
            }
            dropped = [.. replaced, .. dropped];
            var notification = NotificationStore.ToNotification(new NotificationStore.NotificationRow
            {
                Id = id,
                UserId = userId,
                Code = draft.Code,
                Params = parameters,
                Tone = NotificationStore.ToneText(draft.Tone),
                App = draft.App,
                CreatedAt = SessionService.Format(now),
            });
            _store.Add(userId, notification, dropped);
            _hub.Publish(NotificationStore.Topic, new UserEvent(userId, new NotificationEvent(NotificationChange.Created, notification, dropped, null)));
            LogCreated(id, draft.Code, userId);
            return notification;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Creates the same notification for every administrator. Returns how many there are.</summary>
    public async Task<int> CreateForAdministratorsAsync(NotificationDraft draft, CancellationToken cancellationToken)
    {
        List<long> administrators;
        await using (var connection = await _connections.OpenAsync(cancellationToken))
        {
            // Deactivated administrators too: they read it when they are back.
            administrators = [.. (await Permissions.PermissionService.AllUsersAsync(connection, null, asIfActive: true)).Where(user => user.Permissions.IsAdmin).Select(user => user.Id).Order()];
        }
        foreach (var administrator in administrators)
        {
            await CreateAsync(administrator, draft, cancellationToken);
        }
        return administrators.Count;
    }

    /// <summary>
    /// What a core app sends through the SDK: its own plain text, stored as <see cref="AppMessageCode"/>. The desktop names
    /// the core app; third-party apps go through <see cref="CreateForAppAsync"/>, where the app comes from its token.
    /// </summary>
    public Task<Notification> CreateFromAppAsync(ClaimsPrincipal user, AppNotificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.App is null || !AppId.IsValid(request.App))
        {
            throw Invalid("app", AppId.MaxLength);
        }
        if (!Apps.CoreApps.All.Contains(request.App))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
        var (title, body) = Texts(request.Title, request.Body);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = title, ["body"] = body };
        return CreateAsync(CurrentUser.Id(user), new NotificationDraft(AppMessageCode, parameters, request.Tone ?? NotificationTone.Info, request.App), cancellationToken);
    }

    /// <summary>
    /// What a third-party app sends through the SDK for the user its token names: plain text, at most two buttons, data
    /// that comes back with a click, and a tag that replaces the app's earlier notification with the same tag. All of
    /// it is kept in the params of <see cref="AppMessageCode"/> (<c>title</c>, <c>body</c>, <c>actions</c>, <c>data</c>,
    /// <c>tag</c>).
    /// </summary>
    public async Task<Notification> CreateForAppAsync(long userId, string appId, AppNotifyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (title, body) = Texts(request.Title, request.Body);
        var actions = request.Actions ?? [];
        if (actions.Count > MaxActions)
        {
            throw Invalid("actions", MaxActions);
        }
        foreach (var action in actions)
        {
            if (action?.Id is not { Length: > 0 and <= MaxActionIdLength } || action.Label?.Trim() is not { Length: > 0 and <= MaxActionLabelLength })
            {
                throw Invalid("actions", MaxActionLabelLength);
            }
        }
        if (actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != actions.Count)
        {
            throw Invalid("actions", MaxActions);
        }
        if (request.Data is { } data && data.GetRawText().Length > MaxDataLength)
        {
            throw Invalid("data", MaxDataLength);
        }
        if (request.Tag is { } tag && tag.Length is 0 or > MaxTagLength)
        {
            throw Invalid("tag", MaxTagLength);
        }
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["body"] = body,
            ["actions"] = actions.Select(action => new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = action.Id, ["label"] = action.Label.Trim() }).ToList(),
            ["data"] = request.Data is { ValueKind: not JsonValueKind.Null } given ? given : null,
            ["tag"] = request.Tag,
        };
        var tone = request.Level switch
        {
            AppNotificationLevel.Success => NotificationTone.Success,
            AppNotificationLevel.Warning => NotificationTone.Warning,
            AppNotificationLevel.Error => NotificationTone.Critical,
            _ => NotificationTone.Info,
        };
        Func<Notification, bool>? sameTag = request.Tag is not { } tagged
            ? null
            : entry => entry.App == appId && entry.Code == AppMessageCode && entry.Params.TryGetValue("tag", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == tagged;
        return await CreateAsync(userId, new NotificationDraft(AppMessageCode, parameters, tone, appId), sameTag, cancellationToken);
    }

    private static (string Title, string Body) Texts(string? requestTitle, string? requestBody)
    {
        var title = requestTitle?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitleLength)
        {
            throw Invalid("title", MaxTitleLength);
        }
        var body = requestBody?.Trim() ?? "";
        if (body.Length > MaxBodyLength)
        {
            throw Invalid("body", MaxBodyLength);
        }
        return (title, body);
    }

    /// <summary>The signed-in user's notifications, newest first.</summary>
    public async Task<NotificationList> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userId = CurrentUser.Id(user);
        await _store.LoadAsync(cancellationToken);
        return new NotificationList(_store.Of(userId));
    }

    /// <summary>Marks the user's notifications as read: those in <paramref name="ids"/>, or every unread one when null.</summary>
    public async Task MarkReadAsync(ClaimsPrincipal user, IReadOnlyList<long>? ids, CancellationToken cancellationToken)
    {
        var userId = CurrentUser.Id(user);
        await _store.LoadAsync(cancellationToken);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            var unread = _store.Of(userId).Where(entry => entry.ReadAt is null && (ids is null || ids.Contains(entry.Id))).Select(entry => entry.Id).ToList();
            if (unread.Count == 0)
            {
                return;
            }
            await using (var connection = await _connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    "UPDATE notifications SET read_at = @readAt WHERE user_id = @userId AND id IN @unread AND read_at IS NULL",
                    new { userId, unread, readAt = SessionService.Format(now) });
            }
            _store.MarkRead(userId, unread, now);
            _hub.Publish(NotificationStore.Topic, new UserEvent(userId, new NotificationEvent(NotificationChange.Read, null, unread, now)));
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Deletes one of the user's notifications. Someone else's answers like a missing one.</summary>
    public async Task DeleteAsync(ClaimsPrincipal user, long id, CancellationToken cancellationToken)
    {
        var userId = CurrentUser.Id(user);
        await _store.LoadAsync(cancellationToken);
        if (!_store.Of(userId).Any(entry => entry.Id == id))
        {
            throw new PithyxException(ErrorCodes.NotificationsNotificationNotFound);
        }
        await RemoveAsync(userId, [id], cancellationToken);
    }

    /// <summary>Deletes every notification of the user.</summary>
    public async Task ClearAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userId = CurrentUser.Id(user);
        await _store.LoadAsync(cancellationToken);
        await RemoveAsync(userId, [.. _store.Of(userId).Select(entry => entry.Id)], cancellationToken);
    }

    /// <summary>Deletes notifications older than <see cref="Lifetime"/>. Returns how many were deleted.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await _store.LoadAsync(cancellationToken);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            List<(long UserId, long Id)> deleted;
            await using (var connection = await _connections.OpenAsync(cancellationToken))
            {
                deleted = [.. await connection.QueryAsync<(long UserId, long Id)>(
                    "DELETE FROM notifications WHERE created_at <= @cutoff RETURNING user_id, id",
                    new { cutoff = SessionService.Format(_time.GetUtcNow() - Lifetime) })];
            }
            foreach (var group in deleted.GroupBy(entry => entry.UserId))
            {
                List<long> ids = [.. group.Select(entry => entry.Id)];
                _store.Remove(group.Key, ids);
                _hub.Publish(NotificationStore.Topic, new UserEvent(group.Key, new NotificationEvent(NotificationChange.Deleted, null, ids, null)));
            }
            return deleted.Count;
        }
        finally
        {
            _writes.Release();
        }
    }

    private async Task RemoveAsync(long userId, List<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }
        await _writes.WaitAsync(cancellationToken);
        try
        {
            await using (var connection = await _connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync("DELETE FROM notifications WHERE user_id = @userId AND id IN @ids", new { userId, ids });
            }
            _store.Remove(userId, ids);
            _hub.Publish(NotificationStore.Topic, new UserEvent(userId, new NotificationEvent(NotificationChange.Deleted, null, ids, null)));
        }
        finally
        {
            _writes.Release();
        }
    }

    public void Dispose() => _writes.Dispose();

    private static PithyxException Invalid(string field, int maxLength) =>
        new(ErrorCodes.NotificationsMessageInvalid, new Dictionary<string, object?> { ["field"] = field, ["maxLength"] = maxLength });

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationId} {Code} created for user {UserId}")]
    private partial void LogCreated(long notificationId, string code, long userId);
}
