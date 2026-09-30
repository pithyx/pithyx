using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Notifications;

/// <summary>
/// Every user's notifications in memory, loaded from SQLite on first use, for the per-user <c>notifications</c> topic:
/// its snapshot and events are shaped per subscriber, which needs the data without a database read. Only
/// <see cref="NotificationService"/> changes it, after its database write. It needs nothing that depends on
/// <see cref="TopicHub"/>.
/// </summary>
public sealed class NotificationStore(SqliteConnectionFactory connections) : IDisposable
{
    public const string Topic = "notifications";

    internal const string SelectColumns =
        "SELECT id AS Id, user_id AS UserId, code AS Code, params AS Params, tone AS Tone, app AS App, created_at AS CreatedAt, read_at AS ReadAt FROM notifications";

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _loading = new(1, 1);
    private Dictionary<long, List<Notification>>? _byUser;

    /// <summary>Reads every notification once; later calls return at once.</summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _byUser) is not null)
        {
            return;
        }
        await _loading.WaitAsync(cancellationToken);
        try
        {
            if (_byUser is not null)
            {
                return;
            }
            IEnumerable<NotificationRow> rows;
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                rows = await connection.QueryAsync<NotificationRow>(SelectColumns + " ORDER BY id DESC");
            }
            var byUser = rows.GroupBy(row => row.UserId).ToDictionary(group => group.Key, group => group.Select(ToNotification).ToList());
            Volatile.Write(ref _byUser, byUser);
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>The user's notifications, newest first. Empty before <see cref="LoadAsync"/>.</summary>
    public IReadOnlyList<Notification> Of(long userId)
    {
        lock (_lock)
        {
            return _byUser is { } byUser && byUser.TryGetValue(userId, out var list) ? [.. list] : [];
        }
    }

    public TopicDefinition Definition() => new(
        Topic,
        typeof(NotificationList),
        typeof(NotificationEvent),
        async (_, cancellationToken) =>
        {
            await LoadAsync(cancellationToken);
            return EveryUser.Instance;
        },
        TopicDefinition.AnyUser,
        ForUser: ForUser);

    public void Dispose() => _loading.Dispose();

    internal void Add(long userId, Notification notification, IReadOnlyCollection<long> dropped)
    {
        lock (_lock)
        {
            if (_byUser is not { } byUser)
            {
                return;
            }
            if (!byUser.TryGetValue(userId, out var list))
            {
                byUser[userId] = list = [];
            }
            list.Insert(0, notification);
            list.RemoveAll(entry => dropped.Contains(entry.Id));
        }
    }

    internal void MarkRead(long userId, IReadOnlyCollection<long> ids, DateTimeOffset readAt)
    {
        lock (_lock)
        {
            if (_byUser is { } byUser && byUser.TryGetValue(userId, out var list))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (ids.Contains(list[i].Id))
                    {
                        list[i] = list[i] with { ReadAt = readAt };
                    }
                }
            }
        }
    }

    internal void Remove(long userId, IReadOnlyCollection<long> ids)
    {
        lock (_lock)
        {
            if (_byUser is { } byUser && byUser.TryGetValue(userId, out var list))
            {
                list.RemoveAll(entry => ids.Contains(entry.Id));
            }
        }
    }

    internal static Notification ToNotification(NotificationRow row) => new(
        row.Id,
        row.Code,
        ParamsFrom(row.Params),
        ParseTone(row.Tone),
        row.App,
        SessionService.Parse(row.CreatedAt),
        row.ReadAt is null ? null : SessionService.Parse(row.ReadAt));

    internal static string ToneText(NotificationTone tone) => tone switch
    {
        NotificationTone.Info => "info",
        NotificationTone.Success => "success",
        NotificationTone.Warning => "warning",
        NotificationTone.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    private static NotificationTone ParseTone(string text) => text switch
    {
        "info" => NotificationTone.Info,
        "success" => NotificationTone.Success,
        "warning" => NotificationTone.Warning,
        "critical" => NotificationTone.Critical,
        _ => throw new InvalidOperationException($"Unknown notification tone {text}."),
    };

    private static Dictionary<string, JsonElement> ParamsFrom(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private object? ForUser(ClaimsPrincipal user, object data)
    {
        if (!long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
        {
            return null;
        }
        return data switch
        {
            EveryUser => new NotificationList(Of(userId)),
            UserEvent change when change.UserId == userId => change.Event,
            _ => null,
        };
    }

    /// <summary>The snapshot before it is shaped: every user's list, of which each subscriber gets their own.</summary>
    private sealed class EveryUser
    {
        public static readonly EveryUser Instance = new();
    }

    // Properties instead of a constructor: Dapper cannot map a constructor when a nullable column is NULL in every row.
    internal sealed class NotificationRow
    {
        public long Id { get; init; }
        public long UserId { get; init; }
        public string Code { get; init; } = "";
        public string Params { get; init; } = "{}";
        public string Tone { get; init; } = "";
        public string? App { get; init; }
        public string CreatedAt { get; init; } = "";
        public string? ReadAt { get; init; }
    }
}

/// <summary>A change of one user's notifications as published; the topic hands only that user the event.</summary>
internal sealed record UserEvent(long UserId, NotificationEvent Event);
